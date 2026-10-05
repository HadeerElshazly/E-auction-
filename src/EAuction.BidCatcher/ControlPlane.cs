using System.Text.Json;
using EAuction.Core;

namespace EAuction.BidCatcher;

/// <summary>
/// Fills <see cref="CatcherState"/> from the compacted control topics.
///
/// Without this the catcher starts empty and rejects every bid as an unknown
/// auction. Replaying the three topics from the beginning is what makes "even
/// if the service is down it can read it later" true (D-12): an auction
/// approved, or a deposit paid, while the catcher was offline is still in the
/// log when it comes back.
///
/// Every pod runs its own consumer and holds a full copy. At this size —
/// hundreds of auctions, thousands of eligibility rows, one long per price —
/// that is cheaper than coordinating.
/// </summary>
public sealed class ControlPlane(
    CatcherState state,
    IEventStream events,
    ILogger<ControlPlane> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Set once each topic has been replayed to a quiet point.</summary>
    public bool Warm { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            ConsumeAsync(Topics.Upcoming, ApplyAuction, stoppingToken),
            ConsumeAsync(Topics.Participants, ApplyParticipant, stoppingToken),
            ConsumeAsync(Topics.CurrentWinner, ApplyPrice, stoppingToken),
            MarkWarmAsync(stoppingToken));
    }

    /// <summary>
    /// Readiness. A catcher that reports ready before its state is loaded would
    /// be sent traffic it can only reject, so the probe waits for a first pass.
    /// </summary>
    /// <summary>
    /// How long to wait before a stable state is allowed to count as a replayed one.
    ///
    /// Without a floor, "the count stopped changing" is indistinguishable from "the
    /// count has not started changing yet": a Kafka consumer takes seconds to join
    /// its group and begin delivering, so a fresh pod declared itself warm with an
    /// empty state and then rejected every bid as UnknownAuction while the probe
    /// said it was healthy. Found when a portal asked the query BFF for the
    /// catalogue a moment after it came up and was told there were no auctions.
    /// </summary>
    public TimeSpan MinimumWarmUp { get; init; } = TimeSpan.FromSeconds(8);

    private async Task MarkWarmAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lastSeen = -1;
        var stableFor = 0;

        while (!ct.IsCancellationRequested && !Warm)
        {
            var seen = state.AuctionCount + state.EligibilityCount + state.ClerkCount;
            stableFor = seen == lastSeen ? stableFor + 1 : 0;
            lastSeen = seen;

            if (stableFor >= 4 && DateTimeOffset.UtcNow - startedAt >= MinimumWarmUp)
            {
                Warm = true;
                logger.LogInformation(
                    "Control plane warm: {Auctions} auction(s), {Eligibility} eligibility row(s), "
                    + "{Clerks} clerk assignment(s).",
                    state.AuctionCount, state.EligibilityCount, state.ClerkCount);
                return;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ApplyAuction(StreamEvent record)
    {
        if (record.EventType != "AuctionApproved") return;

        var payload = JsonSerializer.Deserialize<AuctionApprovedPayload>(record.Payload, Json);
        if (payload is null) return;

        state.UpsertAuction(new AuctionDefinition
        {
            AuctionId = payload.AuctionId,
            StartsAt = payload.StartsAt,
            EndsAt = payload.EndsAt,
            OpeningPriceMinorUnits = payload.OpeningPriceMinorUnits,
            // The catcher never sees the reserve and does not need it: it gates
            // on the window and eligibility, and screens price only advisorily.
            ReservePriceMinorUnits = 0,
            Increment = new IncrementPolicy.Fixed(payload.MinIncrementMinorUnits),
            QuietPeriod = payload.QuietPeriodSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : null,
            MaxExtensions = payload.MaxExtensions,

            // Which channel this auction runs on decides who may post a frame for
            // it at all (§29). Anything unrecognised is Online, which is the
            // channel with the stricter caller rule — a hall auction misread as
            // online refuses the clerk rather than letting a bidder in.
            Channel = Enum.TryParse<BidChannel>(payload.Channel, ignoreCase: true, out var channel)
                ? channel
                : BidChannel.Online
        });
    }

    private void ApplyParticipant(StreamEvent record)
    {
        switch (record.EventType)
        {
            case "ParticipantEligibilityChanged":
            {
                var payload = JsonSerializer.Deserialize<ParticipantEligibilityPayload>(
                    record.Payload, Json);
                if (payload is null) return;

                if (payload.Eligible)
                    state.GrantEligibility(payload.AuctionId, payload.BidderId, payload.KeyEpoch);
                else
                    state.RevokeEligibility(payload.AuctionId, payload.BidderId);
                return;
            }

            // Who may enter bids from the hall (§29). It rides this topic rather
            // than a new one because it answers the same question — whose key
            // signs a frame for this auction — and the catcher rebuilds both from
            // the same replay.
            case "AuctionClerkAssigned":
            {
                var payload = JsonSerializer.Deserialize<ClerkAssignmentPayload>(
                    record.Payload, Json);
                if (payload is null) return;

                if (payload.Assigned)
                    state.AssignClerk(payload.AuctionId, payload.ClerkUserId, payload.KeyEpoch);
                else
                    state.UnassignClerk(payload.AuctionId, payload.ClerkUserId);
                return;
            }
        }
    }

    private void ApplyPrice(StreamEvent record)
    {
        if (record.EventType != "CurrentWinner") return;

        var payload = JsonSerializer.Deserialize<CurrentWinnerPayload>(record.Payload, Json);
        if (payload is not null)
            state.UpdateCurrentPrice(payload.AuctionId, payload.PriceMinorUnits);
    }

    private async Task ConsumeAsync(
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
                catch (Exception ex)
                {
                    // One unreadable record must not stall the topic behind it.
                    logger.LogError(ex,
                        "Failed to apply {EventType} at offset {Offset} on {Topic}.",
                        record.EventType, record.Offset, topic);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

// The catcher's own view of the contracts it consumes, redeclared rather than
// shared so the dependency points the right way.

internal sealed record AuctionApprovedPayload
{
    public Guid AuctionId { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public long OpeningPriceMinorUnits { get; init; }
    public long MinIncrementMinorUnits { get; init; }
    public int? QuietPeriodSeconds { get; init; }
    public int MaxExtensions { get; init; }
    public string? Channel { get; init; }
}

internal sealed record ParticipantEligibilityPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public bool Eligible { get; init; }
    public int KeyEpoch { get; init; }
}

internal sealed record ClerkAssignmentPayload
{
    public Guid AuctionId { get; init; }
    public Guid ClerkUserId { get; init; }
    public bool Assigned { get; init; }
    public int KeyEpoch { get; init; }
}

internal sealed record CurrentWinnerPayload
{
    public Guid AuctionId { get; init; }
    public long PriceMinorUnits { get; init; }
}
