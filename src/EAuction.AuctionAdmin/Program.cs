using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Outbox;
using EAuction.AuctionAdmin.Persistence;
using EAuction.Core;
using EAuction.Outbox;
using EAuction.Security;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("Admin")
    ?? "Host=localhost;Database=eauction;Username=eauction;Password=eauction";

builder.Services.AddDbContextFactory<AdminDbContext>(o => o.UseNpgsql(connectionString));

// The same master key the participant service and the catcher hold. This service
// needs it for one thing only: handing a hall clerk the key they sign frames with
// (§29). Derived, never stored and never published — the participants topic
// carries the clerk's epoch, exactly as it carries a bidder's.
var clerkMasterKeyHex = builder.Configuration["Admin:BidderMasterKeyHex"];
if (string.IsNullOrWhiteSpace(clerkMasterKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Admin:BidderMasterKeyHex is required. Without the same master key the catcher "
            + "uses, no clerk could sign a bid the catcher would accept.");

    clerkMasterKeyHex = Convert.ToHexString(BidderKeys.NewMasterKey());
}

var clerkMasterKey = Convert.FromHexString(clerkMasterKeyHex);

// The key the document service verifies grants with. Held here because the rule
// for an award letter is this service's — did this bidder win — and the document
// service must not have to learn what an award is.
var documentGrantKeyHex = builder.Configuration["Documents:GrantKeyHex"];
if (string.IsNullOrWhiteSpace(documentGrantKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Documents:GrantKeyHex is required, and must match the document service's. "
            + "Without it a winner cannot open their own award letter.");

    documentGrantKeyHex = Convert.ToHexString(DocumentGrants.NewKey());
}
var documentGrantKey = Convert.FromHexString(documentGrantKeyHex);

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];
if (string.IsNullOrWhiteSpace(bootstrap))
{
    builder.Services.AddSingleton<ITopicPublisher, InMemoryTopicPublisher>();
}
else
{
    builder.Services.AddSingleton<ITopicPublisher>(sp => new KafkaTopicPublisher(
        new KafkaPublisherOptions
        {
            BootstrapServers = bootstrap,
            ReplicationFactor = (short)builder.Configuration.GetValue("Kafka:ReplicationFactor", 3)
            // BidTopicPartitions is deliberately not configurable: a bid topic
            // is one auction's ordering domain and must have exactly one
            // partition. Exposing it only invites someone to break an auction.
        },
        sp.GetRequiredService<ILogger<KafkaTopicPublisher>>()));
}

builder.Services.AddSingleton<IOutboxRouter, AuctionOutboxRouter>();
builder.Services.AddSingleton<OutboxRelay<AdminDbContext>>();
builder.Services.AddHostedService<OutboxRelayService<AdminDbContext>>();

// Admin's half of the loop with the bid processor.
builder.Services.AddSingleton<IEventStream>(sp =>
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "auction-admin"
        }));
builder.Services.AddSingleton<LifecycleConsumer>();
builder.Services.AddHostedService<LifecycleConsumerService>();

// Compliance window before a winner is disqualified and the award cascades.
// A contract term, not a tuning knob: it has to match the كراسة الشروط, which
// bidders accept (§11, C-1).
var complianceWindow = TimeSpan.FromDays(
    builder.Configuration.GetValue("Award:ComplianceWindowDays", 5));

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);
builder.Services.AddEAuctionStepUp(builder.Configuration);
builder.Services.AddEAuctionCors(builder.Configuration);

// Enums as names, both ways. Responses already hand back "Online" and "Eligible" as
// strings, so without this a portal cannot PUT back what it just read: the request
// side would only accept the ordinal. The converter still accepts numbers, so this
// widens the contract rather than changing it.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseEAuctionCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok("ok")).AllowAnonymous();
app.MapGet("/health/ready", async (IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(503);
}).AllowAnonymous();

// --- إعداد المزاد : auction preparation ------------------------------------

app.MapPost("/auctions", async (
    CreateAuctionRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var auction = Auction.CreateDraft(r.CreatedByUserId, r.NameAr, r.NameEn);
    db.Auctions.Add(auction);

    // Outside Mutate because the auction does not exist to be loaded yet, so the
    // audit row is written by hand. The transaction is the same one, which is what
    // matters.
    var (who, roles, source) = StaffAudit.ActorOf(http);
    db.RecordStaffAction(
        who, roles, source, "CreateAuctionDraft", AuditSubject.Auction(auction.Id), r.NameAr);

    await db.SaveChangesAsync(ct);
    return Results.Created($"/auctions/{auction.Id}", AuctionResponse.From(auction));
})
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPut("/auctions/{id:guid}", (
    Guid id, UpdateAuctionRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "UpdateAuctionDetails", a => a.UpdateDetails(
        r.NameAr, r.NameEn, r.Channel, r.BidderVisibility, r.StartsAt, r.EndsAt,
        r.OpeningPriceMinorUnits, r.ReservePriceMinorUnits, r.MinIncrementMinorUnits,
        r.DepositMinorUnits, r.BrokerageFeePercent, r.BookletPriceMinorUnits,
        r.QuietPeriodSeconds, r.MaxExtensions, r.Phase),
        // That the reserve moved, and not what it moved to or from.
        //
        // The one entry in the whole trail where the obvious summary is the wrong
        // one. "Reserve changed from 1,200,000 to 1,400,000" would put السعر
        // الاحتياطي — the single figure the outcome of an auction turns on, kept off
        // every other topic by D-23 — onto a topic, in a row, in a different
        // service's database, for ever. An auditor who needs the figure asks the
        // auction service; what they need from here is that somebody changed it, and
        // who.
        details: r.ReservePriceMinorUnits is null
            ? null
            : "The reserve price was changed (the figure is deliberately not recorded here)."))
    .RequireAuthorization(Policies.AuctionAdmin);

// --- قاعة المزاد: the clerk on the floor (§29) ------------------------------

app.MapPut("/auctions/{id:guid}/clerk", (
    Guid id, AssignClerkRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "AssignClerk", a => a.AssignClerk(r.ClerkUserId),
        details: $"Clerk {r.ClerkUserId}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapDelete("/auctions/{id:guid}/clerk", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "UnassignClerk", a => a.UnassignClerk()))
    .RequireAuthorization(Policies.AuctionAdmin);

// The clerk's own signing key. Derived on demand like a bidder's, and handed to
// nobody else: whoever holds it can sign a bid for any eligible bidder in that
// auction, which is exactly the clerk's job and nobody else's.
app.MapGet("/auctions/{id:guid}/clerk-key", async (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id, ct);
    if (auction is null) return Results.NotFound();

    // Not "an operator": this auction's operator. A clerk running the hall next
    // door has the same role and no business signing here.
    if (auction.ClerkUserId is null || auction.ClerkUserId != http.User.SubjectId())
        return Results.Forbid();

    // Audited, although it is a GET and changes nothing.
    //
    // The only read in this service that is recorded, and it earns it: whoever
    // holds this value can sign a bid for any eligible bidder in the auction, so
    // "who collected the hall's signing key, and when" is exactly the question
    // asked after a disputed hall auction. Writing on a GET is the smaller
    // oddity — the audit row *is* a state change, and the alternative is the one
    // secret this service hands out leaving no trace.
    var (who, roles, source) = StaffAudit.ActorOf(http);
    db.RecordStaffAction(
        who, roles, source, "ReadClerkSigningKey", AuditSubject.Auction(id),
        $"Key epoch {auction.ClerkKeyEpoch}.");
    await db.SaveChangesAsync(ct);

    return Results.Ok(new SigningKeyResponse(
        Convert.ToHexString(
            BidderKeys.Derive(clerkMasterKey, id, auction.ClerkUserId.Value, auction.ClerkKeyEpoch)),
        auction.ClerkKeyEpoch));
}).RequireAuthorization(Policies.Operator);

app.MapPost("/auctions/{id:guid}/extend", (
    Guid id, HttpContext http, ExtendRequest r,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "ExtendAuction",
        a => a.ExtendByClerk(http.User.SubjectId() ?? Guid.Empty, r.Seconds),
        details: $"By {r.Seconds} seconds."))
    .RequireAuthorization(Policies.Operator);

app.MapPost("/auctions/{id:guid}/close", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "CloseAuction",
        a => a.CloseByClerk(http.User.SubjectId() ?? Guid.Empty)))
    .RequireAuthorization(Policies.Operator);

app.MapPost("/auctions/{id:guid}/plots", (
    Guid id, AddPlotRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "AddPlot", a => a.AddPlot(new Plot(
        id, r.DeedNumber, r.AreaSqm, r.Latitude, r.Longitude, r.DescriptionAr, r.DescriptionEn)),
        details: $"Deed {r.DeedNumber}, {r.AreaSqm} m²."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapDelete("/auctions/{id:guid}/plots/{plotId:guid}", (
    Guid id, Guid plotId, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "RemovePlot", a => a.RemovePlot(plotId),
        details: $"Plot {plotId}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/booklet", (
    Guid id, DocumentRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "AttachBooklet", a => a.AttachBooklet(r.DocumentId),
        details: $"Document {r.DocumentId}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/cover-image", (
    Guid id, DocumentRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "AttachCoverImage", a => a.AttachCoverImage(r.DocumentId),
        details: $"Document {r.DocumentId}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/attachments", (
    Guid id, AttachmentRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "AddPublicDocument", a => a.AddAttachment(r.DocumentId, r.TitleAr),
        details: $"Document {r.DocumentId}: {r.TitleAr}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapDelete("/auctions/{id:guid}/attachments/{documentId:guid}", (
    Guid id, Guid documentId, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "RemovePublicDocument", a => a.RemoveAttachment(documentId),
        details: $"Document {documentId}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapGet("/auctions/{id:guid}/validation", async (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var auction = await Load(db, id, ct);
    return auction is null
        ? Results.NotFound()
        : Results.Ok(new { problems = auction.Validate(DateTimeOffset.UtcNow) });
})
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/submit", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "SubmitAuctionForReview",
        a => a.SubmitForReview(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/approve", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "ApproveAuction", a => a.Approve(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/reject", (
    Guid id, RejectRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "RejectAuction", a => a.Reject(r.Reason), details: r.Reason))
    .RequireAuthorization(Policies.AwardCommittee);

// Withdrawing an approved auction before it opens. The auction manager's call, as
// preparing it was, and audited with its reason — «توثيق الإلغاء المصرح به».
app.MapPost("/auctions/{id:guid}/cancel", (
    Guid id, RejectRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "CancelAuction",
        a => a.Cancel(r.Reason, http.User.SubjectId() ?? Guid.Empty, DateTimeOffset.UtcNow),
        details: r.Reason))
    .RequireAuthorization(Policies.AuctionAdmin);

// --- lifecycle --------------------------------------------------------------
//
// Temporary. These transitions belong to the bid processor and should arrive
// over auctions.lifecycle; they are exposed here so the award workflow is
// reachable and operable before that consumer exists.

app.MapPost("/auctions/{id:guid}/lifecycle/{transition}", (
    Guid id, string transition, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "ChangeAuctionLifecycle", a =>
    {
        switch (transition.ToLowerInvariant())
        {
            case "scheduled": a.MarkScheduled(); break;
            case "live": a.MarkLive(); break;
            case "closing": a.MarkClosing(); break;
            case "eligibility-review": a.MarkPendingEligibilityReview(); break;
            default: throw new AuctionValidationException(
                new[] { $"Unknown lifecycle transition '{transition}'." });
        }
    }, details: $"To {transition}."))
    .RequireAuthorization(Policies.AuctionAdmin);

// --- الترسية : award workflow ----------------------------------------------

app.MapPost("/auctions/{id:guid}/candidate", (
    Guid id, OfferCandidateRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "OfferAwardCandidate",
        a => a.OfferCandidate(r.BidderId, r.AmountMinorUnits),
        // The amount, unlike the reserve, is a bid: it reaches the public fan-out
        // the moment it is made. Nothing is kept secret by leaving it out here, and
        // an award entry that did not say for how much would be useless.
        details: $"Bidder {r.BidderId} at {r.AmountMinorUnits} halalas."))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award", (
    Guid id, ConfirmAwardRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "ConfirmAward",
        a => a.ConfirmAward(r.CommitteeUserId, DateTimeOffset.UtcNow, complianceWindow)))
    // The single most consequential act in the platform: it transfers a parcel of
    // state land to a named person. A committee member's role is not enough on its
    // own — the second factor is what ties the decision to the person, which is
    // what the minutes of an award have to be able to claim.
    .RequireAuthorization(Policies.AwardCommitteeStepUp);

app.MapPost("/auctions/{id:guid}/award/letter", (
    Guid id, DocumentRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "GenerateAwardLetter", a => a.GenerateAwardLetter(r.DocumentId),
        details: $"Document {r.DocumentId}."))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award/signed-letter", (
    Guid id, DocumentRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "UploadSignedAwardLetter",
        a => a.UploadSignedAwardLetter(r.DocumentId), details: $"Document {r.DocumentId}."))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award/notify", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "NotifyWinner", a => a.NotifyWinner(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

// A grant for the winner to read their own award letter.
//
// The letter is Restricted in the document service, which means no role opens it
// — not an administrator's and not the committee's own. That is deliberate: a
// signed خطاب ترسية is the winner's instrument, and "staff can read it" is a much
// larger set of people than anyone would choose if asked.
//
// The signed letter if there is one, otherwise the draft: a winner notified
// before the signature is still entitled to see what they were notified about.
app.MapGet("/auctions/{id:guid}/award/letter-grant", async (
    HttpContext http, Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    var subject = http.User.SubjectId();
    if (subject is null) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);
    var auction = await db.Auctions
        .Include(a => a.Awards)
        .FirstOrDefaultAsync(a => a.Id == id, ct);

    if (auction is null) return Results.NotFound();

    var award = auction.CurrentAward;

    // Theirs, and only theirs. A bidder who lost — or who was disqualified and
    // replaced by the cascade — is not the holder of this letter.
    if (award is null || award.BidderId != subject.Value) return Results.NotFound();

    var documentId = award.SignedLetterDocumentId ?? award.LetterDocumentId;
    if (documentId is null)
        return Results.NotFound(new { error = "No award letter has been issued yet." });

    return Results.Ok(new DocumentGrantResponse(
        documentId.Value,
        DocumentGrants.Mint(
            documentGrantKey, documentId.Value, subject.Value, DateTimeOffset.UtcNow),
        DateTimeOffset.UtcNow.Add(DocumentGrants.Lifetime)));
}).RequireAuthorization(Policies.Bidder);

app.MapPost("/auctions/{id:guid}/award/disqualify", (
    Guid id, DisqualifyRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "DisqualifyWinner",
        a => a.DisqualifyWinner(r.Reason, r.ForfeitDeposit, DateTimeOffset.UtcNow),
        details: r.ForfeitDeposit
            ? $"Deposit forfeited. {r.Reason}"
            : $"Deposit returned. {r.Reason}"))
    .RequireAuthorization(Policies.AwardCommittee);

// After a winner is disqualified, the committee — not the system — decides whether
// the next bidder is put up for award.
app.MapPost("/auctions/{id:guid}/next-bidder", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "ReferToNextBidder", a => a.ReferToNextBidder()))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/result/reject", (
    Guid id, RejectRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "RejectPreliminaryResult", a => a.RejectResult(r.Reason),
        details: r.Reason))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/unsold", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "MarkAuctionUnsold", a => a.MarkUnsold()))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/settle", (
    Guid id, HttpContext http, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "SettleAuction", a => a.Settle(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

// The list the portal opens on. Staff-only: it carries every auction including
// drafts and rejections, which is a different thing entirely from the public
// catalogue the query BFF serves.
// --- award follow-up (الخاصية 11) ------------------------------------------
//
// The winner pays the land price, and the title passes at the notary, outside this
// platform; integrating either is out of the first phase. What is in scope is the
// manual record, so these endpoints write entries — each with the reference a person
// can trace it by — and never move money. Recorded by the administrators the
// requirements give settlement to; read by them, the committee and finance.

app.MapPost("/auctions/{id:guid}/award/receipts", (
    Guid id, AwardReceiptRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "RecordAwardPayment",
        a => a.RecordAwardPayment(r.AmountMinorUnits, r.PaidOn, r.Reference, r.DocumentId,
            http.User.SubjectId() ?? Guid.Empty, DateTimeOffset.UtcNow),
        details: $"{r.AmountMinorUnits} halalas, reference {r.Reference}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/award/deposit-credit", (
    Guid id, ReferenceRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "CreditDepositToAward",
        a => a.CreditDepositToAward(r.Reference, http.User.SubjectId() ?? Guid.Empty,
            DateTimeOffset.UtcNow),
        details: $"Deposit counted towards the price, reference {r.Reference}."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/award/transfer", (
    Guid id, TransferRequest r, HttpContext http,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, http, "UpdateTransfer",
        a => a.UpdateTransfer(r.Status, r.Reference, r.DocumentId, DateTimeOffset.UtcNow),
        details: $"Transfer {r.Status}, reference {r.Reference ?? "—"}."))
    .RequireAuthorization(Policies.AuctionAdmin);

// Every award still being followed up, overdue first: unpaid, unsettled, or settled
// with the title not yet transferred. The list «تظهر الترسية غير المسددة» asks for.
app.MapGet("/awards/follow-up", async (
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var auctions = await db.Auctions
        .Include(a => a.Awards)
        .Where(a => a.Status == AuctionStatus.Awarded || a.Status == AuctionStatus.Settled)
        .AsSplitQuery()
        .ToListAsync(ct);

    var now = DateTimeOffset.UtcNow;
    var items = auctions
        .Select(a => (a, award: a.FollowUpAward))
        .Where(x => x.award is not null
                    && !(x.a.Status == AuctionStatus.Settled
                         && x.award.TransferStatus == TransferStatus.Completed))
        .Select(x => new FollowUpEntry(
            x.a.Id, x.a.NameAr, x.a.Status.ToString(), x.a.Phase,
            AwardResponse.From(x.award!, now)))
        .OrderByDescending(e => e.Award.Overdue)
        .ThenBy(e => e.Award.ComplianceDeadline)
        .ToArray();

    return Results.Ok(new { items });
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/auctions", async (
    string? status, int? skip, int? take,
    IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    AuctionStatus? filter = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<AuctionStatus>(status, ignoreCase: true, out var parsed))
            return Results.BadRequest(new
            {
                problems = new[] { $"Unknown status '{status}'." },
                allowed = Enum.GetNames<AuctionStatus>()
            });
        filter = parsed;
    }

    // Bounded, and bounded here rather than trusted from the query string: a
    // portal bug should not be able to ask for every auction ever held.
    var page = Math.Clamp(take ?? 50, 1, 200);
    var offset = Math.Max(0, skip ?? 0);

    await using var db = await f.CreateDbContextAsync(ct);

    var query = db.Auctions.AsNoTracking();
    if (filter is not null) query = query.Where(a => a.Status == filter);

    var total = await query.CountAsync(ct);

    // Newest first, with the id as a tiebreak so paging cannot skip or repeat a
    // row when two auctions share a creation instant.
    var rows = await query
        .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
        .Skip(offset).Take(page)
        .Select(a => new AuctionListItem(
            a.Id, a.Status.ToString(), a.NameAr, a.NameEn, a.Channel.ToString(),
            a.BidderVisibility.ToString(),
            a.StartsAt, a.EndsAt, a.OpeningPriceMinorUnits, a.DepositMinorUnits,
            a.Plots.Count, a.CreatedAt,
            a.BookletPriceMinorUnits, a.CoverImageDocumentId, a.Plots.Sum(p => p.AreaSqm)))
        .ToListAsync(ct);

    return Results.Ok(new { total, skip = offset, take = page, items = rows });
})
    .RequireAuthorization();

app.MapGet("/auctions/{id:guid}", async (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var auction = await Load(db, id, ct);
    return auction is null ? Results.NotFound() : Results.Ok(AuctionResponse.From(auction));
})
    .RequireAuthorization();

app.Run();

// ---------------------------------------------------------------------------

static async Task<Auction?> Load(AdminDbContext db, Guid id, CancellationToken ct) =>
    await db.Auctions
        .Include(a => a.Plots)
        .Include(a => a.Awards)
        .FirstOrDefaultAsync(a => a.Id == id, ct);

/// <summary>
/// Loads, applies the change, records who did it, saves. SaveChanges drains any
/// domain events the aggregate raised into the outbox in the same transaction.
/// </summary>
/// <param name="action">
/// The name this appears under in the audit trail. Required rather than optional,
/// because every state change this service makes is a consequential act by a
/// member of staff and an unaudited one would be a hole whoever found it could
/// use. A new endpoint cannot be added without choosing a name for it.
/// </param>
/// <param name="details">
/// A short summary, composed at the call site because only the call site knows
/// what may be said — see the note on <see cref="StaffActionRecorded.Details"/>
/// and what the reserve price costs if it is got wrong.
/// </param>
/// <remarks>
/// Only successful changes are recorded. A rejected attempt — approving an auction
/// that is already live, say — leaves nothing in the trail, because the audit row
/// shares the transaction that the rejection rolls back, and that sharing is the
/// point (D-44). The cost is real: somebody probing what they are allowed to do is
/// invisible here. The service log has the 409s, and the alternative — a second
/// transaction for the attempt — buys a trail a portal's ordinary validation
/// failures would fill.
/// </remarks>
static async Task<IResult> Mutate(
    IDbContextFactory<AdminDbContext> factory, Guid id, CancellationToken ct,
    HttpContext http, string action, Action<Auction> change, string? details = null)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var auction = await Load(db, id, ct);
    if (auction is null) return Results.NotFound();

    try
    {
        change(auction);

        var (who, roles, source) = StaffAudit.ActorOf(http);
        db.RecordStaffAction(who, roles, source, action, AuditSubject.Auction(id), details);

        await db.SaveChangesAsync(ct);
        return Results.Ok(AuctionResponse.From(auction));
    }
    catch (AuctionValidationException ex)
    {
        return Results.BadRequest(new { problems = ex.Problems });
    }
    catch (InvalidAuctionTransitionException ex)
    {
        return Results.Conflict(new { error = ex.Message, status = ex.From.ToString() });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
}

// Deliberately not `public partial class Program;` — making it public would
// collide with the bid-catcher's Program in any test assembly referencing both.
// Nothing here needs WebApplicationFactory.

// --- contracts -------------------------------------------------------------

public sealed record CreateAuctionRequest(Guid CreatedByUserId, string NameAr, string NameEn);

public sealed record UpdateAuctionRequest(
    string NameAr, string NameEn, BidChannel Channel,
    /// <summary>Masked unless the administrator says otherwise (D-22).</summary>
    BidderVisibility BidderVisibility,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    long OpeningPriceMinorUnits,
    /// <summary>Omit or null to leave the reserve unchanged; it is never readable back.</summary>
    long? ReservePriceMinorUnits,
    long MinIncrementMinorUnits, long DepositMinorUnits,
    decimal BrokerageFeePercent, long BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int MaxExtensions, string? Phase);

public sealed record AssignClerkRequest(Guid ClerkUserId);
public sealed record ExtendRequest(int Seconds);
public sealed record SigningKeyResponse(string SecretHex, int KeyEpoch);

public sealed record AttachmentRequest(Guid DocumentId, string TitleAr);

public sealed record AddPlotRequest(
    string DeedNumber, decimal AreaSqm, string? Latitude, string? Longitude,
    string? DescriptionAr, string? DescriptionEn);

public sealed record DocumentRequest(Guid DocumentId);

/// <summary>A signed permission to read one document, and when it stops working.</summary>
public sealed record DocumentGrantResponse(
    Guid DocumentId, string Grant, DateTimeOffset ExpiresAt);
public sealed record RejectRequest(string Reason);
public sealed record OfferCandidateRequest(Guid BidderId, long AmountMinorUnits);
public sealed record ConfirmAwardRequest(Guid CommitteeUserId);
public sealed record DisqualifyRequest(string Reason, bool ForfeitDeposit);

/// <summary>
/// The API view of an auction.
///
/// Note what is absent: <c>ReservePriceMinorUnits</c>. The reserve is secret
/// (D-06) and never appears in a response from this service — not even to an
/// admin through this endpoint, and not after the auction fails to reach it.
/// It leaves the service only on the restricted auctions.sealed topic.
/// </summary>
/// <summary>
/// Deliberately smaller than <see cref="AuctionResponse"/>: a list of fifty
/// auctions does not need every award and plot on each one.
/// </summary>
public sealed record AuctionListItem(
    Guid Id, string Status, string NameAr, string NameEn, string Channel, string BidderVisibility,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    long OpeningPriceMinorUnits, long DepositMinorUnits,
    int PlotCount, DateTimeOffset CreatedAt,
    // What the bidder's catalogue card shows, so staff see the same card citizens do.
    long BookletPriceMinorUnits, Guid? CoverImageDocumentId, decimal TotalAreaSqm);

public sealed record AuctionResponse(
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    string BidderVisibility, Guid? ClerkUserId, string? Phase,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    long OpeningPriceMinorUnits, long MinIncrementMinorUnits, long DepositMinorUnits,
    decimal BrokerageFeePercent, long BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int MaxExtensions,
    Guid? BookletDocumentId, Guid? CoverImageDocumentId,
    IReadOnlyList<PublicDocument> Attachments,
    int PlotCount, decimal TotalAreaSqm, string? RejectionReason,
    string? CancellationReason, DateTimeOffset? CancelledAt,
    string? ResultRejectionReason,
    // Both halves of the pending candidate. The id alone would ask the committee to
    // approve an unknown sum.
    Guid? PendingCandidateBidderId, long? PendingCandidateAmountMinorUnits,
    AwardResponse? CurrentAward,
    // The open award, or the settled one whose title transfer is still tracked.
    AwardResponse? FollowUpAward)
{
    public static AuctionResponse From(Auction a)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            a.Id, a.Status.ToString(), a.NameAr, a.NameEn, a.Channel.ToString(),
            a.BidderVisibility.ToString(), a.ClerkUserId, a.Phase,
            a.StartsAt, a.EndsAt, a.OpeningPriceMinorUnits, a.MinIncrementMinorUnits,
            a.DepositMinorUnits, a.BrokerageFeePercent, a.BookletPriceMinorUnits,
            a.QuietPeriodSeconds, a.MaxExtensions, a.BookletDocumentId, a.CoverImageDocumentId,
            a.Attachments.Select(x => new PublicDocument(x.DocumentId, x.TitleAr)).ToArray(),
            a.Plots.Count, a.TotalAreaSqm, a.RejectionReason,
            a.CancellationReason, a.CancelledAt,
            a.ResultRejectionReason,
            a.PendingCandidateBidderId, a.PendingCandidateAmountMinorUnits,
            a.CurrentAward is null ? null : AwardResponse.From(a.CurrentAward, now),
            a.FollowUpAward is null ? null : AwardResponse.From(a.FollowUpAward, now));
    }
}

public sealed record AwardResponse(
    Guid Id, Guid BidderId, long AmountMinorUnits, int CascadeStep,
    DateTimeOffset ConfirmedAt, DateTimeOffset ComplianceDeadline,
    Guid? LetterDocumentId, Guid? SignedLetterDocumentId, DateTimeOffset? WinnerNotifiedAt,
    DateTimeOffset? SettledAt,
    long PaidMinorUnits, long RemainingMinorUnits, bool Overdue,
    IReadOnlyList<ReceiptResponse> Receipts,
    string TransferStatus, string? TransferReference, Guid? TransferDocumentId,
    DateTimeOffset? TransferUpdatedAt, DateTimeOffset? TransferCompletedAt)
{
    public static AwardResponse From(Award a, DateTimeOffset now) => new(
        a.Id, a.BidderId, a.AmountMinorUnits, a.CascadeStep,
        a.ConfirmedAt, a.ComplianceDeadline,
        a.LetterDocumentId, a.SignedLetterDocumentId, a.WinnerNotifiedAt,
        a.SettledAt,
        a.PaidMinorUnits, a.RemainingMinorUnits, a.IsOverdue(now),
        a.Receipts.OrderBy(r => r.PaidOn)
            .Select(r => new ReceiptResponse(
                r.ReceiptId, r.Kind.ToString(), r.AmountMinorUnits, r.PaidOn, r.Reference,
                r.DocumentId, r.RecordedAt))
            .ToArray(),
        a.TransferStatus.ToString(), a.TransferReference, a.TransferDocumentId,
        a.TransferUpdatedAt, a.TransferCompletedAt);
}

public sealed record ReceiptResponse(
    Guid Id, string Kind, long AmountMinorUnits, DateTimeOffset PaidOn, string Reference,
    Guid? DocumentId, DateTimeOffset RecordedAt);

public sealed record FollowUpEntry(
    Guid AuctionId, string NameAr, string Status, string? Phase, AwardResponse Award);

public sealed record AwardReceiptRequest(
    long AmountMinorUnits, DateTimeOffset PaidOn, string Reference, Guid? DocumentId);

public sealed record ReferenceRequest(string Reference);

public sealed record TransferRequest(TransferStatus Status, string? Reference, Guid? DocumentId);
