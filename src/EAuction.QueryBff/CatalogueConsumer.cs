using System.Text.Json;
using EAuction.Core;

namespace EAuction.QueryBff;

/// <summary>
/// Fills <see cref="CatalogueState"/> from the control topics.
///
/// Four topics, and deliberately not a fifth: this service never consumes
/// <c>auctions.sealed</c>, so the reserve price is unreachable from the public read
/// path by ACL rather than by care (D-23). It also never touches a bid topic — the
/// bid path is the hot path and a read API has no business on it.
///
/// Every applied record also pushes to <see cref="FanOut"/>. <c>bids.rejected</c> is
/// consumed for that reason alone: the processor has always published a bidder's
/// rejection there and, until this, nothing read it — so a bidder could not learn
/// why a bid failed, and the portal left a refused bid on screen marked "recorded"
/// indefinitely.
/// </summary>
public sealed class CatalogueConsumer(
    CatalogueState state,
    BidderNames names,
    LeaderLabels labels,
    FanOut fanOut,
    IEventStream events,
    ILogger<CatalogueConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// False until the control topics have been replayed. Readiness depends on it: a
    /// BFF answering with an empty catalogue reads as "there are no auctions", which
    /// is worse than a 503 — a citizen is told the municipality is selling nothing.
    /// </summary>
    public bool Warm { get; private set; }

    /// <summary>
    /// How long to wait before a stable catalogue is allowed to count as a replayed
    /// one.
    ///
    /// Without a floor, "the count stopped changing" is indistinguishable from "the
    /// count has not started changing yet": a Kafka consumer takes seconds to join
    /// its group and begin delivering. This service declared itself warm with zero
    /// auctions, the readiness probe passed, and the bidder portal was told the
    /// catalogue was empty for an auction that had been approved a minute earlier.
    /// </summary>
    public TimeSpan MinimumWarmUp { get; init; } = TimeSpan.FromSeconds(8);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(
            FollowAsync(Topics.Upcoming, ApplyDefinition, ct),
            FollowAsync(Topics.CurrentWinner, ApplyPrice, ct),
            FollowAsync(Topics.Lifecycle, ApplyLifecycle, ct),
            FollowAsync(Topics.BidsRejected, ApplyRejection, ct),
            MarkWarmAsync(ct));
    }

    private async Task MarkWarmAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lastSeen = -1;
        var stableFor = 0;

        while (!ct.IsCancellationRequested && !Warm)
        {
            var seen = state.Count;
            stableFor = seen == lastSeen ? stableFor + 1 : 0;
            lastSeen = seen;

            if (stableFor >= 4 && DateTimeOffset.UtcNow - startedAt >= MinimumWarmUp)
            {
                Warm = true;
                logger.LogInformation("Catalogue warm: {Count} auction(s).", state.Count);
                return;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task FollowAsync(
        string topic, Action<StreamEvent> apply, CancellationToken ct)
    {
        try
        {
            await foreach (var record in events.ReadAsync(topic, ct))
            {
                try
                {
                    apply(record);
                }
                catch (JsonException e)
                {
                    // One unreadable record must not stop the topic: the rest of the
                    // catalogue is still serveable, and a stuck consumer is worse
                    // than a missing auction.
                    logger.LogError(e, "Unreadable {EventType} at {Topic}:{Offset}.",
                        record.EventType, topic, record.Offset);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private void ApplyDefinition(StreamEvent record)
    {
        if (record.EventType != "AuctionApproved") return;

        var p = JsonSerializer.Deserialize<AuctionApprovedPayload>(record.Payload, Json);
        if (p is null) return;

        state.Upsert(new AuctionEntry
        {
            AuctionId = p.AuctionId,
            NameAr = p.NameAr,
            NameEn = p.NameEn,
            Channel = p.Channel,
            BidderVisibility = p.BidderVisibility ?? "Masked",
            StartsAt = p.StartsAt,
            EndsAt = p.EndsAt,
            OpeningPriceMinorUnits = p.OpeningPriceMinorUnits,
            MinIncrementMinorUnits = p.MinIncrementMinorUnits,
            DepositMinorUnits = p.DepositMinorUnits,
            BookletPriceMinorUnits = p.BookletPriceMinorUnits,
            BrokerageFeePercent = p.BrokerageFeePercent,
            QuietPeriodSeconds = p.QuietPeriodSeconds,
            MaxExtensions = p.MaxExtensions,
            TotalAreaSqm = p.TotalAreaSqm,
            Plots = (p.Plots ?? [])
                .Select(x => new PlotEntry(
                    x.Id, x.DeedNumber, x.AreaSqm,
                    x.Latitude, x.Longitude, x.DescriptionAr, x.DescriptionEn))
                .ToArray(),
            CoverImageDocumentId = p.CoverImageDocumentId,
            Attachments = (p.Attachments ?? [])
                .Where(x => x.DocumentId != Guid.Empty && !string.IsNullOrWhiteSpace(x.TitleAr))
                .Select(x => new PublicDocumentEntry(x.DocumentId, x.TitleAr))
                .ToArray()
        });

        // The compacted topics replay concurrently, so this may be the record that
        // completes an auction whose price arrived first.
        if (state.TryGet(p.AuctionId, out var entry)) Push(entry);
    }

    /// <summary>
    /// Who may bid, and — only for an auction that names its bidders — what they are
    /// called (D-22).
    ///
    /// This service consumes <c>auctions.participants</c> for the name and nothing
    /// else; eligibility is the bid catcher's business, not the read path's. The
    /// name is dropped unless the auction says otherwise, which is the second of two
    /// locks: the participant service does not publish one for a masked auction in
    /// the first place.
    ///
    /// An eligibility for an auction this service has not seen yet is dropped rather
    /// than buffered. Registration follows approval, so the definition arrives first
    /// in practice — and on a replay both topics are read from offset 0, so a
    /// restart recovers anything a live race lost.
    /// </summary>
    private void ApplyParticipant(StreamEvent record)
    {
        if (record.EventType != "ParticipantEligibilityChanged") return;

        var p = JsonSerializer.Deserialize<ParticipantPayload>(record.Payload, Json);
        if (p is null) return;

        if (!p.Eligible || !state.TryGet(p.AuctionId, out var auction))
        {
            names.Forget(p.AuctionId, p.BidderId);
            return;
        }

        names.Remember(auction, p.BidderId, p.DisplayNameAr);
    }

    private void ApplyPrice(StreamEvent record)
    {
        if (record.EventType != "CurrentWinner") return;

        var p = JsonSerializer.Deserialize<CurrentWinnerPayload>(record.Payload, Json);
        if (p is null) return;

        var updated = state.SetPrice(
            p.AuctionId, p.PriceMinorUnits, p.LeaderBidderId, p.LeaderClientBidId,
            p.EffectiveEndsAt, p.ExtensionsUsed);

        // Null while this topic is replaying ahead of auctions.upcoming, which is
        // normal on a cold start: the definition arrives and brings its own push.
        if (updated is not null) Push(updated);
    }

    /// <summary>
    /// Sends a price change to everyone watching, in the two variants that exist.
    ///
    /// Pushed from here rather than from the endpoint, so a price reaches a watcher
    /// because the processor published it and not because the watcher asked.
    /// </summary>
    private void Push(AuctionEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var label = labels.For(entry);

        fanOut.PublishPrice(
            entry.AuctionId,
            LiveViews.Serialise(LiveViews.ForOthers(entry, label, now)),
            LiveViews.Serialise(LiveViews.ForLeader(entry, label, now)),
            entry.LeaderBidderId);
    }

    private void ApplyRejection(StreamEvent record)
    {
        if (record.EventType != "BidRejected") return;

        var p = JsonSerializer.Deserialize<BidRejectedPayload>(record.Payload, Json);
        if (p is null) return;

        var minimumNext = state.TryGet(p.AuctionId, out var entry)
            ? entry.MinimumNextBidMinorUnits
            : p.CurrentPriceMinorUnits;

        fanOut.PublishVerdict(p.AuctionId, p.BidderId, LiveViews.Serialise(
            new BidVerdictView(
                p.AuctionId, p.ClientBidId, Accepted: false, p.Reason,
                p.CurrentPriceMinorUnits, minimumNext, DateTimeOffset.UtcNow)));
    }

    private void ApplyLifecycle(StreamEvent record)
    {
        if (record.EventType == "AuctionCancelled")
        {
            if (!Guid.TryParse(record.Key, out var cancelledId)) return;
            var reason = JsonSerializer.Deserialize<CancelledPayload>(record.Payload, Json)?.Reason ?? "";
            if (state.MarkCancelled(cancelledId, reason) is { } cancelled) Push(cancelled);
            fanOut.Forget(cancelledId);
            return;
        }

        var status = record.EventType switch
        {
            "AuctionStarted" => "Live",
            "AuctionClosed" => "Closed",
            "CandidateOffered" => "PendingAward",
            "LadderExhausted" => "Unsold",
            _ => null
        };
        if (status is null) return;

        var p = JsonSerializer.Deserialize<LifecyclePayload>(record.Payload, Json);
        if (p is null) return;

        var updated = state.SetStatus(p.AuctionId, status, p.EffectiveEndsAt);
        if (updated is null) return;

        // Watchers need the close as much as they need a price: it is what turns the
        // bid box off, and a portal that only learned about it by polling would keep
        // offering to bid on an auction that had ended.
        Push(updated);

        // An auction past its award is nobody's live view any more, and its verdict
        // buffers are memory held for bidders who will not come back for them.
        if (status is "Unsold") fanOut.Forget(p.AuctionId);
    }

    // Local payload shapes rather than a shared contracts package: this service reads
    // the topics as a stranger would, so a producer-side field it does not know about
    // cannot break it.
    private sealed record AuctionApprovedPayload
    {
        public Guid AuctionId { get; init; }
        public string NameAr { get; init; } = "";
        public string NameEn { get; init; } = "";
        public string Channel { get; init; } = "";
        public string? BidderVisibility { get; init; }
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public long OpeningPriceMinorUnits { get; init; }
        public long MinIncrementMinorUnits { get; init; }
        public long DepositMinorUnits { get; init; }
        public long BookletPriceMinorUnits { get; init; }
        public decimal BrokerageFeePercent { get; init; }
        public int? QuietPeriodSeconds { get; init; }
        public int MaxExtensions { get; init; }
        public decimal TotalAreaSqm { get; init; }
        public PlotPayload[]? Plots { get; init; }
        public Guid? CoverImageDocumentId { get; init; }
        public AttachmentPayload[]? Attachments { get; init; }
    }

    private sealed record AttachmentPayload
    {
        public Guid DocumentId { get; init; }
        public string TitleAr { get; init; } = "";
    }

    private sealed record PlotPayload
    {
        public Guid Id { get; init; }
        public string DeedNumber { get; init; } = "";
        public decimal AreaSqm { get; init; }
        public string? Latitude { get; init; }
        public string? Longitude { get; init; }
        public string? DescriptionAr { get; init; }
        public string? DescriptionEn { get; init; }
    }

    private sealed record CurrentWinnerPayload
    {
        public Guid AuctionId { get; init; }
        public long PriceMinorUnits { get; init; }
        public Guid? LeaderBidderId { get; init; }
        public Guid? LeaderClientBidId { get; init; }
        public DateTimeOffset EffectiveEndsAt { get; init; }
        public int ExtensionsUsed { get; init; }
    }

    private sealed record BidRejectedPayload
    {
        public Guid AuctionId { get; init; }
        public Guid BidderId { get; init; }
        public Guid ClientBidId { get; init; }
        public string Reason { get; init; } = "";
        public long CurrentPriceMinorUnits { get; init; }
    }

    private sealed record ParticipantPayload
    {
        public Guid AuctionId { get; init; }
        public Guid BidderId { get; init; }
        public bool Eligible { get; init; }
        public string? DisplayNameAr { get; init; }
    }

    private sealed record CancelledPayload
    {
        public string Reason { get; init; } = "";
    }

    private sealed record LifecyclePayload
    {
        public Guid AuctionId { get; init; }
        public DateTimeOffset? EffectiveEndsAt { get; init; }
    }
}
