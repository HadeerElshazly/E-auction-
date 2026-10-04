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
    private async Task MarkWarmAsync(CancellationToken ct)
    {
        var lastSeen = -1;
        var stableFor = 0;

        while (!ct.IsCancellationRequested && !Warm)
        {
            var seen = state.AuctionCount + state.EligibilityCount;
            stableFor = seen == lastSeen ? stableFor + 1 : 0;
            lastSeen = seen;

            if (stableFor >= 4)
            {
                Warm = true;
                logger.LogInformation(
                    "Control plane warm: {Auctions} auction(s), {Eligibility} eligibility row(s).",
                    state.AuctionCount, state.EligibilityCount);
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
            MaxExtensions = payload.MaxExtensions
        });
    }

    private void ApplyParticipant(StreamEvent record)
    {
        if (record.EventType != "ParticipantEligibilityChanged") return;

        var payload = JsonSerializer.Deserialize<ParticipantEligibilityPayload>(record.Payload, Json);
        if (payload is null) return;

        if (payload.Eligible)
            state.GrantEligibility(payload.AuctionId, payload.BidderId, payload.KeyEpoch);
        else
            state.RevokeEligibility(payload.AuctionId, payload.BidderId);
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
}

internal sealed record ParticipantEligibilityPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public bool Eligible { get; init; }
    public int KeyEpoch { get; init; }
}

internal sealed record CurrentWinnerPayload
{
    public Guid AuctionId { get; init; }
    public long PriceMinorUnits { get; init; }
}
