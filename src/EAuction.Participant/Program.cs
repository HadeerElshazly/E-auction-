using EAuction.Core;
using EAuction.Outbox;
using EAuction.Participant.Domain;
using EAuction.Participant.Integration;
using EAuction.Participant.Persistence;
using Microsoft.EntityFrameworkCore;

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

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok("ok"));
app.MapGet("/health/ready", async (IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(503);
});

// --- registration ----------------------------------------------------------

// Created from a Nafath assertion, never from a form: identity is the one
// thing a bidder cannot be allowed to assert about themselves. The Nafath
// integration itself is not built, so this endpoint stands in for its
// callback and must be gated before any real use.
app.MapPost("/bidders/nafath", async (
    NafathAssertionRequest r, IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);

    var existing = await db.Bidders.FirstOrDefaultAsync(b => b.NationalId == r.NationalId, ct);
    if (existing is not null) return Results.Ok(BidderResponse.From(existing));

    try
    {
        var bidder = Bidder.FromNafath(r.NationalId, r.NameAr, r.NameEn, DateTimeOffset.UtcNow);
        db.Bidders.Add(bidder);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/bidders/{bidder.Id}", BidderResponse.From(bidder));
    }
    catch (ParticipantValidationException ex)
    {
        return Results.BadRequest(new { problems = ex.Problems });
    }
});

app.MapPost("/bidders/{id:guid}/profile", async (
    Guid id, CompleteProfileRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
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
});

app.MapGet("/bidders/{id:guid}", async (
    Guid id, IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var bidder = await db.Bidders.FindAsync(new object?[] { id }, ct);
    return bidder is null ? Results.NotFound() : Results.Ok(BidderResponse.From(bidder));
});

// --- subscription (الاشتراك في المزاد) --------------------------------------

app.MapPost("/auctions/{auctionId:guid}/subscriptions", async (
    Guid auctionId, StartSubscriptionRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
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
});

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/booklet", (
    Guid auctionId, Guid bidderId, PaymentRefRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, _) => s.PurchaseBooklet(r.PaymentRef, DateTimeOffset.UtcNow)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/terms", (
    Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, _) => s.AcceptTerms(DateTimeOffset.UtcNow)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/deposit-method", (
    Guid auctionId, Guid bidderId, DepositMethodRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, terms) => s.ChooseDeposit(r.Method, terms, DateTimeOffset.UtcNow)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/deposit", (
    Guid auctionId, Guid bidderId, PaymentRefRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, bidder, _) => s.ConfirmDepositPayment(r.PaymentRef, bidder, DateTimeOffset.UtcNow)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/guarantee", (
    Guid auctionId, Guid bidderId, BankGuaranteeRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, terms) => s.SubmitBankGuarantee(r.DocumentId, r.ExpiresAt, terms)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/guarantee/verify", (
    Guid auctionId, Guid bidderId, VerifyGuaranteeRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, bidder, _) => s.VerifyBankGuarantee(r.VerifiedByUserId, bidder, DateTimeOffset.UtcNow)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/revoke", (
    Guid auctionId, Guid bidderId, RevokeRequest r,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct,
        (s, _, _) => s.Revoke(r.Reason, DateTimeOffset.UtcNow)));

app.MapPost("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/rotate-key", (
    Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
    Mutate(f, auctionId, bidderId, ct, (s, _, _) => s.RotateKey()));

app.MapGet("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}", async (
    Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var subscription = await db.Subscriptions
        .FirstOrDefaultAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId, ct);
    return subscription is null
        ? Results.NotFound()
        : Results.Ok(SubscriptionResponse.From(subscription));
});

// The bidder's signing secret, handed over once they are eligible.
//
// It is derived, not stored, so it never sits in a database or on a topic.
// This endpoint must be authenticated as the bidder before any real use —
// right now it is open, like everything else in this service.
app.MapGet("/auctions/{auctionId:guid}/subscriptions/{bidderId:guid}/signing-key", async (
    Guid auctionId, Guid bidderId,
    IDbContextFactory<ParticipantDbContext> f, CancellationToken ct) =>
{
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
});

app.Run();

// ---------------------------------------------------------------------------

static async Task<IResult> Mutate(
    IDbContextFactory<ParticipantDbContext> factory,
    Guid auctionId, Guid bidderId, CancellationToken ct,
    Action<Subscription, Bidder, AuctionTerms> change)
{
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
        await db.SaveChangesAsync(ct);
        return Results.Ok(SubscriptionResponse.From(subscription));
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

public sealed record NafathAssertionRequest(string NationalId, string NameAr, string NameEn);
public sealed record CompleteProfileRequest(string Phone, string Email);
public sealed record StartSubscriptionRequest(Guid BidderId);
public sealed record PaymentRefRequest(string PaymentRef);
public sealed record DepositMethodRequest(DepositMethod Method);
public sealed record BankGuaranteeRequest(Guid DocumentId, DateTimeOffset ExpiresAt);
public sealed record VerifyGuaranteeRequest(Guid VerifiedByUserId);
public sealed record RevokeRequest(string Reason);
public sealed record SigningKeyResponse(string SecretHex, int KeyEpoch);

/// <summary>Note the absence of NationalId: personal data under PDPL, kept in the service.</summary>
public sealed record BidderResponse(
    Guid Id, string NameAr, string NameEn, string? Phone, string? Email,
    bool Verified, bool ProfileComplete)
{
    public static BidderResponse From(Bidder b) => new(
        b.Id, b.NameAr, b.NameEn, b.Phone, b.Email, b.IsVerified, b.IsProfileComplete);
}

public sealed record SubscriptionResponse(
    Guid Id, Guid AuctionId, Guid BidderId, string Status,
    DateTimeOffset? BookletPurchasedAt, DateTimeOffset? TermsAcceptedAt,
    string? DepositMethod, DateTimeOffset? DepositPaidAt,
    Guid? GuaranteeDocumentId, DateTimeOffset? GuaranteeExpiresAt,
    DateTimeOffset? GuaranteeVerifiedAt,
    int KeyEpoch, DateTimeOffset? EligibleAt, string? RevocationReason,
    DateTimeOffset? DepositResolvedAt, bool DepositForfeited)
{
    public static SubscriptionResponse From(Subscription s) => new(
        s.Id, s.AuctionId, s.BidderId, s.Status.ToString(),
        s.BookletPurchasedAt, s.TermsAcceptedAt,
        s.DepositMethod?.ToString(), s.DepositPaidAt,
        s.GuaranteeDocumentId, s.GuaranteeExpiresAt, s.GuaranteeVerifiedAt,
        s.KeyEpoch, s.EligibleAt, s.RevocationReason,
        s.DepositResolvedAt, s.DepositForfeited);
}
