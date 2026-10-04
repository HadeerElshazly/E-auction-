using EAuction.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAuction.BidProcessor;

/// <summary>
/// Wires the processor together: consume the control topics, run every
/// auction, and advance the clock.
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

        var consumers = new[]
        {
            ConsumeAsync(Topics.Upcoming, record => { registry.Apply(record); return Task.CompletedTask; }, stoppingToken),
            ConsumeAsync(Topics.Sealed, record => { registry.Apply(record); return Task.CompletedTask; }, stoppingToken),
            ConsumeAsync(Topics.Lifecycle, record => supervisor.ApplyLifecycleAsync(record, stoppingToken), stoppingToken),
            TickLoopAsync(stoppingToken)
        };

        await Task.WhenAll(consumers);
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

public sealed record ProcessorServiceOptions
{
    /// <summary>
    /// Close detection granularity. An auction closes within one tick of its
    /// effective end plus the close grace, so this bounds how late a close can
    /// be — not how accurate the result is, which the log decides.
    /// </summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
