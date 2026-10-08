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

    public CheckpointPolicy Checkpoint { get; init; } = new();

    /// <summary>
    /// How long resume waits for a replay to rebuild an auction's ladder
    /// before giving up on re-issuing that auction's owed candidate offer.
    /// </summary>
    public TimeSpan ReplayTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs every live auction and closes the loop with auction-admin.
///
/// Startup has two phases. During <b>recovery</b> the supervisor replays its
/// own published events and the checkpoints to work out where it left off, and
/// takes no action. <see cref="ResumeAsync"/> then starts the bid pumps, and
/// from that point the supervisor acts on what it consumes.
///
/// The phases matter: without them a restart would re-announce auctions,
/// re-close them, and re-offer candidates the committee has already seen.
///
/// The committee still awards (D-08). This service only ever says who
/// qualifies; it never decides that anyone has won.
/// </summary>
public sealed class AuctionSupervisor(
    IBidLog bidLog,
    IEventStream events,
    CheckpointStore checkpoints,
    SupervisorOptions options,
    ILogger<AuctionSupervisor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, RunningAuction> _running = new();
    private readonly ConcurrentDictionary<Guid, RecoveredState> _recovered = new();

    /// <summary>
    /// Auctions an administrator withdrew before they opened. Kept apart from
    /// <see cref="_running"/> because the cancellation and the definition arrive on
    /// different topics in either order, and a cancelled auction must not be started
    /// by whichever of the two lands second.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = new();
    private volatile bool _resumed;

    public bool Resumed => _resumed;
    public IReadOnlyCollection<RunningAuction> Running => _running.Values.ToList();

    public bool TryGet(Guid auctionId, out RunningAuction auction) =>
        _running.TryGetValue(auctionId, out auction!);

    /// <summary>
    /// Registers an auction. The pump starts immediately once recovery is
    /// complete, and is held until <see cref="ResumeAsync"/> before that.
    /// </summary>
    public RunningAuction Start(AuctionDefinition definition, CancellationToken ct)
    {
        // Never registered, so never announced, launched or closed.
        if (_cancelled.ContainsKey(definition.AuctionId)) return new RunningAuction(definition);

        var running = _running.GetOrAdd(definition.AuctionId, _ =>
        {
            var created = new RunningAuction(definition);

            if (_recovered.TryGetValue(definition.AuctionId, out var state))
                state.ApplyTo(created);

            created.PublishedThrough = checkpoints.PublishedThrough(definition.AuctionId);
            return created;
        });

        if (_resumed) Launch(running, ct);
        return running;
    }

    /// <summary>
    /// Replaces an auction's definition before it opens (§6.5): the committee approved
    /// an amendment, and the dates, the opening price or the step may have moved. The
    /// pump is stopped and started again on the new terms — before the start there are
    /// no bids on the log, so nothing replays differently. Once announced, the auction
    /// runs on the terms it opened with and the new definition is logged and dropped:
    /// auction-admin refuses an amendment to an open auction, so reaching here after
    /// the start is a race with the clock, not a decision to honour.
    /// </summary>
    public void Redefine(AuctionDefinition definition, CancellationToken ct)
    {
        if (_cancelled.ContainsKey(definition.AuctionId)) return;

        if (!_running.TryGetValue(definition.AuctionId, out var current))
        {
            Start(definition, ct);
            return;
        }

        if (current.Announced)
        {
            logger.LogWarning(
                "Auction {AuctionId} was redefined after it opened; it keeps the terms it opened with.",
                definition.AuctionId);
            return;
        }

        current.Stop();
        _running.TryRemove(definition.AuctionId, out _);
        logger.LogInformation(
            "Auction {AuctionId} redefined before opening: now {Start:o} to {End:o}.",
            definition.AuctionId, definition.StartsAt, definition.EndsAt);

        Start(definition, ct);
    }

    private void Launch(RunningAuction running, CancellationToken ct)
    {
        if (running.Pump is not null) return;

        running.Pump = new AuctionPump(
            running.Definition, bidLog,
            async (verdict, token) => await OnVerdictAsync(running, verdict, token),
            (_, _, _) => ValueTask.CompletedTask);

        // Replayed clerk extensions, one call per extension the clerk actually made,
        // so the engine's MaxExtensions cap governs the replay exactly as it governed
        // the originals. Replaying the total as a single call would restore the right
        // end time while leaving the clerk extensions they had already spent.
        foreach (var seconds in running.ClerkExtensions)
            running.Pump.Engine.ExtendByClerk(TimeSpan.FromSeconds(seconds));

        // Always from offset 0: the engine's price, ladder, extensions and
        // ledger are rebuilt by replaying every bid, which is deterministic.
        // The checkpoint only decides which of those replayed bids stay silent.
        //
        // On the auction's own lifetime token as well as the host's, so a
        // redefinition can stop this pump without stopping every other auction.
        var lifetime = running.Lifetime(ct);
        running.Task = Task.Run(async () =>
        {
            try
            {
                await running.Pump.RunAsync(0, lifetime);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Stopped on purpose: the host shutting down, or the auction redefined.
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Ends the recovery phase: starts every pump and re-issues any candidate
    /// offer that was owed but never published.
    /// </summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        if (_resumed) return;
        _resumed = true;

        // Cancellations replayed after their definitions registered the auction.
        foreach (var id in _cancelled.Keys) Withdraw(id);

        foreach (var running in _running.Values)
        {
            Launch(running, ct);

            if (running.PublishedThrough >= 0)
                logger.LogInformation(
                    "Auction {AuctionId} resumes with offsets through {Offset} already published.",
                    running.Definition.AuctionId, running.PublishedThrough);
        }

        // A crash between recording a disqualification and publishing the next
        // offer would otherwise leave the committee waiting forever. One offer
        // is owed per close, plus one per disqualification.
        foreach (var running in _running.Values.Where(r => r.Closed))
        {
            var owed = 1 + running.CascadeStep;
            if (running.OffersPublished >= owed) continue;

            // The candidate comes from the ladder, which the replay is still
            // rebuilding. Reading it now would see an empty engine and report
            // the ladder exhausted on an auction that has a perfectly good
            // next bidder.
            if (!await WaitForReplayAsync(running, ct))
            {
                logger.LogError(
                    "Auction {AuctionId}: replay did not catch up; not re-issuing its owed offer.",
                    running.Definition.AuctionId);
                continue;
            }

            logger.LogWarning(
                "Auction {AuctionId} owes {Count} candidate offer(s) after restart; re-issuing.",
                running.Definition.AuctionId, owed - running.OffersPublished);

            await OfferNextCandidateAsync(running, ct);
        }
    }

    /// <summary>
    /// Blocks until the pump has consumed every record currently in the log,
    /// so the engine's ladder is whole before anything reads it.
    /// </summary>
    private async Task<bool> WaitForReplayAsync(RunningAuction running, CancellationToken ct)
    {
        var endOffset = await bidLog.GetEndOffsetAsync(running.Definition.AuctionId, ct);
        if (endOffset == 0) return true;

        var target = endOffset - 1;
        var deadline = DateTimeOffset.UtcNow + options.ReplayTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (running.LastProcessedOffset >= target) return true;
            await Task.Delay(20, ct);
        }

        return running.LastProcessedOffset >= target;
    }

    private async ValueTask OnVerdictAsync(
        RunningAuction running, BidVerdict verdict, CancellationToken ct)
    {
        running.RecordProcessed(verdict.Offset);

        // Replay over ground already covered: rebuild the engine, publish
        // nothing. Without this a restart re-sends an outbid notification for
        // every bid the auction ever had.
        if (verdict.Offset <= running.PublishedThrough) return;

        if (verdict.Accepted)
        {
            await events.PublishAsync(Topics.CurrentWinner, verdict.AuctionId.ToString(),
                JsonSerializer.Serialize(new CurrentWinner
                {
                    AuctionId = verdict.AuctionId,
                    PriceMinorUnits = verdict.PriceAfterMinorUnits,
                    LeaderBidderId = running.Pump!.Engine.ProvisionalLeader,
                    LeaderClientBidId = verdict.ClientBidId,
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

        running.MarkPublished(verdict.Offset);
        await MaybeCheckpointAsync(running, ct);
    }

    private async Task MaybeCheckpointAsync(RunningAuction running, CancellationToken ct)
    {
        if (!running.CheckpointDue(options.Checkpoint, DateTimeOffset.UtcNow)) return;

        await checkpoints.CommitAsync(
            running.Definition.AuctionId, running.PublishedThrough, ct);
        running.CheckpointWritten(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Advances the clock. Starts auctions that have reached their start time
    /// and closes those whose effective end plus grace has passed.
    ///
    /// Split out from a timer so tests drive it directly rather than sleeping.
    /// </summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!_resumed) return;

        foreach (var running in _running.Values)
        {
            if (running.Pump is null) continue;

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

            // A hall auction is closed by the person running it and by nothing else
            // (§29). The auctioneer brings the hammer down, so a clock here would
            // close an auction that is still taking bids in the room — and "we
            // already know who wins" is only true because a human said so.
            if (running.Definition.Channel == BidChannel.Onsite) continue;

            // The engine moves EffectiveEndsAt as quiet-period extensions land,
            // so this is re-read every tick rather than scheduled once.
            if (now < running.Pump.Engine.EffectiveEndsAt + options.CloseGrace) continue;

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

        // Everything published for this auction is durable before the close is
        // announced, so a crash here cannot lose a verdict the close implies.
        await checkpoints.CommitAsync(
            running.Definition.AuctionId, running.PublishedThrough, ct);
        running.CheckpointWritten(now);

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
        }
        else
        {
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

        running.OffersPublished++;
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

    /// <summary>
    /// Handles the lifecycle topic. During recovery this only records state —
    /// the processor's own past events are how it learns where it left off.
    /// After <see cref="ResumeAsync"/> it acts.
    /// </summary>
    public Task ApplyLifecycleAsync(StreamEvent record, CancellationToken ct)
    {
        if (!Guid.TryParse(record.Key, out var auctionId)) return Task.CompletedTask;

        if (!_resumed)
        {
            RecordDuringRecovery(auctionId, record);
            return Task.CompletedTask;
        }

        switch (record.EventType)
        {
            case InboundEvents.WinnerDisqualified:
            {
                var payload = JsonSerializer.Deserialize<WinnerDisqualifiedPayload>(
                    record.Payload, Json);
                return payload is null
                    ? Task.CompletedTask
                    : OnWinnerDisqualifiedAsync(payload, ct);
            }

            case InboundEvents.AuctionExtendedByClerk:
            {
                var payload = JsonSerializer.Deserialize<ClerkCommandPayload>(record.Payload, Json);
                if (payload is not null) OnClerkExtended(payload);
                return Task.CompletedTask;
            }

            case InboundEvents.AuctionClosedByClerk:
            {
                var payload = JsonSerializer.Deserialize<ClerkCommandPayload>(record.Payload, Json);
                return payload is null ? Task.CompletedTask : OnClerkClosedAsync(payload, ct);
            }

            case InboundEvents.AuctionClosedByAdmin:
            {
                // The same close as the hall's hammer: the engine stops where it is
                // and the highest valid bid is offered to the committee.
                var payload = JsonSerializer.Deserialize<ClerkCommandPayload>(record.Payload, Json);
                return payload is null ? Task.CompletedTask : OnClerkClosedAsync(payload, ct);
            }

            case InboundEvents.AuctionCancelled:
                _cancelled[auctionId] = 0;
                Withdraw(auctionId);
                return Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Moves a hall auction's end time at the clerk's instruction.
    ///
    /// The engine refuses once the published <c>MaxExtensions</c> is spent, and that
    /// refusal is recorded rather than swallowed: a clerk who thinks they extended
    /// and did not would keep taking bids the engine is about to reject.
    /// </summary>
    private void OnClerkExtended(ClerkCommandPayload payload)
    {
        if (!_running.TryGetValue(payload.AuctionId, out var running) || running.Pump is null)
            return;

        if (running.Closed) return;

        var by = TimeSpan.FromSeconds(payload.ExtendBySeconds);
        if (running.Pump.Engine.ExtendByClerk(by))
        {
            logger.LogInformation(
                "Auction {AuctionId}: clerk {Clerk} extended to {EndsAt:o} ({Used} of {Max}).",
                payload.AuctionId, payload.ClerkUserId,
                running.Pump.Engine.EffectiveEndsAt, running.Pump.Engine.ExtensionsUsed,
                running.Definition.MaxExtensions);
        }
        else
        {
            logger.LogWarning(
                "Auction {AuctionId}: clerk {Clerk} could not extend — {Used} of {Max} used.",
                payload.AuctionId, payload.ClerkUserId,
                running.Pump.Engine.ExtensionsUsed, running.Definition.MaxExtensions);
        }
    }

    /// <summary>
    /// The hammer. Closes the auction and offers the candidate exactly as a clock
    /// close would, so everything downstream — the committee's candidate, the
    /// cascade, the deposits — is the same workflow whichever channel sold the land.
    /// </summary>
    private async Task OnClerkClosedAsync(ClerkCommandPayload payload, CancellationToken ct)
    {
        if (!_running.TryGetValue(payload.AuctionId, out var running) || running.Pump is null)
            return;

        // At-least-once delivery, and a clerk may press the button twice.
        if (running.Closed) return;

        logger.LogInformation(
            "Auction {AuctionId}: closed by clerk {Clerk}.", payload.AuctionId, payload.ClerkUserId);

        await CloseAsync(running, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>
    /// Drops a cancelled auction: before it opens, it never starts; once running, it
    /// stops where it is with no close and no candidate — the administrator cancelled
    /// the sale itself, and every deposit is refunded.
    /// </summary>
    private void Withdraw(Guid auctionId)
    {
        if (!_running.TryGetValue(auctionId, out var running)) return;

        if (running.Announced)
        {
            // Cancelled while running, by an administrator's decision: it stops here,
            // and no close or candidate is ever published for it. The catcher refuses
            // its bids from the same event; deposits go back through DepositsReleasable.
            running.Closed = true;
            _running.TryRemove(auctionId, out _);
            logger.LogWarning("Auction {AuctionId} cancelled while running; stopped with no candidate.", auctionId);
            return;
        }

        _running.TryRemove(auctionId, out _);
        logger.LogInformation("Auction {AuctionId} cancelled before it opened.", auctionId);
    }

    private void RecordDuringRecovery(Guid auctionId, StreamEvent record)
    {
        if (record.EventType == InboundEvents.AuctionCancelled)
        {
            _cancelled[auctionId] = 0;
            return;
        }

        var state = _recovered.GetOrAdd(auctionId, _ => new RecoveredState());

        switch (record.EventType)
        {
            case nameof(AuctionStarted):
                state.Announced = true;
                break;

            case nameof(AuctionClosed):
                state.Closed = true;
                break;

            case InboundEvents.AuctionExtendedByClerk:
            {
                // Replayed because it is not a bid: the engine's ladder is rebuilt
                // from the bid log, but a clerk's extension lives only here, and a
                // processor that restarted mid-auction would otherwise come back
                // with the original end time and reject the hall's next bid.
                var payload = JsonSerializer.Deserialize<ClerkCommandPayload>(
                    record.Payload, Json);
                if (payload is not null) state.ClerkExtensions.Add(payload.ExtendBySeconds);
                break;
            }

            case nameof(CandidateOffered):
            case nameof(LadderExhausted):
                state.OffersPublished++;
                break;

            case InboundEvents.WinnerDisqualified:
            {
                var payload = JsonSerializer.Deserialize<WinnerDisqualifiedPayload>(
                    record.Payload, Json);
                if (payload is not null) state.Disqualified.Add(payload.BidderId);
                break;
            }
        }

        // An auction may already be registered when its history replays.
        if (_running.TryGetValue(auctionId, out var running)) state.ApplyTo(running);
    }

    public void ApplyCheckpoint(StreamEvent record) => checkpoints.Apply(record);

    /// <summary>What the processor's own event history says about an auction.</summary>
    private sealed class RecoveredState
    {
        public bool Announced;
        public bool Closed;
        public int OffersPublished;
        public readonly List<int> ClerkExtensions = new();
        public readonly HashSet<Guid> Disqualified = new();

        public void ApplyTo(RunningAuction running)
        {
            running.Announced |= Announced;
            running.Closed |= Closed;
            // Assigned rather than appended: ApplyTo runs again whenever more
            // history replays, and appending would double every extension.
            running.ClerkExtensions.Clear();
            running.ClerkExtensions.AddRange(ClerkExtensions);
            running.OffersPublished = Math.Max(running.OffersPublished, OffersPublished);
            foreach (var bidder in Disqualified) running.Disqualified.Add(bidder);

            // Cascade step is the number of disqualifications, which is exactly
            // how it is incremented in the first place.
            running.CascadeStep = running.Disqualified.Count;
        }
    }
}

/// <summary>One auction being run by the supervisor.</summary>
public sealed class RunningAuction(AuctionDefinition definition)
{
    private int _processed;
    private long _lastProcessedOffset = -1;
    private long _publishedThrough = -1;
    private int _sinceCheckpoint;
    private DateTimeOffset _lastCheckpoint = DateTimeOffset.UtcNow;

    public AuctionDefinition Definition { get; } = definition;
    public AuctionPump? Pump { get; internal set; }
    public Task? Task { get; internal set; }

    private CancellationTokenSource? _lifetime;

    /// <summary>A token that ends with the host or with <see cref="Stop"/>, whichever is first.</summary>
    internal CancellationToken Lifetime(CancellationToken host)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(host);
        return _lifetime.Token;
    }

    /// <summary>Stops the pump, if one was launched. The auction is being replaced, not closed.</summary>
    internal void Stop() => _lifetime?.Cancel();

    public bool Announced { get; internal set; }
    public bool Closed { get; internal set; }
    public int CascadeStep { get; internal set; }

    /// <summary>
    /// Extensions a clerk granted before this process started, in the order granted,
    /// carried from the lifecycle replay and applied to the engine once it exists
    /// (§29). The bid log rebuilds everything else; this is the one piece of engine
    /// state that is not in it.
    ///
    /// A list rather than a total, because each one also consumed an extension from
    /// the auction's published cap.
    /// </summary>
    public List<int> ClerkExtensions { get; } = new();

    /// <summary>Candidate offers and exhaustion notices already published.</summary>
    public int OffersPublished { get; internal set; }

    /// <summary>Bidders auction-admin has disqualified, skipped by the cascade.</summary>
    public HashSet<Guid> Disqualified { get; } = new();

    /// <summary>Highest bid offset whose side effects are published. -1 if none.</summary>
    public long PublishedThrough
    {
        get => Interlocked.Read(ref _publishedThrough);
        internal set => Interlocked.Exchange(ref _publishedThrough, value);
    }

    public int ProcessedBidCount => Volatile.Read(ref _processed);

    /// <summary>Highest bid offset the engine has consumed. -1 before the first.</summary>
    public long LastProcessedOffset => Interlocked.Read(ref _lastProcessedOffset);

    internal void RecordProcessed(long offset)
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Exchange(ref _lastProcessedOffset, offset);
    }

    internal void MarkPublished(long offset)
    {
        PublishedThrough = offset;
        Interlocked.Increment(ref _sinceCheckpoint);
    }

    internal bool CheckpointDue(CheckpointPolicy policy, DateTimeOffset now) =>
        Volatile.Read(ref _sinceCheckpoint) >= policy.EveryRecords
        || now - _lastCheckpoint >= policy.EveryInterval;

    internal void CheckpointWritten(DateTimeOffset at)
    {
        Interlocked.Exchange(ref _sinceCheckpoint, 0);
        _lastCheckpoint = at;
    }
}
