using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAuction.Outbox;

/// <summary>
/// Service-specific decisions the relay needs: where a row goes, and anything
/// that must happen around publishing it.
/// </summary>
public interface IOutboxRouter
{
    /// <summary>The destination topic, or null if the row cannot be routed.</summary>
    string? Resolve(string aggregateType);

    /// <summary>
    /// Runs before the row is published. Used where ordering against something
    /// outside the outbox matters — creating a topic before announcing the
    /// auction that will use it, for instance.
    /// </summary>
    Task BeforePublishAsync(OutboxMessage message, ITopicPublisher publisher, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Runs after a successful publish, in the same transaction that marks the
    /// row relayed. For state that only becomes true once the event is out.
    /// </summary>
    Task AfterPublishAsync(DbContext db, OutboxMessage message, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Publishes outbox rows to Kafka, oldest first.
///
/// In deployment Debezium does this by tailing the WAL, which is why the
/// outbox table uses the EventRouter column names. This polling relay exists
/// for local development and as a fallback where Debezium is not yet
/// deployed — the two are interchangeable because the contract is the table,
/// not the publisher. Run one or the other, never both.
///
/// Delivery is at-least-once: a crash between publishing and marking the row
/// relayed republishes it. Consumers are keyed and idempotent, so a duplicate
/// is harmless, whereas a lost event is not.
/// </summary>
public sealed class OutboxRelay<TContext>(
    IDbContextFactory<TContext> dbFactory,
    ITopicPublisher publisher,
    IOutboxRouter router,
    ILogger<OutboxRelay<TContext>> logger)
    where TContext : DbContext
{
    public async Task<int> DrainOnceAsync(int batchSize, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var batch = await db.Set<OutboxMessage>()
            .Where(m => m.RelayedAt == null)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        var published = 0;
        foreach (var message in batch)
        {
            var topic = router.Resolve(message.AggregateType);
            if (topic is null)
            {
                logger.LogError(
                    "Outbox row {Id} has unroutable aggregate type {AggregateType}; leaving it queued.",
                    message.Id, message.AggregateType);
                // Stop rather than skip: skipping would publish later events
                // for this aggregate ahead of this one.
                break;
            }

            await router.BeforePublishAsync(message, publisher, ct);
            await publisher.PublishAsync(topic, message.AggregateId, message.Payload, message.Type, ct);

            message.RelayedAt = DateTimeOffset.UtcNow;
            await router.AfterPublishAsync(db, message, ct);
            published++;
        }

        if (published > 0) await db.SaveChangesAsync(ct);
        return published;
    }
}

/// <summary>Runs the relay on a timer.</summary>
public sealed class OutboxRelayService<TContext>(
    OutboxRelay<TContext> relay,
    ILogger<OutboxRelayService<TContext>> logger) : BackgroundService
    where TContext : DbContext
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
