using System.Text.Json;
using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Persistence;
using EAuction.Core;
using Microsoft.EntityFrameworkCore;

namespace EAuction.AuctionAdmin.Outbox;

/// <summary>
/// Admin's half of the loop: applies the bid processor's lifecycle events to
/// the auction aggregate, so the workflow advances on its own instead of
/// waiting for someone to call an endpoint.
///
/// The processor says what happened to the bidding; the committee still
/// decides the award (D-08). Nothing here confirms an award — the most it does
/// is put a candidate in front of the committee.
/// </summary>
public sealed class LifecycleConsumer(
    IDbContextFactory<AdminDbContext> dbFactory,
    ILogger<LifecycleConsumer> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ApplyAsync(StreamEvent record, CancellationToken ct)
    {
        // These are the processor's events. Admin's own events also land on
        // this topic and come straight back; ignoring them is not an oversight.
        Action<Auction>? change = record.EventType switch
        {
            "AuctionStarted" => auction =>
            {
                if (auction.Status == AuctionStatus.Approved) auction.MarkScheduled();
                auction.MarkLive();
            },
            "AuctionClosed" => auction =>
            {
                auction.MarkClosing();
                auction.MarkPendingEligibilityReview();
            },
            "CandidateOffered" => auction =>
            {
                var payload = JsonSerializer.Deserialize<CandidateOfferedPayload>(record.Payload, Json)!;
                auction.OfferCandidate(payload.BidderId, payload.AmountMinorUnits);
            },
            "LadderExhausted" => auction => auction.MarkUnsold(),
            _ => null
        };

        if (change is null) return;

        if (!Guid.TryParse(record.Key, out var auctionId))
        {
            logger.LogError("Lifecycle event {EventType} has an unusable key {Key}.",
                record.EventType, record.Key);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Auctions
            .Include(a => a.Awards)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);

        if (target is null)
        {
            logger.LogWarning("Lifecycle event {EventType} for unknown auction {AuctionId}.",
                record.EventType, auctionId);
            return;
        }

        try
        {
            change(target);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Applied {EventType} to auction {AuctionId}; now {Status}.",
                record.EventType, auctionId, target.Status);
        }
        catch (InvalidAuctionTransitionException ex)
        {
            // Delivery is at-least-once, so a redelivered event will find the
            // auction already past that transition. That is the expected path,
            // not a failure — the transition guard is what makes it safe.
            logger.LogInformation(
                "Ignoring {EventType} for auction {AuctionId}: {Message}",
                record.EventType, auctionId, ex.Message);
        }
    }

    private sealed record CandidateOfferedPayload
    {
        public Guid AuctionId { get; init; }
        public Guid BidderId { get; init; }
        public long AmountMinorUnits { get; init; }
        public int CascadeStep { get; init; }
    }
}

/// <summary>Runs the lifecycle consumer for as long as the service is up.</summary>
public sealed class LifecycleConsumerService(
    LifecycleConsumer consumer,
    IEventStream events,
    ILogger<LifecycleConsumerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var record in events.ReadAsync(Topics.Lifecycle, stoppingToken))
            {
                try
                {
                    await consumer.ApplyAsync(record, stoppingToken);
                }
                catch (Exception ex)
                {
                    // One bad record must not stall every auction behind it.
                    logger.LogError(ex,
                        "Failed to apply {EventType} at offset {Offset}.",
                        record.EventType, record.Offset);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
