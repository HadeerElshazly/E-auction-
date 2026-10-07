using System.Text.Json;
using EAuction.Audit.Domain;
using EAuction.Audit.Persistence;
using EAuction.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EAuction.Audit.Integration;

/// <summary>
/// Projects the platform's own actions into <see cref="SystemEvent"/>: eligibility
/// from <c>auctions.participants</c>, money from <c>payments.settlements</c>, and the
/// processor's refusals from <c>bids.rejected</c>. Read-only on all three; its own
/// consumer group, so it never shares offsets with the staff trail's consumer.
/// </summary>
public sealed class SystemEventsConsumer(
    IDbContextFactory<AuditDbContext> dbFactory,
    IEventStream events,
    ILogger<SystemEventsConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(
        FollowAsync(Topics.Participants, ApplyEligibilityAsync, ct),
        FollowAsync(Topics.Settlements, ApplySettlementAsync, ct),
        FollowAsync(Topics.BidsRejected, ApplyRejectionAsync, ct));

    private async Task FollowAsync(
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
                catch (JsonException e)
                {
                    logger.LogError(e, "Unreadable {EventType} at {Topic}:{Offset}.",
                        record.EventType, topic, record.Offset);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task ApplyEligibilityAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "ParticipantEligibilityChanged") return;
        var p = JsonSerializer.Deserialize<EligibilityPayload>(record.Payload, Json);
        if (p is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A key rotation republishes eligibility unchanged; only a change is news.
        var last = await db.SystemEvents.AsNoTracking()
            .Where(e => e.AuctionId == p.AuctionId && e.BidderId == p.BidderId
                && (e.Kind == SystemEventKinds.EligibilityGranted || e.Kind == SystemEventKinds.EligibilityWithdrawn)
                && !(e.Topic == record.Topic && e.Key == record.Key && e.Offset == record.Offset))
            .OrderByDescending(e => e.Id)
            .Select(e => e.Kind)
            .FirstOrDefaultAsync(ct);
        var kind = p.Eligible ? SystemEventKinds.EligibilityGranted : SystemEventKinds.EligibilityWithdrawn;
        if (last == kind || (last is null && !p.Eligible)) return;

        await SaveAsync(db, SystemEvent.From(record.Topic, record.Key, record.Offset, kind,
            record.Timestamp ?? DateTimeOffset.UtcNow, p.AuctionId, p.BidderId), ct);
    }

    private async Task ApplySettlementAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "PaymentSettled") return;
        var p = JsonSerializer.Deserialize<SettlementPayload>(record.Payload, Json);
        if (p is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await SaveAsync(db, SystemEvent.From(record.Topic, record.Key, record.Offset,
            SystemEventKinds.Payment, p.At == default ? record.Timestamp ?? DateTimeOffset.UtcNow : p.At,
            p.AuctionId, p.BidderId, p.Purpose, p.Outcome, p.AmountMinorUnits,
            string.IsNullOrWhiteSpace(p.Reference) ? null : p.Reference, p.FailureReason), ct);
    }

    private async Task ApplyRejectionAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "BidRejected") return;
        var p = JsonSerializer.Deserialize<RejectionPayload>(record.Payload, Json);
        if (p is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await SaveAsync(db, SystemEvent.From(record.Topic, record.Key, record.Offset,
            SystemEventKinds.BidRejected, record.Timestamp ?? DateTimeOffset.UtcNow,
            p.AuctionId, p.BidderId, reason: p.Reason, clientBidId: p.ClientBidId,
            amount: p.CurrentPriceMinorUnits), ct);
    }

    private static async Task SaveAsync(AuditDbContext db, SystemEvent e, CancellationToken ct)
    {
        db.SystemEvents.Add(e);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Already projected: a replay after a restart.
        }
    }

    private sealed record EligibilityPayload(Guid AuctionId, Guid BidderId, bool Eligible);

    private sealed record SettlementPayload
    {
        public Guid AuctionId { get; init; }
        public Guid BidderId { get; init; }
        public string? Purpose { get; init; }
        public string? Outcome { get; init; }
        public long AmountMinorUnits { get; init; }
        public string? Reference { get; init; }
        public string? FailureReason { get; init; }
        public DateTimeOffset At { get; init; }
    }

    private sealed record RejectionPayload(
        Guid AuctionId, Guid BidderId, Guid ClientBidId, string Reason, long CurrentPriceMinorUnits);
}
