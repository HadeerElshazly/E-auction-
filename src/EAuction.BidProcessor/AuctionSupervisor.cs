using System.Collections.Concurrent;
using System.Text.Json;
using EAuction.Core;

namespace EAuction.BidProcessor;

public sealed record SupervisorOptions
{
    /// <summary>
    /// How long after the effective end to wait before freezing the result.
    ///
    /// The catcher accepts up to the hard ceiling, so bids can still be in
    /// flight when the clock passes the end. This grace lets them land and be
    /// rejected on the record rather than vanish.
    /// </summary>
    public TimeSpan CloseGrace { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Runs every live auction and closes the loop with auction-admin.
///
/// Inbound:  auctions.upcoming + auctions.sealed (definitions), and
///           auctions.lifecycle (WinnerDisqualified, to drive the cascade).
/// Outbound: auctions.current-winner, bids.rejected, and auctions.lifecycle
///           (AuctionStarted, AuctionClosed, CandidateOffered, LadderExhausted).
///
/// The committee still awards (D-08). This service only ever says who
/// qualifies; it never decides that anyone has won.
/// </summary>
public sealed class AuctionSupervisor(
    IBidLog bidLog,
    IEventStream events,
    SupervisorOptions options,
    ILogger<AuctionSupervisor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, RunningAuction> _running = new();

    public IReadOnlyCollection<RunningAuction> Running => _running.Values.ToList();

    public bool TryGet(Guid auctionId, out RunningAuction auction) =>
        _running.TryGetValue(auctionId, out auction!);

    /// <summary>Begins consuming an auction's bid stream. Idempotent.</summary>
    public RunningAuction Start(AuctionDefinition definition, CancellationToken ct)
    {
        return _running.GetOrAdd(definition.AuctionId, _ =>
        {
            var running = new RunningAuction(definition);

            running.Pump = new AuctionPump(
                definition, bidLog,
                async (verdict, token) => await OnVerdictAsync(running, verdict, token),
                (_, _, _) => ValueTask.CompletedTask);

            running.Task = Task.Run(() => running.Pump.RunAsync(0, ct), ct);
            return running;
        });
    }

    private async ValueTask OnVerdictAsync(
        RunningAuction running, BidVerdict verdict, CancellationToken ct)
    {
        running.RecordProcessed();

        if (verdict.Accepted)
        {
            await events.PublishAsync(Topics.CurrentWinner, verdict.AuctionId.ToString(),
                JsonSerializer.Serialize(new CurrentWinner
                {
                    AuctionId = verdict.AuctionId,
                    PriceMinorUnits = verdict.PriceAfterMinorUnits,
                    LeaderBidderId = running.Pump!.Engine.ProvisionalLeader,
                    EffectiveEndsAt = verdict.EffectiveEndsAt,
                    ExtensionsUsed = verdict.ExtensionsUsed
                }, Json),
                nameof(CurrentWinner), ct);
        }
        else
        {
            await events.PublishAsync(Topics.BidsRejected,
                $"{verdict.AuctionId}:{verdict.BidderId}",
                JsonSerializer.Serialize(new BidRejected
                {
                    AuctionId = verdict.AuctionId,
                    BidderId = verdict.BidderId,
                    ClientBidId = verdict.ClientBidId,
                    Reason = verdict.Reason.ToString(),
                    CurrentPriceMinorUnits = verdict.PriceAfterMinorUnits
                }, Json),
                nameof(BidRejected), ct);
        }
    }

    /// <summary>
    /// Advances the clock. Starts auctions that have reached their start time
    /// and closes those whose effective end plus grace has passed.
    ///
    /// Split out from a timer so tests drive it directly rather than sleeping.
    /// </summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var running in _running.Values)
        {
            if (!running.Announced && now >= running.Definition.StartsAt)
            {
                running.Announced = true;
                await events.PublishAsync(Topics.Lifecycle, running.Definition.AuctionId.ToString(),
                    JsonSerializer.Serialize(new AuctionStarted
                    {
                        AuctionId = running.Definition.AuctionId,
                        At = now
                    }, Json),
                    nameof(AuctionStarted), ct);
            }

            if (running.Closed) continue;

            // The engine moves EffectiveEndsAt as quiet-period extensions land,
            // so this is re-read every tick rather than scheduled once.
            var effectiveEnd = running.Pump!.Engine.EffectiveEndsAt;
            if (now < effectiveEnd + options.CloseGrace) continue;

            await CloseAsync(running, now, ct);
        }
    }

    private async Task CloseAsync(RunningAuction running, DateTimeOffset now, CancellationToken ct)
    {
        running.Closed = true;
        var engine = running.Pump!.Engine;

        logger.LogInformation(
            "Auction {AuctionId} closed at {Now:o} after {Extensions} extension(s); {Bids} bids processed.",
            running.Definition.AuctionId, now, engine.ExtensionsUsed, running.ProcessedBidCount);

        await events.PublishAsync(Topics.Lifecycle, running.Definition.AuctionId.ToString(),
            JsonSerializer.Serialize(new AuctionClosed
            {
                AuctionId = running.Definition.AuctionId,
                At = now,
                EffectiveEndsAt = engine.EffectiveEndsAt,
                ExtensionsUsed = engine.ExtensionsUsed,
                BidCount = running.ProcessedBidCount
            }, Json),
            nameof(AuctionClosed), ct);

        await OfferNextCandidateAsync(running, ct);
    }

    /// <summary>
    /// Offers the highest remaining bidder that clears the reserve, or reports
    /// the ladder exhausted. The reserve itself never leaves this service — the
    /// committee learns that someone qualifies, not what they had to beat.
    /// </summary>
    private async Task OfferNextCandidateAsync(RunningAuction running, CancellationToken ct)
    {
        var engine = running.Pump!.Engine;
        var candidate = engine.CascadeCandidates(running.Disqualified).FirstOrDefault();

        var auctionId = running.Definition.AuctionId;
        if (candidate.BidderId == Guid.Empty)
        {
            logger.LogInformation(
                "Auction {AuctionId}: no remaining bidder clears the reserve at cascade step {Step}.",
                auctionId, running.CascadeStep);

            await events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
                JsonSerializer.Serialize(new LadderExhausted
                {
                    AuctionId = auctionId,
                    CascadeStep = running.CascadeStep
                }, Json),
                nameof(LadderExhausted), ct);
            return;
        }

        await events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new CandidateOffered
            {
                AuctionId = auctionId,
                BidderId = candidate.BidderId,
                AmountMinorUnits = candidate.AmountMinorUnits,
                CascadeStep = running.CascadeStep
            }, Json),
            nameof(CandidateOffered), ct);
    }

    /// <summary>
    /// auction-admin disqualified the winner. The next qualifying bidder is
    /// offered, which is the processor's half of the cascade (§8.2).
    /// </summary>
    public async Task OnWinnerDisqualifiedAsync(
        WinnerDisqualifiedPayload payload, CancellationToken ct)
    {
        if (!_running.TryGetValue(payload.AuctionId, out var running))
        {
            logger.LogWarning(
                "Disqualification for unknown auction {AuctionId}; ignoring.", payload.AuctionId);
            return;
        }

        if (!running.Disqualified.Add(payload.BidderId))
        {
            // At-least-once delivery: a redelivered disqualification must not
            // advance the cascade a second time and skip a qualifying bidder.
            logger.LogInformation(
                "Bidder {BidderId} was already disqualified from {AuctionId}; ignoring duplicate.",
                payload.BidderId, payload.AuctionId);
            return;
        }

        running.CascadeStep++;
        await OfferNextCandidateAsync(running, ct);
    }

    public Task ApplyLifecycleAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != "WinnerDisqualified") return Task.CompletedTask;

        var payload = JsonSerializer.Deserialize<WinnerDisqualifiedPayload>(record.Payload, Json);
        return payload is null ? Task.CompletedTask : OnWinnerDisqualifiedAsync(payload, ct);
    }
}

/// <summary>One auction being run by the supervisor.</summary>
public sealed class RunningAuction(AuctionDefinition definition)
{
    private int _processed;

    public AuctionDefinition Definition { get; } = definition;
    public AuctionPump? Pump { get; internal set; }
    public Task? Task { get; internal set; }

    public bool Announced { get; internal set; }
    public bool Closed { get; internal set; }
    public int CascadeStep { get; internal set; }

    /// <summary>Bidders auction-admin has disqualified, skipped by the cascade.</summary>
    public HashSet<Guid> Disqualified { get; } = new();

    public int ProcessedBidCount => Volatile.Read(ref _processed);

    internal void RecordProcessed() => Interlocked.Increment(ref _processed);
}
