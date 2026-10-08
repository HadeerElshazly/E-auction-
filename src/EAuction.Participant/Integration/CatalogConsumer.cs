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
    /// Lifecycle events that arrived before the auction's terms, in order. The topics
    /// are read concurrently, so on a replay they can come first; they are applied the
    /// moment the terms are written instead of being lost.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<string>> _early = new();

    /// <summary>
    /// The auction's stage — for «طلباتي»'s server-side filter — and its cancellation,
    /// which also stops enrolment and deposits (AuctionTerms.RequireOpen).
    /// </summary>
    private async Task ApplyLifecycleAsync(StreamEvent record, CancellationToken ct)
    {
        if (!Guid.TryParse(record.Key, out var auctionId)) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The winner's view of their award. Needs no terms, so it is never deferred.
        if (record.EventType == "AwardFollowUpUpdated")
        {
            await ApplyAwardAsync(db, auctionId, record, ct);
            return;
        }

        var terms = await db.AuctionTerms.FindAsync(new object?[] { auctionId }, ct);
        if (terms is null)
        {
            var list = _early.GetOrAdd(auctionId, _ => new List<string>());
            lock (list) list.Add(record.EventType);
            return;
        }

        if (!Apply(terms, record.EventType)) return;
        await db.SaveChangesAsync(ct);
        if (record.EventType == "AuctionCancelled")
            logger.LogInformation("Auction {AuctionId} cancelled: no further enrolment or deposits.", auctionId);
    }

    private async Task ApplyAwardAsync(
        ParticipantDbContext db, Guid auctionId, StreamEvent record, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<AwardFollowUpPayload>(record.Payload, Json);
        if (p is null) return;

        var award = await db.WinnerAwards.FindAsync(new object?[] { auctionId }, ct);
        if (award is null)
        {
            award = new WinnerAward(auctionId);
            db.WinnerAwards.Add(award);
        }

        if (!award.Apply(
                p.AwardId, p.WinnerBidderId, p.AmountMinorUnits, p.BrokerageMinorUnits,
                p.ConfirmedAt, p.ComplianceDeadline, p.SignedLetterDocumentId, p.WinnerNotifiedAt,
                p.PaidMinorUnits, p.RemainingMinorUnits, p.TransferStatus ?? "NotStarted",
                p.TransferCompletedAt, p.SettledAt, p.DisqualifiedAt, p.At))
            return;

        await db.SaveChangesAsync(ct);
    }

    private sealed record AwardFollowUpPayload
    {
        public Guid AwardId { get; init; }
        public Guid WinnerBidderId { get; init; }
        public long AmountMinorUnits { get; init; }
        public long BrokerageMinorUnits { get; init; }
        public DateTimeOffset ConfirmedAt { get; init; }
        public DateTimeOffset ComplianceDeadline { get; init; }
        public Guid? SignedLetterDocumentId { get; init; }
        public DateTimeOffset? WinnerNotifiedAt { get; init; }
        public long PaidMinorUnits { get; init; }
        public long RemainingMinorUnits { get; init; }
        public string? TransferStatus { get; init; }
        public DateTimeOffset? TransferCompletedAt { get; init; }
        public DateTimeOffset? SettledAt { get; init; }
        public DateTimeOffset? DisqualifiedAt { get; init; }
        public DateTimeOffset At { get; init; }
    }

    private static bool Apply(AuctionTerms terms, string eventType)
    {
        if (eventType == "AuctionCancelled")
        {
            var was = terms.CancelledAt;
            terms.Cancel(DateTimeOffset.UtcNow);
            return was is null;
        }
        return terms.Advance(eventType);
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

        var written = existing ?? db.AuctionTerms.Local.First(t => t.AuctionId == payload.AuctionId);
        written.Name(payload.NameAr);
        if (_early.TryRemove(payload.AuctionId, out var early))
            lock (early)
                foreach (var eventType in early)
                    Apply(written, eventType);

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
                payload.ForfeitAll || forfeited.Contains(subscription.BidderId), now,
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
        public string? NameAr { get; init; }
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

        /// <summary>A cancellation that keeps every deposit, guarantees included.</summary>
        public bool ForfeitAll { get; init; }
    }
}
