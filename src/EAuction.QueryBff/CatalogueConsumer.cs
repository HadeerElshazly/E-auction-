using System.Text.Json;
using EAuction.Core;

namespace EAuction.QueryBff;

/// <summary>
/// Fills <see cref="CatalogueState"/> from the control topics.
///
/// Three topics, and deliberately not a fourth: this service never consumes
/// <c>auctions.sealed</c>, so the reserve price is unreachable from the public read
/// path by ACL rather than by care (D-23). It also never touches a bid topic — the
/// bid path is the hot path and a read API has no business on it.
/// </summary>
public sealed class CatalogueConsumer(
    CatalogueState state,
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
            StartsAt = p.StartsAt,
            EndsAt = p.EndsAt,
            OpeningPriceMinorUnits = p.OpeningPriceMinorUnits,
            MinIncrementMinorUnits = p.MinIncrementMinorUnits,
            DepositMinorUnits = p.DepositMinorUnits,
            BookletPriceMinorUnits = p.BookletPriceMinorUnits,
            QuietPeriodSeconds = p.QuietPeriodSeconds,
            MaxExtensions = p.MaxExtensions,
            TotalAreaSqm = p.TotalAreaSqm,
            Plots = (p.Plots ?? [])
                .Select(x => new PlotEntry(
                    x.Id, x.DeedNumber, x.AreaSqm,
                    x.Latitude, x.Longitude, x.DescriptionAr, x.DescriptionEn))
                .ToArray()
        });
    }

    private void ApplyPrice(StreamEvent record)
    {
        if (record.EventType != "CurrentWinner") return;

        var p = JsonSerializer.Deserialize<CurrentWinnerPayload>(record.Payload, Json);
        if (p is null) return;

        state.SetPrice(
            p.AuctionId, p.PriceMinorUnits, p.LeaderBidderId, p.EffectiveEndsAt, p.ExtensionsUsed);
    }

    private void ApplyLifecycle(StreamEvent record)
    {
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

        state.SetStatus(p.AuctionId, status, p.EffectiveEndsAt);
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
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public long OpeningPriceMinorUnits { get; init; }
        public long MinIncrementMinorUnits { get; init; }
        public long DepositMinorUnits { get; init; }
        public long BookletPriceMinorUnits { get; init; }
        public int? QuietPeriodSeconds { get; init; }
        public int MaxExtensions { get; init; }
        public decimal TotalAreaSqm { get; init; }
        public PlotPayload[]? Plots { get; init; }
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
        public DateTimeOffset EffectiveEndsAt { get; init; }
        public int ExtensionsUsed { get; init; }
    }

    private sealed record LifecyclePayload
    {
        public Guid AuctionId { get; init; }
        public DateTimeOffset? EffectiveEndsAt { get; init; }
    }
}
