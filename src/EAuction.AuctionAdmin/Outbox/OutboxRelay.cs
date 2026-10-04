using EAuction.AuctionAdmin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EAuction.AuctionAdmin.Outbox;

/// <summary>
/// Publishes outbox rows to Kafka, oldest first.
///
/// In deployment Debezium does this by tailing the WAL, which is why the
/// outbox table uses the EventRouter column names. This polling relay exists
/// for local development and as a fallback where Debezium is not yet
/// deployed — the two are interchangeable because the contract is the table,
/// not the publisher.
///
/// Delivery is at-least-once: a crash between publishing and marking the row
/// relayed republishes it. Consumers are keyed by auctionId and idempotent,
/// so a duplicate is harmless, whereas a lost approval is not.
/// </summary>
public sealed class OutboxRelay(
    IDbContextFactory<AdminDbContext> dbFactory,
    ITopicPublisher publisher,
    ILogger<OutboxRelay> logger)
{
    public async Task<int> DrainOnceAsync(int batchSize, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var batch = await db.Outbox
            .Where(m => m.RelayedAt == null)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        var published = 0;
        foreach (var message in batch)
        {
            var topic = TopicMap.Resolve(message.AggregateType);
            if (topic is null)
            {
                logger.LogError(
                    "Outbox row {Id} has unroutable aggregate type {AggregateType}; leaving it queued.",
                    message.Id, message.AggregateType);
                // Stop rather than skip: skipping would publish later events
                // for this auction ahead of this one.
                break;
            }

            // The ordering that makes the catcher safe (§5): an auction must
            // not reach auctions.upcoming before its bid topic exists, or a
            // bid could arrive for a topic that is not there. Auto-create is
            // disabled on the broker precisely so this cannot be papered over.
            if (message.Type == nameof(Domain.AuctionApproved)
                && Guid.TryParse(message.AggregateId, out var auctionId))
            {
                await publisher.EnsureTopicAsync(TopicMap.BidTopicFor(auctionId), ct);
            }

            await publisher.PublishAsync(topic, message.AggregateId, message.Payload, message.Type, ct);

            message.RelayedAt = DateTimeOffset.UtcNow;
            published++;

            // Publication is what makes an auction visible to bidders, so the
            // relay is the only thing that knows when Approved becomes
            // Scheduled. Saved in the same transaction as the relayed marker.
            if (message.Type == nameof(Domain.AuctionApproved)
                && Guid.TryParse(message.AggregateId, out var publishedAuctionId))
            {
                var auction = await db.Auctions
                    .FirstOrDefaultAsync(a => a.Id == publishedAuctionId, ct);

                if (auction?.Status == Domain.AuctionStatus.Approved)
                    auction.MarkScheduled();
            }
        }

        if (published > 0) await db.SaveChangesAsync(ct);
        return published;
    }
}

/// <summary>Runs the relay on a timer.</summary>
public sealed class OutboxRelayService(
    OutboxRelay relay, ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await relay.DrainOnceAsync(100, stoppingToken);
                if (published > 0)
                    logger.LogInformation("Relayed {Count} outbox messages.", published);

                // Back off only when there was nothing to do; a full batch
                // probably means more is waiting.
                if (published < 100)
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay pass failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
