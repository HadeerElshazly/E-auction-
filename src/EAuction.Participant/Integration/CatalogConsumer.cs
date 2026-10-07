using System.Text.Json;
using EAuction.Core;
using EAuction.Participant.Domain;
using EAuction.Participant.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Participant.Integration;

/// <summary>
/// Keeps this service's copy of what it needs from auction-admin: the deposit
/// amount, the booklet price and the auction window.
///
/// It reads <c>auctions.upcoming</c>, which carries no reserve price — that
/// lives on a separate ACL-restricted topic this service cannot read, and does
/// not need to (D-23).
///
/// It also reads <c>auctions.deposits</c>, so a deposit is released or
/// forfeited once the award is final.
/// </summary>
public sealed class CatalogConsumer(
    IDbContextFactory<ParticipantDbContext> dbFactory,
    IEventStream events,
    ILogger<CatalogConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            ConsumeAsync(Topics.Upcoming, ApplyAuctionAsync, stoppingToken),
            ConsumeAsync(Topics.Deposits, ApplyDepositsAsync, stoppingToken),
            ConsumeAsync(Topics.Lifecycle, ApplyLifecycleAsync, stoppingToken));
    }

    /// <summary>
    /// Cancellations that arrived before the auction's terms. The two topics are read
    /// concurrently, so on a replay the cancellation can come first; it is applied
    /// the moment the terms are written instead of being lost.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _cancelledEarly = new();

    /// <summary>
    /// Only a cancellation: it is the one lifecycle fact that changes what this
    /// service may accept. The rest — open, closed, awarded — reaches it as
    /// deposit releases, or does not concern enrolment.
    /// </summary>
    private async Task ApplyLifecycleAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "AuctionCancelled") return;
        if (!Guid.TryParse(record.Key, out var auctionId)) return;

        var at = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var terms = await db.AuctionTerms.FindAsync(new object?[] { auctionId }, ct);
        if (terms is null)
        {
            _cancelledEarly[auctionId] = at;
            return;
        }

        terms.Cancel(at);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Auction {AuctionId} cancelled: no further enrolment or deposits.", auctionId);
    }

    private async Task ApplyAuctionAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "AuctionApproved") return;

        var payload = JsonSerializer.Deserialize<AuctionApprovedPayload>(record.Payload, Json);
        if (payload is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AuctionTerms.FindAsync(new object?[] { payload.AuctionId }, ct);

        if (existing is null)
        {
            db.AuctionTerms.Add(new AuctionTerms(
                payload.AuctionId, payload.StartsAt, payload.EndsAt,
                payload.DepositMinorUnits, payload.BookletPriceMinorUnits,
                Visibility(payload.BidderVisibility), payload.BookletDocumentId));
        }
        else
        {
            existing.Update(
                payload.StartsAt, payload.EndsAt,
                payload.DepositMinorUnits, payload.BookletPriceMinorUnits,
                DateTimeOffset.UtcNow, Visibility(payload.BidderVisibility),
                payload.BookletDocumentId);
        }

        if (_cancelledEarly.TryRemove(payload.AuctionId, out var cancelledAt))
            (existing ?? db.AuctionTerms.Local.First(t => t.AuctionId == payload.AuctionId)).Cancel(cancelledAt);

        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyDepositsAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "DepositsReleasable") return;

        var payload = JsonSerializer.Deserialize<DepositsReleasablePayload>(record.Payload, Json);
        if (payload is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var subscriptions = await db.Subscriptions
            .Where(s => s.AuctionId == payload.AuctionId)
            .ToListAsync(ct);

        var forfeited = payload.ForfeitForBidders.ToHashSet();
        var now = DateTimeOffset.UtcNow;

        foreach (var subscription in subscriptions)
            subscription.ResolveDeposit(
                forfeited.Contains(subscription.BidderId), now,
                appliedToPurchase: subscription.BidderId == payload.AppliedToPurchaseForBidder);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Auction {AuctionId}: resolved {Count} deposit(s), {Forfeited} forfeited.",
            payload.AuctionId, subscriptions.Count, forfeited.Count);
    }

    private async Task ConsumeAsync(
        string topic, Func<StreamEvent, CancellationToken, Task> apply, CancellationToken ct)
    {
        try
        {
            await foreach (var record in events.ReadAsync(topic, ct))
            {
                try
                {
                    await apply(record, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to apply {EventType} at offset {Offset} on {Topic}.",
                        record.EventType, record.Offset, topic);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // This service's own view of auction-admin's contracts.

    /// <summary>
    /// Anything this service does not recognise is masked. An auction published by
    /// an older admin service, or with a value nobody here has heard of, must not
    /// end up naming people because the parse fell through.
    /// </summary>
    private static BidderVisibility Visibility(string? value) =>
        Enum.TryParse<BidderVisibility>(value, ignoreCase: true, out var parsed)
            ? parsed
            : BidderVisibility.Masked;

    private sealed record AuctionApprovedPayload
    {
        public Guid AuctionId { get; init; }
        public string? BidderVisibility { get; init; }
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public long DepositMinorUnits { get; init; }
        public long BookletPriceMinorUnits { get; init; }
        public Guid? BookletDocumentId { get; init; }
    }

    private sealed record DepositsReleasablePayload
    {
        public Guid AuctionId { get; init; }
        public Guid[] ForfeitForBidders { get; init; } = [];
        public Guid? AppliedToPurchaseForBidder { get; init; }
    }
}
