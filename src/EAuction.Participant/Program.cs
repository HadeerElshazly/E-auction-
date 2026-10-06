using EAuction.Core;
using EAuction.Outbox;
using EAuction.Participant.Domain;
using EAuction.Participant.Integration;
using EAuction.Participant.Persistence;
using EAuction.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("Participant")
    ?? "Host=localhost;Database=eauction_participant;Username=eauction;Password=eauction";

builder.Services.AddDbContextFactory<ParticipantDbContext>(o => o.UseNpgsql(connectionString));

// The same master key the bid catcher holds. Neither service ever sends a
// derived secret to the other — only the epoch travels, on the topic.
var bidderMasterKeyHex = builder.Configuration["Participant:BidderMasterKeyHex"];
if (string.IsNullOrWhiteSpace(bidderMasterKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Participant:BidderMasterKeyHex is required, and must match the catcher's. "
            + "Without it, bids signed with the key handed to a bidder cannot be verified.");

    bidderMasterKeyHex = Convert.ToHexString(BidderKeys.NewMasterKey());
}
var bidderMasterKey = Convert.FromHexString(bidderMasterKeyHex);

// The key the document service verifies grants with. Held here because the rule
// for كراسة الشروط is this service's — has this bidder paid for it — and the
// document service must not have to learn what a subscription is.
var documentGrantKeyHex = builder.Configuration["Documents:GrantKeyHex"];
if (string.IsNullOrWhiteSpace(documentGrantKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Documents:GrantKeyHex is required, and must match the document "
            + "service's. Without it no bidder can open the terms booklet they paid for.");

    documentGrantKeyHex = Convert.ToHexString(DocumentGrants.NewKey());
}
var documentGrantKey = Convert.FromHexString(documentGrantKeyHex);

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

builder.Services.AddSingleton<IEventStream>(
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "participant"
        }));

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
        },
        sp.GetRequiredService<ILogger<KafkaTopicPublisher>>()));
}

builder.Services.AddSingleton<IOutboxRouter, ParticipantOutboxRouter>();
builder.Services.AddSingleton<OutboxRelay<ParticipantDbContext>>();
builder.Services.AddHostedService<OutboxRelayService<ParticipantDbContext>>();
builder.Services.AddHostedService<CatalogConsumer>();
builder.Services.AddHostedService<SettlementConsumer>();

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
app.MapGet("/health/ready", async (IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(503);
}).AllowAnonymous();

// --- registration ----------------------------------------------------------

// Created from a Nafath assertion, never from a form: identity is the one
// thing a bidder cannot be allowed to assert about themselves. The Nafath
// integration itself is not built, so this endpoint stands in for its
// callback and must be gated before any real use.
app.MapPost("/bidders/register", async (
    HttpContext http,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    // Every field comes from the token and the endpoint takes no body, because a
    // body is a place a caller could put someone else's national ID. Keycloak put
    // these claims there after Nafath verified them; see deploy/keycloak/README.md
    // for the mappers that produce them.
    var subject = http.User.SubjectId();
    if (subject is null) return Results.Forbid();

    var nationalId = http.User.FindFirst("national_id")?.Value;
    if (string.IsNullOrWhiteSpace(nationalId))
        return Results.Json(
            new { problems = new[] { "لم تصل هويتك من نفاذ. يجب الدخول عبر نفاذ "
                                   + "قبل التسجيل كمزايد." } },
            statusCode: 403);

    var nameAr = http.User.FindFirst("name_ar")?.Value ?? "";
    var nameEn = http.User.FindFirst("name")?.Value ?? "";

    await using var db = await f.CreateDbContextAsync(ct);

    var existing = await db.Bidders.FindAsync(new object?[] { subject.Value }, ct);
    if (existing is not null) return Results.Ok(BidderResponse.From(existing));

    // The same national ID under a different subject. A Keycloak account deleted and
    // re-brokered gives the same citizen a new sub, and this is where that lands.
    // It cannot create a second bidder: a deposit, a signing key, a subscription and
    // a ladder position all hang off Bidder.Id, so two rows for one person would mean
    // two places in the same auction. Nor can it quietly move the row to the new
    // subject -- that is an account takeover if the claim is ever wrong. Operations
    // re-link it deliberately.
    if (await db.Bidders.AnyAsync(b => b.NationalId == nationalId, ct))
        return Results.Conflict(new
        {
            reason = "NationalIdAlreadyRegistered",
            problems = new[] { "رقم الهوية مسجَّل بحساب آخر. يلزم ربطه من جديد "
                             + "قبل استخدامه مع هذا الحساب." }
        });

    try
    {
        var bidder = Bidder.FromNafath(
            subject.Value, nationalId, nameAr, nameEn, DateTimeOffset.UtcNow);
        db.Bidders.Add(bidder);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/bidders/{bidder.Id}", BidderResponse.From(bidder));
    }
    catch (ParticipantValidationException ex)
    {
        return Results.BadRequest(new { problems = ex.Problems });
    }
    catch (DbUpdateException e) when (e.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation
    })
    {
        // Two registrations for one national ID in flight at once: the check above
        // passed for both and the index caught the loser. Same answer, not a 500.
        return Results.Conflict(new
        {
            reason = "NationalIdAlreadyRegistered",
            problems = new[] { "رقم الهوية مسجَّل بحساب آخر. يلزم ربطه من جديد "
                             + "قبل استخدامه مع هذا الحساب." }
        });
    }
})
    // KYC. Registration binds a national identity to an account for good, and
    // every later act in the platform rests on that binding being right.
    .RequireAuthorization(Policies.BidderStepUp);

app.MapPost("/bidders/{id:guid}/profile", async (
    HttpContext http, Guid id, CompleteProfileRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    if (http.User.SubjectId() != id) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);
    var bidder = await db.Bidders.FindAsync(new object?[] { id }, ct);
    if (bidder is null) return Results.NotFound();

    try
    {
        bidder.CompleteProfile(r.Phone, r.Email, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(BidderResponse.From(bidder));
    }
    catch (ParticipantValidationException ex)
    {
        return Results.BadRequest(new { problems = ex.Problems });
    }
}).RequireAuthorization(Policies.Bidder);

app.MapGet("/bidders/{id:guid}", async (
    HttpContext http, Guid id, IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    if (http.User.SubjectId() != id && !http.User.IsInRole(Roles.AuctionAdmin))
        return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);
    var bidder = await db.Bidders.FindAsync(new object?[] { id }, ct);
    return bidder is null ? Results.NotFound() : Results.Ok(BidderResponse.From(bidder));
}).RequireAuthorization();

// --- subscription (الاشتراك في المزاد) --------------------------------------

app.MapPost("/auctions/{auctionId:guid}/subscriptions", async (
    HttpContext http, Guid auctionId, StartSubscriptionRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    if (http.User.SubjectId() != r.BidderId) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);

    if (await db.AuctionTerms.FindAsync(new object?[] { auctionId }, ct) is null)
        return Results.NotFound(new { error = "Unknown auction." });

    var existing = await db.Subscriptions
        .FirstOrDefaultAsync(s => s.AuctionId == auctionId && s.BidderId == r.BidderId, ct);
    if (existing is not null) return Results.Ok(SubscriptionResponse.From(existing));

    var subscription = Subscription.Start(auctionId, r.BidderId);
    db.Subscriptions.Add(subscription);
    await db.SaveChangesAsync(ct);

    return Results.Created(
        $"/auctions/{auctionId}/subscriptions/{r.BidderId}",
        SubscriptionResponse.From(subscription));
}).RequireAuthorization(Policies.Bidder);

// Asks the payment service for the booklet fee. 202, not 200: the booklet is not
// bought when this returns, and a response that said otherwise would be a lie the
// portal then had to work around.
app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/booklet", (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, terms) => s.RequestBooklet(terms, DateTimeOffset.UtcNow), http,
        accepted: true))
    // Money, however little. The booklet fee is a card payment like any other.
    .RequireAuthorization(Policies.BidderStepUp);

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/terms", (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, _) => s.AcceptTerms(DateTimeOffset.UtcNow), http))
    .RequireAuthorization(Policies.Bidder);

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/deposit-method", (
    HttpContext http, Guid auctionId, Guid bidderId, DepositMethodRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, terms) => s.ChooseDeposit(r.Method, terms, DateTimeOffset.UtcNow), http))
    .RequireAuthorization(Policies.Bidder);

// Authorises the deposit — التأمين — and nothing more. The bidder becomes eligible
// when the gateway settles it, never because they asked.
//
// It takes no body. It used to take a payment reference the caller made up, which
// this service then recorded as proof of a payment nobody had taken.
app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/deposit", (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, terms) => s.AuthoriseDeposit(terms, DateTimeOffset.UtcNow), http,
        accepted: true))
    // Money. A second factor, confirmed within the last few minutes.
    .RequireAuthorization(Policies.BidderStepUp);

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/guarantee", (
    HttpContext http, Guid auctionId, Guid bidderId, BankGuaranteeRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, terms) => s.SubmitBankGuarantee(r.DocumentId, r.ExpiresAt, terms), http))
    // A bank guarantee stands in for the deposit, so it is the same gate.
    .RequireAuthorization(Policies.BidderStepUp);

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/guarantee/verify", (
    HttpContext http, Guid auctionId, Guid bidderId, VerifyGuaranteeRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, bidder, terms) => s.VerifyBankGuarantee(
            r.VerifiedByUserId, bidder, terms, DateTimeOffset.UtcNow),
        http, staffAction: true, audit: "VerifyBankGuarantee",
        // Accepting a guarantee is accepting a bank's paper in place of money, and
        // it makes a citizen eligible to bid on state land. If one turns out to
        // have been forged, this row is how anyone finds out who accepted it.
        details: "A bank guarantee was accepted in place of the deposit."))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/revoke", (
    HttpContext http, Guid auctionId, Guid bidderId, RevokeRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, bidder, terms) => s.Revoke(r.Reason, bidder, terms, DateTimeOffset.UtcNow),
        http, staffAction: true, audit: "RevokeEligibility", details: r.Reason))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/rotate-key", (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct, (s, bidder, terms) => s.RotateKey(bidder, terms), http,
        staffAction: http.User.IsInRole(Roles.AuctionAdmin),
        // Recorded only when staff did it. A bidder rotating their own key is a
        // bidder using a feature; an administrator rotating someone else's
        // invalidates the key that bidder is holding, which is a thing done to a
        // person and worth a name against it.
        audit: http.User.IsInRole(Roles.AuctionAdmin) ? "RotateBidderKey" : null))
    .RequireAuthorization();

// The roster a clerk works from: who in the room is allowed to bid (§29).
//
// Staff only, and deliberately narrow — the eligible bidders and their names, no
// deposit history, no payment references, no national id. A clerk picking a bidder
// off a list needs to know who they are, and nothing else here is their business.
//
// It names people regardless of the auction's masking setting (D-22). That setting
// governs what bidders and the public see of each other; a hall auction's clerk is
// the person typing on their behalf and cannot do it from pseudonyms.
app.MapGet("/auctions/{auctionId:guid}/subscriptions", async (
    Guid auctionId, IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);

    var roster = await db.Subscriptions
        .Where(s => s.AuctionId == auctionId && s.Status == SubscriptionStatus.Eligible)
        .Join(db.Bidders, s => s.BidderId, b => b.Id, (s, b) => new { s.BidderId, b.NameAr })
        .OrderBy(x => x.NameAr)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        items = roster.Select((x, i) => new RosterEntry(
            x.BidderId,
            x.NameAr,
            // A paddle number, so a clerk can work from what the room is holding up
            // rather than reading a name off a screen. Positional and stable only
            // for as long as the roster is: it is a convenience, not an identifier.
            i + 1))
    });
}).RequireAuthorization(Policies.StaffOnTheFloor);

app.MapGet("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}", async (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    if (http.User.SubjectId() != bidderId && !http.User.IsInRole(Roles.AuctionAdmin))
        return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);
    var subscription = await db.Subscriptions
        .FirstOrDefaultAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId, ct);
    return subscription is null
        ? Results.NotFound()
        : Results.Ok(SubscriptionResponse.From(subscription));
}).RequireAuthorization();

// The bidder's signing secret, handed over once they are eligible.
//
// It is derived, not stored, so it never sits in a database or on a topic. It is
// also the single most dangerous value this service can emit: whoever holds it
// can produce bids indistinguishable from the bidder's own, which is the entire
// evidential value of signing them.
//
// Three locks, and the role policy below is the outermost of them rather than
// the only one. An endpoint this sensitive should not be reachable because a
// fallback policy happens to be configured somewhere else.
app.MapGet("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/signing-key", async (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    // The bidder and nobody else. An administrator who could read this could
    // bid as them, and the signature would be indistinguishable from the real
    // bidder's — which is the whole evidential value of signing.
    if (http.User.SubjectId() != bidderId) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);
    var subscription = await db.Subscriptions
        .FirstOrDefaultAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId, ct);

    if (subscription is null) return Results.NotFound();
    if (subscription.Status != SubscriptionStatus.Eligible)
        return Results.Conflict(new { error = "This bidder is not eligible to bid." });

    return Results.Ok(new SigningKeyResponse(
        Convert.ToHexString(
            BidderKeys.Derive(bidderMasterKey, auctionId, bidderId, subscription.KeyEpoch)),
        subscription.KeyEpoch));
}).RequireAuthorization(Policies.Bidder);

// A grant to read كراسة الشروط, for a bidder who has paid for it.
//
// The document itself is Restricted in the document service: no role opens it,
// not an administrator's. This endpoint is the only way a bidder gets at it, and
// it says yes for exactly one reason — they bought it.
//
// The grant lasts five minutes and names this bidder, so a forwarded link is
// useless to whoever it is forwarded to.
app.MapGet("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/booklet-grant", async (
    HttpContext http, Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    if (http.User.SubjectId() != bidderId) return Results.Forbid();

    await using var db = await f.CreateDbContextAsync(ct);

    var terms = await db.AuctionTerms.FindAsync(new object?[] { auctionId }, ct);
    if (terms?.BookletDocumentId is null)
        return Results.NotFound(new { error = "This auction has no booklet attached." });

    var subscription = await db.Subscriptions
        .FirstOrDefaultAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId, ct);

    // Paid for, not merely asked for. Draft means the fee is outstanding or in
    // flight, and a bidder who has not paid is the person this gate is for.
    if (subscription is null || subscription.BookletPurchasedAt is null)
        return Results.Conflict(new
        {
            error = "The terms booklet has not been purchased.",
            reason = "BookletNotPurchased"
        });

    return Results.Ok(new DocumentGrantResponse(
        terms.BookletDocumentId.Value,
        DocumentGrants.Mint(
            documentGrantKey, terms.BookletDocumentId.Value, bidderId, DateTimeOffset.UtcNow),
        DateTimeOffset.UtcNow.Add(DocumentGrants.Lifetime)));
}).RequireAuthorization(Policies.Bidder);

app.Run();

// ---------------------------------------------------------------------------

/// <param name="audit">
/// The name this appears under in the audit trail, for the steps a member of staff
/// performs on someone else's subscription. Null for a bidder acting on their own:
/// a citizen buying a booklet is not a staff action, and a trail that recorded one
/// row per bidder per step would bury the handful of rows that matter.
/// </param>
/// <remarks>
/// Like the auction service's equivalent, only successful changes are recorded —
/// the audit row shares the transaction that a rejection rolls back, which is the
/// design (D-44) and its cost.
/// </remarks>
static async Task<IResult> Mutate(
    IDbContextFactory<ParticipantDbContext> factory,
    Guid auctionId, Guid bidderId, CancellationToken ct,
    Action<Subscription, Bidder, AuctionTerms> change,
    HttpContext? http = null, bool staffAction = false, bool accepted = false,
    string? audit = null, string? details = null)
{
    // A bidder may act only on their own subscription. Staff actions —
    // verifying a guarantee, revoking — are gated by role instead, because
    // they are by definition performed on someone else's.
    if (http is not null && !staffAction && http.User.SubjectId() != bidderId)
        return Results.Forbid();

    await using var db = await factory.CreateDbContextAsync(ct);

    var subscription = await db.Subscriptions
        .FirstOrDefaultAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId, ct);
    if (subscription is null) return Results.NotFound();

    var bidder = await db.Bidders.FindAsync(new object?[] { bidderId }, ct);
    if (bidder is null) return Results.NotFound(new { error = "Unknown bidder." });

    var terms = await db.AuctionTerms.FindAsync(new object?[] { auctionId }, ct);
    if (terms is null) return Results.NotFound(new { error = "Unknown auction." });

    try
    {
        change(subscription, bidder, terms);

        if (audit is not null && http is not null)
        {
            var (who, roles, source) = StaffAudit.ActorOf(http);
            db.RecordStaffAction(
                who, roles, source, audit,
                AuditSubject.Subscription(auctionId, bidderId), details);
        }

        await db.SaveChangesAsync(ct);

        // 202 for the two steps that only ask the payment service for money: the
        // state the caller will eventually see is not the state being returned.
        var body = SubscriptionResponse.From(subscription);
        return accepted ? Results.Accepted(value: body) : Results.Ok(body);
    }
    catch (ParticipantValidationException ex)
    {
        return Results.BadRequest(new { problems = ex.Problems });
    }
    catch (InvalidSubscriptionTransitionException ex)
    {
        return Results.Conflict(new { error = ex.Message, status = ex.From.ToString() });
    }
}

// Not `public partial class Program;` — it would collide with the other
// services' Program in a test assembly referencing more than one.

public sealed record CompleteProfileRequest(string Phone, string Email);
public sealed record StartSubscriptionRequest(Guid BidderId);
public sealed record DepositMethodRequest(DepositMethod Method);
public sealed record BankGuaranteeRequest(Guid DocumentId, DateTimeOffset ExpiresAt);
public sealed record VerifyGuaranteeRequest(Guid VerifiedByUserId);
public sealed record RevokeRequest(string Reason);
public sealed record SigningKeyResponse(string SecretHex, int KeyEpoch);

/// <summary>A signed permission to read one document, and when it stops working.</summary>
public sealed record DocumentGrantResponse(
    Guid DocumentId, string Grant, DateTimeOffset ExpiresAt);

/// <summary>Note the absence of NationalId: personal data under PDPL, kept in the service.</summary>
public sealed record BidderResponse(
    Guid Id, string NameAr, string NameEn, string? Phone, string? Email,
    bool Verified, bool ProfileComplete)
{
    public static BidderResponse From(Bidder b) => new(
        b.Id, b.NameAr, b.NameEn, b.Phone, b.Email, b.IsVerified, b.IsProfileComplete);
}

/// <summary>One eligible bidder, as the clerk's terminal lists them.</summary>
public sealed record RosterEntry(Guid BidderId, string NameAr, int PaddleNumber);

/// <summary>
/// Carries the two "requested at" timestamps and the last refusal as well as the
/// status, because with payment asynchronous the status alone no longer tells a
/// bidder what is happening: <c>Draft</c> means both "buy the booklet" and "we are
/// waiting for your bank", and those need different screens.
/// </summary>
public sealed record SubscriptionResponse(
    Guid Id, Guid AuctionId, Guid BidderId, string Status,
    DateTimeOffset? BookletRequestedAt,
    DateTimeOffset? BookletPurchasedAt, DateTimeOffset? TermsAcceptedAt,
    string? DepositMethod,
    DateTimeOffset? DepositRequestedAt, DateTimeOffset? DepositPaidAt,
    Guid? GuaranteeDocumentId, DateTimeOffset? GuaranteeExpiresAt,
    DateTimeOffset? GuaranteeVerifiedAt,
    int KeyEpoch, DateTimeOffset? EligibleAt, string? RevocationReason,
    DateTimeOffset? DepositResolvedAt, bool DepositForfeited,
    string? PaymentFailurePurpose, string? PaymentFailureReason,
    DateTimeOffset? PaymentFailedAt)
{
    public static SubscriptionResponse From(Subscription s) => new(
        s.Id, s.AuctionId, s.BidderId, s.Status.ToString(),
        s.BookletRequestedAt, s.BookletPurchasedAt, s.TermsAcceptedAt,
        s.DepositMethod?.ToString(), s.DepositRequestedAt, s.DepositPaidAt,
        s.GuaranteeDocumentId, s.GuaranteeExpiresAt, s.GuaranteeVerifiedAt,
        s.KeyEpoch, s.EligibleAt, s.RevocationReason,
        s.DepositResolvedAt, s.DepositForfeited,
        s.PaymentFailurePurpose, s.PaymentFailureReason, s.PaymentFailedAt);
}
