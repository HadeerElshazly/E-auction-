using System.Text.Json;
using EAuction.Core;
using EAuction.Reporting.Domain;
using EAuction.Reporting.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EAuction.Reporting.Integration;

/// <summary>
/// Builds the reporting read model from the control topics.
///
/// Four topics, and no watermark. That absence is the design: the notification
/// service needs one because a notice is not idempotent to a person, and the
/// payment service writes a marker because charging twice costs money — but a
/// report is pure state, so replaying every topic from offset 0 is not merely
/// harmless here, it is how the model is built. Drop the database, restart, and the
/// reports come back.
///
/// <para>
/// Two things make that true rather than hoped for. Every write is an upsert on a
/// natural key, so a replay rewrites rather than appends; and
/// <see cref="AuctionRecord.Reach"/> never lets an outcome retreat, so re-reading
/// <c>AuctionStarted</c> for an auction that settled months ago does not report it
/// as live.
/// </para>
/// </summary>
public sealed class ReportingConsumer(
    IDbContextFactory<ReportingDbContext> dbFactory,
    IEventStream events,
    ILogger<ReportingConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True once every topic has been followed for at least one pass.</summary>
    public bool Ready { get; private set; }

    /// <summary>
    /// Serialises the writes.
    ///
    /// Four topics are followed concurrently and they touch the same rows — an
    /// auction's definition from one, its outcome from another, its bidders from a
    /// third. Two handlers reading the same row and saving it would lose one of the
    /// two updates, which in a report looks like an auction that never closed.
    /// A lock rather than per-row optimistic concurrency because the volume is a
    /// few records per auction and the simpler thing is the right size here.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private (string Topic, Func<StreamEvent, CancellationToken, Task> Handle)[] Topics() =>
    [
        // The definition first, so a lifecycle event for an auction this service has
        // not seen yet is the exception rather than the rule. Nothing waits for it —
        // see EnsureAuctionAsync for what happens when it does arrive out of order.
        (Core.Topics.Upcoming, ApplyDefinitionAsync),
        (Core.Topics.Lifecycle, ApplyLifecycleAsync),
        (Core.Topics.Participants, ApplyEligibilityAsync),
        (Core.Topics.Settlements, ApplySettlementAsync),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topics = Topics();
        Ready = true;

        await Task.WhenAll(topics.Select(t => Follow(t.Topic, t.Handle, stoppingToken)));
    }

    private async Task Follow(
        string topic, Func<StreamEvent, CancellationToken, Task> handle, CancellationToken ct)
    {
        await foreach (var record in events.ReadAsync(topic, ct))
        {
            try
            {
                await _gate.WaitAsync(ct);
                try { await handle(record, ct); }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Logged and skipped, which is the opposite of the audit consumer's
                // choice and right for the opposite reason. Nothing here is chained,
                // so one bad record costs one row in a report; stopping would cost
                // every row. The topics are replayed from the start on the next
                // restart, so a skipped record is not necessarily lost either.
                logger.LogError(
                    e, "Reporting: {EventType} at offset {Offset} of {Topic} was not applied.",
                    record.EventType, record.Offset, topic);
            }
        }
    }

    // --- auctions.upcoming -----------------------------------------------------

    private async Task ApplyDefinitionAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.AuctionApproved) return;

        var payload = JsonSerializer.Deserialize<AuctionApprovedPayload>(record.Payload, Json);
        if (payload is null || payload.AuctionId == Guid.Empty) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var auction = await db.Auctions.FindAsync([payload.AuctionId], ct);
        if (auction is null) db.Auctions.Add(AuctionRecord.From(payload));
        else auction.Redefine(payload);

        // The plots by their own ids, so a package that was edited between two
        // approvals does not leave the removed ones behind. An auction can only be
        // approved once, so in practice this runs once per auction and again on
        // every replay — which is exactly why it has to be an upsert.
        var existing = await db.Plots
            .Where(p => p.AuctionId == payload.AuctionId)
            .ToDictionaryAsync(p => p.PlotId, ct);

        foreach (var plot in payload.Plots)
        {
            if (existing.TryGetValue(plot.Id, out var found)) found.Redefine(plot);
            else db.Plots.Add(PlotRecord.From(payload.AuctionId, plot));
        }

        foreach (var (id, stale) in existing)
            if (payload.Plots.All(p => p.Id != id)) db.Plots.Remove(stale);

        await SaveAsync(db, ct);
    }

    // --- auctions.lifecycle ----------------------------------------------------

    private async Task ApplyLifecycleAsync(StreamEvent record, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        switch (record.EventType)
        {
            case InboundEvents.AuctionStarted:
            {
                var p = Read<AuctionStartedPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                a?.Started(p!.At);
                break;
            }

            case InboundEvents.AuctionClosed:
            {
                var p = Read<AuctionClosedPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                a?.Closed(p!);
                break;
            }

            case InboundEvents.CandidateOffered:
            {
                var p = Read<CandidateOfferedPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                a?.CandidateOffered();
                break;
            }

            case InboundEvents.AwardConfirmed:
            {
                var p = Read<AwardConfirmedPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                if (a is null || p is null) break;

                a.Awarded(p);

                // The winner's own row, so the participation report can say who took
                // it without joining back through the auction.
                var winner = await BidderAsync(db, p.AuctionId, p.WinnerBidderId, ct);
                winner.WonIt();
                break;
            }

            case InboundEvents.WinnerDisqualified:
            {
                var p = Read<WinnerDisqualifiedPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                if (a is null || p is null) break;

                a.WinnerDisqualified();

                // The event carries no timestamp, so this is when the report learned
                // of it. Close enough for a disqualification date and recorded as
                // such — the award's own ConfirmedAt was added to the event precisely
                // because that one had to be exact.
                var loser = await BidderAsync(db, p.AuctionId, p.BidderId, ct);
                loser.Disqualified(p, DateTimeOffset.UtcNow);
                break;
            }

            // Two names for one outcome: the committee marking it unsold, and the
            // processor finding nobody left who clears the reserve.
            case InboundEvents.AuctionUnsold:
            case InboundEvents.LadderExhausted:
            {
                var p = Read<AuctionIdPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                a?.Unsold(DateTimeOffset.UtcNow);
                break;
            }

            case InboundEvents.AuctionSettled:
            {
                var p = Read<AuctionSettledPayload>(record);
                var a = await FindAsync(db, p?.AuctionId, ct);
                if (a is null || p is null) break;

                a.Settled(p);
                (await BidderAsync(db, p.AuctionId, p.WinnerBidderId, ct)).WonIt();
                break;
            }

            case InboundEvents.AuctionRejected:
            {
                var p = Read<AuctionRejectedPayload>(record);
                if (p is null || p.AuctionId == Guid.Empty) break;

                // The only lifecycle event for an auction that never reached
                // auctions.upcoming, because rejection is what stops it getting
                // there. Without a definition there is nothing to report but the
                // rejection itself, which is still worth a row: "we prepared eleven
                // auctions this quarter and rejected two" is a report.
                var now = DateTimeOffset.UtcNow;
                var a = await db.Auctions.FindAsync([p.AuctionId], ct);

                if (a is null)
                {
                    var placeholder = AuctionRecord.From(new AuctionApprovedPayload
                    {
                        AuctionId = p.AuctionId,
                        NameAr = "(مرفوض قبل النشر)",
                        NameEn = "(rejected before publication)",
                    });

                    // Given a date, because the alternative is 0001-01-01 and the
                    // reports date an auction that never opened by its schedule —
                    // so a rejection would sort to the beginning of time and fall
                    // out of every report with a `from` filter.
                    placeholder.ScheduleUnknown(now);
                    placeholder.Rejected(p.Reason, now);
                    db.Auctions.Add(placeholder);
                }
                else
                {
                    a.Rejected(p.Reason, now);
                }
                break;
            }

            default:
                return;
        }

        await SaveAsync(db, ct);
    }

    // --- auctions.participants -------------------------------------------------

    private async Task ApplyEligibilityAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.ParticipantEligibilityChanged) return;

        var p = Read<EligibilityPayload>(record);
        if (p is null || p.AuctionId == Guid.Empty || p.BidderId == Guid.Empty) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The topic carries no timestamp, so eligibility is dated when this service
        // saw it. On a cold replay of a compacted topic that is "now" for every
        // bidder at once, which is a real limitation of this column and is said so
        // in §35 — the funnel's *counts* are exact, its dates are not.
        (await BidderAsync(db, p.AuctionId, p.BidderId, ct)).Eligibility(p, DateTimeOffset.UtcNow);

        await SaveAsync(db, ct);
    }

    // --- payments.settlements --------------------------------------------------

    private async Task ApplySettlementAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.PaymentSettled) return;

        var p = Read<PaymentSettledPayload>(record);
        if (p is null || p.AuctionId == Guid.Empty || p.BidderId == Guid.Empty) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Keyed on the topic offset, so a replay collides instead of double-counting.
        // Checked before the aggregate below is touched, because the aggregate's
        // arithmetic is the thing a double-count would corrupt.
        if (await db.Settlements.AnyAsync(s => s.Offset == record.Offset, ct)) return;

        db.Settlements.Add(SettlementRecord.From(p, record.Offset));
        (await BidderAsync(db, p.AuctionId, p.BidderId, ct)).Settlement(p);

        await SaveAsync(db, ct);
    }

    // --- helpers ---------------------------------------------------------------

    private static T? Read<T>(StreamEvent record) where T : class =>
        JsonSerializer.Deserialize<T>(record.Payload, Json);

    private static async Task<AuctionRecord?> FindAsync(
        ReportingDbContext db, Guid? auctionId, CancellationToken ct) =>
        auctionId is null || auctionId == Guid.Empty
            ? null
            : await db.Auctions.FindAsync([auctionId.Value], ct);

    /// <summary>
    /// The bidder's row for this auction, created if this is the first thing known
    /// about them.
    ///
    /// Created rather than skipped, because the four topics arrive in no particular
    /// order between them: a settlement can reach this service before the
    /// eligibility it paid for. A row that exists with only a payment on it is a
    /// true statement about a bidder who paid and is not yet eligible, which is
    /// precisely a line in the funnel.
    /// </summary>
    private static async Task<BidderRecord> BidderAsync(
        ReportingDbContext db, Guid auctionId, Guid bidderId, CancellationToken ct)
    {
        var found = await db.Bidders.FindAsync([auctionId, bidderId], ct);
        if (found is not null) return found;

        var created = BidderRecord.For(auctionId, bidderId);
        db.Bidders.Add(created);
        return created;
    }

    private static async Task SaveAsync(ReportingDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            // Another replica inserted the same row. Every value here is derived
            // from the record, so the row that won says the same thing as the one
            // that lost.
        }
    }
}
