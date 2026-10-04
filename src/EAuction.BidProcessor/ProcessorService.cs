using EAuction.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAuction.BidProcessor;

public sealed record ProcessorServiceOptions
{
    /// <summary>
    /// Close detection granularity. An auction closes within one tick of its
    /// effective end plus the close grace, so this bounds how late a close can
    /// be — not how accurate the result is, which the log decides.
    /// </summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A control topic counts as caught up once it has been silent this long.
    /// </summary>
    public TimeSpan RecoveryQuietPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound on recovery, so a chatty topic cannot stall startup forever.</summary>
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Wires the processor together.
///
/// Startup recovers before it acts: the control topics and the processor's own
/// published history are replayed to a quiet point, and only then do the bid
/// pumps start. Acting first would re-announce auctions, re-close them, and
/// re-offer candidates the committee has already seen.
/// </summary>
public sealed class ProcessorService(
    AuctionRegistry registry,
    AuctionSupervisor supervisor,
    IEventStream events,
    ProcessorServiceOptions options,
    ILogger<ProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.AuctionReady += definition => supervisor.Start(definition, stoppingToken);

        await RecoverAsync(stoppingToken);
        await supervisor.ResumeAsync(stoppingToken);

        logger.LogInformation(
            "Recovery complete: {Count} auction(s) resumed.", supervisor.Running.Count);

        // The long-running consumers re-read each topic from the beginning.
        // Everything they apply is idempotent — the registry overwrites, the
        // checkpoint store only moves forward, and a replayed disqualification
        // is dropped by the disqualified set.
        await Task.WhenAll(
            ConsumeAsync(Topics.Checkpoints, r => { supervisor.ApplyCheckpoint(r); return Task.CompletedTask; }, stoppingToken),
            ConsumeAsync(Topics.Upcoming, r => { registry.Apply(r); return Task.CompletedTask; }, stoppingToken),
            ConsumeAsync(Topics.Sealed, r => { registry.Apply(r); return Task.CompletedTask; }, stoppingToken),
            ConsumeAsync(Topics.Lifecycle, r => supervisor.ApplyLifecycleAsync(r, stoppingToken), stoppingToken),
            TickLoopAsync(stoppingToken));
    }

    /// <summary>
    /// Replays the control topics to a quiet point. Checkpoints and auction
    /// definitions first, so that by the time the lifecycle history is applied
    /// the auctions it refers to already exist.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.RecoveryTimeout);

        await DrainUntilQuietAsync(Topics.Checkpoints,
            r => { supervisor.ApplyCheckpoint(r); return Task.CompletedTask; }, budget.Token);

        await DrainUntilQuietAsync(Topics.Upcoming,
            r => { registry.Apply(r); return Task.CompletedTask; }, budget.Token);

        await DrainUntilQuietAsync(Topics.Sealed,
            r => { registry.Apply(r); return Task.CompletedTask; }, budget.Token);

        await DrainUntilQuietAsync(Topics.Lifecycle,
            r => supervisor.ApplyLifecycleAsync(r, budget.Token), budget.Token);
    }

    private async Task DrainUntilQuietAsync(
        string topic, Func<StreamEvent, Task> handle, CancellationToken ct)
    {
        var applied = 0;

        try
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(options.RecoveryQuietPeriod);

            await foreach (var record in events.ReadAsync(topic, quiet.Token))
            {
                try
                {
                    await handle(record);
                    applied++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Recovery: failed to apply {EventType} at offset {Offset} on {Topic}.",
                        record.EventType, record.Offset, topic);
                }

                // Each record extends the window; the topic is caught up only
                // once it has gone quiet.
                quiet.CancelAfter(options.RecoveryQuietPeriod);
            }
        }
        catch (OperationCanceledException) { }

        logger.LogInformation("Recovery: applied {Count} record(s) from {Topic}.", applied, topic);
    }

    private async Task ConsumeAsync(
        string topic, Func<StreamEvent, Task> handle, CancellationToken ct)
    {
        try
        {
            await foreach (var record in events.ReadAsync(topic, ct))
            {
                try
                {
                    await handle(record);
                }
                catch (Exception ex)
                {
                    // One malformed record must not stop the stream; it would
                    // stall every auction behind it.
                    logger.LogError(ex,
                        "Failed to handle {EventType} at offset {Offset} on {Topic}.",
                        record.EventType, record.Offset, topic);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await supervisor.TickAsync(DateTimeOffset.UtcNow, ct);
                await Task.Delay(options.TickInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tick failed; continuing.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
