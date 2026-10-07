using System.Collections.Concurrent;
using System.Text.Json;
using EAuction.Core;
using EAuction.Notifications.Delivery;
using EAuction.Notifications.Domain;
using EAuction.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EAuction.Notifications.Integration;

/// <summary>
/// Turns the events that matter to a bidder into things the bidder is told.
///
/// Six topics, nine kinds of notice. The rule for what belongs here is narrow: a
/// bidder has to act, or money moved, or the thing they were waiting for happened.
/// Everything else is noise, and a notification channel people learn to ignore is
/// worse than no channel — the one notice that mattered arrives in the same list.
/// </summary>
public sealed class EventConsumer(
    IDbContextFactory<NotificationsDbContext> dbFactory,
    INotificationChannel channel,
    IEventStream events,
    ILogger<EventConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Who led each auction last time this service looked.
    ///
    /// In memory rather than stored, and deliberately empty on a cold start: the
    /// first <c>CurrentWinner</c> seen for an auction establishes a baseline and
    /// notifies nobody. Without that, a restart during a live auction would replay
    /// the compacted topic and tell whoever led at each step that they had been
    /// outbid — a storm of notices about an auction they are probably still winning.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Guid> _leaders = new();

    /// <summary>
    /// How long the first-run absorb may spend reading one topic's history before
    /// it gives up on the rest of it.
    ///
    /// A bound, not the mechanism. The watermark is set to the topic's end offset
    /// before a single record is read, so whatever this cuts short is history that
    /// will still never be announced — only the roster and the auction names are
    /// left less complete, and the next real event fills them in.
    /// </summary>
    public TimeSpan FirstRunTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public bool Ready { get; private set; }

    /// <summary>
    /// Every topic and the handler that reads it, in one list.
    ///
    /// Built here rather than held in a field because a field initialiser cannot
    /// reference an instance method — and one list is what makes the first-run
    /// drain and the ordinary follow provably cover the same topics, rather
    /// than two lists that drift the first time a seventh is added.
    ///
    /// The auction catalogue is first on purpose: a notice that cannot name its
    /// auction is a worse notice, and this is the only ordering that can be had
    /// for free. Nothing waits for it.
    /// </summary>
    private (string Topic, Func<StreamEvent, bool, CancellationToken, Task> Handle)[] Topics() =>
    [
        (Core.Topics.Upcoming, ApplyAuctionAsync),
        (Core.Topics.Participants, ApplyEligibilityAsync),
        (Core.Topics.Settlements, ApplySettlementAsync),
        (Core.Topics.Lifecycle, ApplyLifecycleAsync),
        (Core.Topics.CurrentWinner, ApplyCurrentWinnerAsync),
        (Core.Topics.Deposits, ApplyDepositsAsync),
        (Core.Topics.Inquiries, ApplyInquiriesAsync),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topics = Topics();

        await AbsorbFirstRunHistoryAsync(topics, stoppingToken);
        Ready = true;

        await Task.WhenAll(topics.Select(t => Follow(t.Topic, t.Handle, stoppingToken)));
    }

    private async Task<long> WatermarkOf(string topic, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Watermarks.FindAsync(new object?[] { topic }, ct);
        return row?.Offset ?? -1;
    }

    private async Task AdvanceWatermarkAsync(string topic, long offset, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Watermarks.FindAsync(new object?[] { topic }, ct);

        if (row is null)
            db.Watermarks.Add(new TopicWatermark(topic, offset, DateTimeOffset.UtcNow));
        else
            row.Advance(offset, DateTimeOffset.UtcNow);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            // Two readers reached the same topic's first record at once. The next
            // record's advance will carry it.
        }
    }

    /// <summary>
    /// On a genuinely new database, swallows every topic's history and tells nobody
    /// anything.
    ///
    /// This is not an optimisation. These topics carry the platform's whole
    /// history: <c>auctions.participants</c> is compacted and holds an eligibility
    /// row for every bidder of every auction there has ever been. A service
    /// deployed onto an existing cluster reads all of it, and without this it sends
    /// every one of those bidders a notice about an auction that closed months ago.
    ///
    /// <para>
    /// The watermark is set to each topic's end offset <em>before</em> anything is
    /// read, and that order is the whole correctness argument: nothing at or below
    /// it can be announced even if the absorb below is cut short, crashes, or is
    /// never reached. The absorb is best-effort enrichment; the suppression is
    /// exact.
    /// </para>
    ///
    /// <para>
    /// The first version waited for each topic to go quiet instead, and sent
    /// seventeen "you are eligible" notices for four registrations — then, once
    /// that was fixed with a watermark, sent the whole history again because a
    /// Kafka consumer needs longer than two seconds to be assigned a partition, so
    /// the drain saw nothing and recorded no watermark at all. An empty topic and a
    /// broker that has not answered yet are indistinguishable by waiting, which is
    /// why <see cref="IEventStream.LatestOffsetAsync"/> exists.
    /// </para>
    ///
    /// <para>
    /// Only on a cold start, and that distinction is the point. After a restart the
    /// database already holds what was sent, so the unique index (D-40) suppresses
    /// everything already notified and what remains is what happened while the
    /// service was down — which is exactly what it should catch up on and send. An
    /// unconditional suppression would lose those.
    /// </para>
    /// </summary>
    private async Task AbsorbFirstRunHistoryAsync(
        (string Topic, Func<StreamEvent, bool, CancellationToken, Task> Handle)[] topics,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Watermarks.AnyAsync(ct))
        {
            logger.LogInformation(
                "Notifications: watermarks found, so the topics are read as a "
                + "catch-up rather than a first run.");
            return;
        }

        foreach (var (topic, handle) in topics)
            await AbsorbAsync(topic, handle, ct);

        logger.LogInformation(
            "Notifications: first run. History absorbed for {Auctions} auction(s) and "
            + "{Audience} registration(s); nothing was announced.",
            await db.AuctionNames.CountAsync(ct), await db.Audience.CountAsync(ct));
    }

    private async Task AbsorbAsync(
        string topic, Func<StreamEvent, bool, CancellationToken, Task> handle, CancellationToken ct)
    {
        var end = await events.LatestOffsetAsync(topic, ct);

        if (end < 0)
        {
            logger.LogInformation("First run: {Topic} is empty.", topic);
            return;
        }

        // Before reading anything. See the note on AbsorbFirstRunHistoryAsync: this
        // order is what makes the suppression exact regardless of what follows.
        await AdvanceWatermarkAsync(topic, end, ct);

        var absorbed = 0;

        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(FirstRunTimeout);

            await foreach (var record in events.ReadAsync(topic, bounded.Token))
            {
                try
                {
                    await handle(record, false, bounded.Token);
                    absorbed++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e,
                        "First run: failed to absorb {EventType} on {Topic}:{Offset}.",
                        record.EventType, topic, record.Offset);
                }

                if (record.Offset >= end) break;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "First run: {Topic} was still being read after {Timeout}; {Absorbed} of its "
                + "records were absorbed. Nothing up to offset {End} will be announced.",
                topic, FirstRunTimeout, absorbed, end);
        }

        logger.LogInformation(
            "First run: absorbed {Absorbed} record(s) from {Topic} up to offset {End}.",
            absorbed, topic, end);
    }

    /// <summary>
    /// Reads a topic and announces only what is above the watermark.
    ///
    /// The read starts at offset 0 every time — that is what <c>IEventStream</c>
    /// does, by design — so this pass sees the whole history again, including
    /// everything the first-run drain absorbed. The offset comparison is what stops
    /// it being announced the second time round, and getting that wrong is how the
    /// first version of this service sent seventeen "you are eligible" notices for
    /// four registrations: the drain suppressed them and the follow, reading the
    /// same records from the beginning, did not.
    /// </summary>
    private async Task Follow(
        string topic,
        Func<StreamEvent, bool, CancellationToken, Task> handle,
        CancellationToken ct)
    {
        var watermark = await WatermarkOf(topic, ct);

        try
        {
            await foreach (var record in events.ReadAsync(topic, ct))
            {
                var isNews = record.Offset > watermark;

                try
                {
                    await handle(record, isNews, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One unreadable record must not stop the rest: everything
                    // behind it on this topic is somebody's notice.
                    logger.LogError(e, "Failed to handle {EventType} on {Topic}:{Offset}.",
                        record.EventType, topic, record.Offset);
                }

                if (isNews)
                {
                    watermark = record.Offset;
                    await AdvanceWatermarkAsync(topic, record.Offset, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // --- what it listens to -------------------------------------------------

    private async Task ApplyAuctionAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.AuctionApproved) return;

        var payload = JsonSerializer.Deserialize<AuctionApprovedPayload>(record.Payload, Json);
        if (payload is null || string.IsNullOrWhiteSpace(payload.NameAr)) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AuctionNames.FindAsync(new object?[] { payload.AuctionId }, ct);

        if (existing is null)
            db.AuctionNames.Add(new AuctionName(
                payload.AuctionId, payload.NameAr, DateTimeOffset.UtcNow));
        else
            existing.Rename(payload.NameAr, DateTimeOffset.UtcNow);

        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyEligibilityAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        if (record.EventType == InboundEvents.BankGuaranteeRejected)
        {
            var rejected = JsonSerializer.Deserialize<GuaranteeRejectedPayload>(record.Payload, Json);
            if (rejected is null) return;

            await using var gdb = await dbFactory.CreateDbContextAsync(ct);
            var auction = await NameOf(gdb, rejected.AuctionId, ct);
            var (gTitle, gBody) = Messages.GuaranteeRejected(auction, rejected.Reason);

            // One notice per refusal: a bidder refused twice was refused twice.
            await RaiseAsync(
                rejected.BidderId, rejected.AuctionId, NotificationKind.GuaranteeRejected,
                gTitle, gBody, DateTimeOffset.UtcNow, notify, ct,
                dedup: rejected.At.ToUnixTimeMilliseconds().ToString());
            return;
        }

        if (record.EventType != InboundEvents.ParticipantEligibilityChanged) return;

        var payload = JsonSerializer.Deserialize<EligibilityPayload>(record.Payload, Json);
        if (payload is null) return;

        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var row = await db.Audience.FindAsync(
            new object?[] { payload.AuctionId, payload.BidderId }, ct);

        // Whether this is news, decided before the row is changed. The topic is
        // compacted and republished on a key rotation, so the same eligibility
        // arrives again routinely and must not be announced again.
        var isNews = row is null || row.Eligible != payload.Eligible;

        if (row is null)
            db.Audience.Add(new Audience(payload.AuctionId, payload.BidderId, payload.Eligible, now));
        else
            row.Set(payload.Eligible, now);

        await db.SaveChangesAsync(ct);

        if (!isNews) return;

        var name = await NameOf(db, payload.AuctionId, ct);

        var (title, body) = payload.Eligible
            ? Messages.Eligible(name)
            : Messages.Revoked(name);

        await RaiseAsync(
            payload.BidderId, payload.AuctionId,
            payload.Eligible ? NotificationKind.Eligible : NotificationKind.Revoked,
            title, body, now, notify, ct);
    }

    private async Task ApplySettlementAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        // Only a refusal. A bidder does not need telling that a payment they just
        // made went through — the step completing is the news, and the eligibility
        // notice says it better.
        if (record.EventType != InboundEvents.PaymentSettled) return;

        var payload = JsonSerializer.Deserialize<SettlementPayload>(record.Payload, Json);
        if (payload is null || payload.Outcome != "Refused") return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var name = await NameOf(db, payload.AuctionId, ct);

        var (title, body) = Messages.PaymentRefused(name, payload.Purpose, payload.FailureReason);

        // Deduped on the settlement's own timestamp, which is in the payload and so
        // survives a replay. A second genuine refusal has a different one and is a
        // second notice, correctly: the bidder tried twice and failed twice.
        await RaiseAsync(
            payload.BidderId, payload.AuctionId, NotificationKind.PaymentRefused,
            title, body, DateTimeOffset.UtcNow, notify, ct,
            dedup: $"{payload.Purpose}:{payload.At.ToUnixTimeMilliseconds()}");
    }

    private async Task ApplyLifecycleAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        switch (record.EventType)
        {
            case InboundEvents.AuctionStarted:
            {
                var payload = JsonSerializer.Deserialize<AuctionIdPayload>(record.Payload, Json);
                if (payload is null) return;

                var name = await NameOf(db, payload.AuctionId, ct);
                var (title, body) = Messages.AuctionStarted(name);

                await RaiseForAudienceAsync(
                    db, payload.AuctionId, NotificationKind.AuctionStarted,
                    title, body, now, notify, ct);
                return;
            }

            // Every way an auction ends unawarded reaches auction-admin's MarkUnsold,
            // which emits this once — so one notice, whichever path got there.
            case InboundEvents.AuctionUnsold:
            {
                var payload = JsonSerializer.Deserialize<AuctionIdPayload>(record.Payload, Json);
                if (payload is null) return;

                var name = await NameOf(db, payload.AuctionId, ct);
                var (title, body) = Messages.AuctionUnsold(name);

                await RaiseForAudienceAsync(
                    db, payload.AuctionId, NotificationKind.AuctionUnsold,
                    title, body, now, notify, ct);
                return;
            }

            case InboundEvents.AuctionCancelled:
            {
                var payload = JsonSerializer.Deserialize<AuctionCancelledPayload>(record.Payload, Json);
                if (payload is null) return;

                var name = await NameOf(db, payload.AuctionId, ct);
                var (title, body) = Messages.AuctionCancelled(name, payload.Reason);

                await RaiseForAudienceAsync(
                    db, payload.AuctionId, NotificationKind.AuctionCancelled,
                    title, body, now, notify, ct);
                return;
            }

            case InboundEvents.AuctionClosed:
            {
                var payload = JsonSerializer.Deserialize<AuctionIdPayload>(record.Payload, Json);
                if (payload is null) return;

                var name = await NameOf(db, payload.AuctionId, ct);
                var (title, body) = Messages.AuctionClosed(name);

                await RaiseForAudienceAsync(
                    db, payload.AuctionId, NotificationKind.AuctionClosed,
                    title, body, now, notify, ct);
                return;
            }

            case InboundEvents.AwardConfirmed:
            {
                var payload = JsonSerializer.Deserialize<AwardConfirmedPayload>(record.Payload, Json);
                if (payload is null) return;

                var name = await NameOf(db, payload.AuctionId, ct);
                var (title, body) = Messages.Awarded(
                    name, payload.AmountMinorUnits, payload.ComplianceDeadline);

                // The winner only. Everyone else learns the auction closed, which
                // is all they are entitled to know (D-22): naming the winner to the
                // losers of a masked auction would undo the masking by notification.
                //
                // Deduped on the cascade: a disqualification moves the award to the
                // next bidder and that is a new award, not a repeat of this one.
                await RaiseAsync(
                    payload.WinnerBidderId, payload.AuctionId, NotificationKind.Awarded,
                    title, body, now, notify, ct,
                    dedup: payload.AmountMinorUnits.ToString());
                return;
            }

            case InboundEvents.WinnerDisqualified:
            {
                var payload = JsonSerializer.Deserialize<WinnerDisqualifiedPayload>(record.Payload, Json);
                if (payload is null) return;

                var name = await NameOf(db, payload.AuctionId, ct);
                var (title, body) = Messages.Disqualified(
                    name, payload.Reason, payload.DepositForfeited);

                await RaiseAsync(
                    payload.BidderId, payload.AuctionId, NotificationKind.Disqualified,
                    title, body, now, notify, ct, dedup: payload.Reason);
                return;
            }
        }
    }

    /// <summary>
    /// Tells whoever has just stopped leading.
    ///
    /// The live stream already tells a bidder who is watching the page, in
    /// milliseconds. This is for the one who closed the tab, and it is the single
    /// most useful thing this service sends.
    /// </summary>
    private async Task ApplyCurrentWinnerAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.CurrentWinner) return;

        var payload = JsonSerializer.Deserialize<CurrentWinnerPayload>(record.Payload, Json);
        if (payload?.LeaderBidderId is null) return;

        var leader = payload.LeaderBidderId.Value;

        // Baseline on first sight, and notify nobody: see _leaders.
        if (!_leaders.TryGetValue(payload.AuctionId, out var previous))
        {
            _leaders[payload.AuctionId] = leader;
            return;
        }

        _leaders[payload.AuctionId] = leader;

        // The same bidder raising their own lead is not an outbid. The engine
        // refuses that anyway (B-04), but a leader can reappear after a cascade.
        if (previous == leader) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var name = await NameOf(db, payload.AuctionId, ct);
        var (title, body) = Messages.Outbid(name, payload.PriceMinorUnits);

        // Deduped on the price that beat them, so four raises are four notices and
        // a redelivery of the same record is none.
        await RaiseAsync(
            previous, payload.AuctionId, NotificationKind.Outbid,
            title, body, DateTimeOffset.UtcNow, notify, ct,
            dedup: payload.PriceMinorUnits.ToString());
    }

    private async Task ApplyDepositsAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.DepositsReleasable) return;

        var payload = JsonSerializer.Deserialize<DepositsReleasablePayload>(record.Payload, Json);
        if (payload is null) return;

        var forfeited = (payload.ForfeitForBidders ?? []).ToHashSet();
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var name = await NameOf(db, payload.AuctionId, ct);

        var audience = await db.Audience
            .Where(a => a.AuctionId == payload.AuctionId)
            .Select(a => a.BidderId)
            .ToListAsync(ct);

        foreach (var bidder in audience)
        {
            var (title, body) =
                forfeited.Contains(bidder) ? Messages.DepositForfeited(name)
                : payload.AppliedToPurchaseForBidder == bidder ? Messages.DepositApplied(name)
                : Messages.DepositReturned(name);

            await RaiseAsync(
                bidder, payload.AuctionId, NotificationKind.DepositResolved,
                title, body, now, notify, ct);
        }
    }

    // --- what it does -------------------------------------------------------

    private static async Task<string> NameOf(
        NotificationsDbContext db, Guid auctionId, CancellationToken ct)
    {
        var name = await db.AuctionNames.FindAsync(new object?[] { auctionId }, ct);
        return string.IsNullOrWhiteSpace(name?.NameAr) ? Messages.UnnamedAuction : name.NameAr;
    }

    /// <summary>
    /// «الاستفسارات والإجابات»: the asker is told a reply arrived — not what it says,
    /// which they read in the portal — and the auction's bidders that a clarification
    /// was published.
    /// </summary>
    private async Task ApplyInquiriesAsync(StreamEvent record, bool notify, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        switch (record.EventType)
        {
            case "InquiryAnswered":
            {
                var p = JsonSerializer.Deserialize<InquiryAnsweredPayload>(record.Payload, Json);
                if (p is null) return;
                var (title, body) = Messages.InquiryAnswered(await NameOf(db, p.AuctionId, ct));
                await RaiseAsync(p.BidderId, p.AuctionId, NotificationKind.InquiryAnswered,
                    title, body, now, notify, ct, dedup: p.InquiryId.ToString());
                return;
            }
            case "ClarificationPublished":
            {
                var p = JsonSerializer.Deserialize<ClarificationPublishedPayload>(record.Payload, Json);
                if (p is null || !notify) return;
                var (title, body) = Messages.ClarificationPublished(await NameOf(db, p.AuctionId, ct));
                var audience = await db.Audience
                    .Where(a => a.AuctionId == p.AuctionId && a.Eligible)
                    .Select(a => a.BidderId)
                    .ToListAsync(ct);
                foreach (var bidder in audience)
                    await RaiseAsync(bidder, p.AuctionId, NotificationKind.ClarificationPublished,
                        title, body, now, notify, ct, dedup: p.ClarificationId.ToString());
                return;
            }
        }
    }

    private async Task RaiseForAudienceAsync(
        NotificationsDbContext db, Guid auctionId, NotificationKind kind,
        string title, string body, DateTimeOffset now, bool notify, CancellationToken ct)
    {
        if (!notify) return;

        // The eligible only. Someone whose eligibility was revoked is not waiting
        // for this auction to open.
        var audience = await db.Audience
            .Where(a => a.AuctionId == auctionId && a.Eligible)
            .Select(a => a.BidderId)
            .ToListAsync(ct);

        foreach (var bidder in audience)
            await RaiseAsync(bidder, auctionId, kind, title, body, now, notify, ct);
    }

    /// <summary>
    /// Stores one notification, once, and hands it to the outbound channel.
    ///
    /// Insert-then-swallow rather than check-then-insert: two replicas, or one
    /// replica reading two topics, reach this at the same moment for the same
    /// notice, and a check would pass for both. The unique index is the only thing
    /// that actually decides.
    /// </summary>
    private async Task RaiseAsync(
        Guid bidderId, Guid auctionId, NotificationKind kind,
        string title, string body, DateTimeOffset now, bool notify, CancellationToken ct,
        string dedup = "")
    {
        // The first-run drain absorbs state and announces nothing: see
        // AbsorbFirstRunHistoryAsync for why that is not an optimisation.
        if (!notify) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var notification = Notification.For(
            bidderId, auctionId, kind, title, body, now, dedup, Messages.Actionable(kind));

        db.Notifications.Add(notification);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            // Already told. The ordinary case on a replay, and not worth a log line
            // per bidder per restart.
            return;
        }

        if (await channel.SendAsync(notification, ct))
        {
            notification.MarkDispatched(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
        }
    }
}
