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

app.MapPost("/auctions", async (CreateAuctionRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var auction = Auction.CreateDraft(r.CreatedByUserId, r.NameAr, r.NameEn);
    db.Auctions.Add(auction);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/auctions/{auction.Id}", AuctionResponse.From(auction));
})
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPut("/auctions/{id:guid}", (Guid id, UpdateAuctionRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.UpdateDetails(
        r.NameAr, r.NameEn, r.Channel, r.StartsAt, r.EndsAt,
        r.OpeningPriceMinorUnits, r.ReservePriceMinorUnits, r.MinIncrementMinorUnits,
        r.DepositMinorUnits, r.BrokerageFeePercent, r.BookletPriceMinorUnits,
        r.QuietPeriodSeconds, r.MaxExtensions, r.Phase)))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/plots", (Guid id, AddPlotRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.AddPlot(new Plot(
        id, r.DeedNumber, r.AreaSqm, r.Latitude, r.Longitude, r.DescriptionAr, r.DescriptionEn))))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapDelete("/auctions/{id:guid}/plots/{plotId:guid}", (Guid id, Guid plotId, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.RemovePlot(plotId)))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/booklet", (Guid id, DocumentRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.AttachBooklet(r.DocumentId)))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/cover-image", (Guid id, DocumentRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.AttachCoverImage(r.DocumentId)))
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

app.MapPost("/auctions/{id:guid}/submit", (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.SubmitForReview(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AuctionAdmin);

app.MapPost("/auctions/{id:guid}/approve", (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.Approve(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/reject", (Guid id, RejectRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.Reject(r.Reason)))
    .RequireAuthorization(Policies.AwardCommittee);

// --- lifecycle --------------------------------------------------------------
//
// Temporary. These transitions belong to the bid processor and should arrive
// over auctions.lifecycle; they are exposed here so the award workflow is
// reachable and operable before that consumer exists.

app.MapPost("/auctions/{id:guid}/lifecycle/{transition}", (Guid id, string transition, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a =>
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
    }))
    .RequireAuthorization(Policies.AuctionAdmin);

// --- الترسية : award workflow ----------------------------------------------

app.MapPost("/auctions/{id:guid}/candidate", (Guid id, OfferCandidateRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.OfferCandidate(r.BidderId, r.AmountMinorUnits)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award", (Guid id, ConfirmAwardRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.ConfirmAward(r.CommitteeUserId, DateTimeOffset.UtcNow, complianceWindow)))
    // The single most consequential act in the platform: it transfers a parcel of
    // state land to a named person. A committee member's role is not enough on its
    // own — the second factor is what ties the decision to the person, which is
    // what the minutes of an award have to be able to claim.
    .RequireAuthorization(Policies.AwardCommitteeStepUp);

app.MapPost("/auctions/{id:guid}/award/letter", (Guid id, DocumentRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.GenerateAwardLetter(r.DocumentId)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award/signed-letter", (Guid id, DocumentRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.UploadSignedAwardLetter(r.DocumentId)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award/notify", (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.NotifyWinner(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/award/disqualify", (Guid id, DisqualifyRequest r, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.DisqualifyWinner(r.Reason, r.ForfeitDeposit, DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/unsold", (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.MarkUnsold()))
    .RequireAuthorization(Policies.AwardCommittee);

app.MapPost("/auctions/{id:guid}/settle", (Guid id, IDbContextFactory<AdminDbContext> f, CancellationToken ct) =>
    Mutate(f, id, ct, a => a.Settle(DateTimeOffset.UtcNow)))
    .RequireAuthorization(Policies.AwardCommittee);

// The list the portal opens on. Staff-only: it carries every auction including
// drafts and rejections, which is a different thing entirely from the public
// catalogue the query BFF serves.
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
            a.StartsAt, a.EndsAt, a.OpeningPriceMinorUnits, a.DepositMinorUnits,
            a.Plots.Count, a.CreatedAt))
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
/// Loads, applies the change, saves. SaveChanges drains any domain events the
/// aggregate raised into the outbox in the same transaction.
/// </summary>
static async Task<IResult> Mutate(
    IDbContextFactory<AdminDbContext> factory, Guid id, CancellationToken ct, Action<Auction> change)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var auction = await Load(db, id, ct);
    if (auction is null) return Results.NotFound();

    try
    {
        change(auction);
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
    DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    long OpeningPriceMinorUnits,
    /// <summary>Omit or null to leave the reserve unchanged; it is never readable back.</summary>
    long? ReservePriceMinorUnits,
    long MinIncrementMinorUnits, long DepositMinorUnits,
    decimal BrokerageFeePercent, long BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int MaxExtensions, string? Phase);

public sealed record AddPlotRequest(
    string DeedNumber, decimal AreaSqm, string? Latitude, string? Longitude,
    string? DescriptionAr, string? DescriptionEn);

public sealed record DocumentRequest(Guid DocumentId);
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
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    long OpeningPriceMinorUnits, long DepositMinorUnits,
    int PlotCount, DateTimeOffset CreatedAt);

public sealed record AuctionResponse(
    Guid Id, string Status, string NameAr, string NameEn, string Channel, string? Phase,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    long OpeningPriceMinorUnits, long MinIncrementMinorUnits, long DepositMinorUnits,
    decimal BrokerageFeePercent, long BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int MaxExtensions,
    Guid? BookletDocumentId, Guid? CoverImageDocumentId,
    int PlotCount, decimal TotalAreaSqm, string? RejectionReason,
    // Both halves of the pending candidate. The id alone would ask the committee to
    // approve an unknown sum.
    Guid? PendingCandidateBidderId, long? PendingCandidateAmountMinorUnits,
    AwardResponse? CurrentAward)
{
    public static AuctionResponse From(Auction a) => new(
        a.Id, a.Status.ToString(), a.NameAr, a.NameEn, a.Channel.ToString(), a.Phase,
        a.StartsAt, a.EndsAt, a.OpeningPriceMinorUnits, a.MinIncrementMinorUnits,
        a.DepositMinorUnits, a.BrokerageFeePercent, a.BookletPriceMinorUnits,
        a.QuietPeriodSeconds, a.MaxExtensions, a.BookletDocumentId, a.CoverImageDocumentId,
        a.Plots.Count, a.TotalAreaSqm, a.RejectionReason,
        a.PendingCandidateBidderId, a.PendingCandidateAmountMinorUnits,
        a.CurrentAward is null ? null : AwardResponse.From(a.CurrentAward));
}

public sealed record AwardResponse(
    Guid Id, Guid BidderId, long AmountMinorUnits, int CascadeStep,
    DateTimeOffset ConfirmedAt, DateTimeOffset ComplianceDeadline,
    Guid? LetterDocumentId, Guid? SignedLetterDocumentId, DateTimeOffset? WinnerNotifiedAt)
{
    public static AwardResponse From(Award a) => new(
        a.Id, a.BidderId, a.AmountMinorUnits, a.CascadeStep,
        a.ConfirmedAt, a.ComplianceDeadline,
        a.LetterDocumentId, a.SignedLetterDocumentId, a.WinnerNotifiedAt);
}
