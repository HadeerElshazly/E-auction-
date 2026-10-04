using System.Text.Json;
using EAuction.BidProcessor;
using EAuction.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EAuction.Tests;

/// <summary>
/// Wires a registry and supervisor over in-memory transports and drives the
/// clock by hand, so close timing is tested without sleeping through it.
/// </summary>
internal sealed class ProcessorHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();

    public InMemoryBidLog BidLog { get; } = new();
    public InMemoryEventStream Events { get; } = new();
    public AuctionRegistry Registry { get; }
    public AuctionSupervisor Supervisor { get; }

    public ProcessorHarness(TimeSpan? closeGrace = null)
    {
        Registry = new AuctionRegistry(NullLogger<AuctionRegistry>.Instance);
        Supervisor = new AuctionSupervisor(
            BidLog, Events,
            new SupervisorOptions { CloseGrace = closeGrace ?? TimeSpan.FromSeconds(5) },
            NullLogger<AuctionSupervisor>.Instance);

        Registry.AuctionReady += definition => Supervisor.Start(definition, _cts.Token);
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>Publishes what auction-admin's outbox relay would publish.</summary>
    public async Task PublishApprovalAsync(AuctionDefinition d)
    {
        await Events.PublishAsync(Topics.Upcoming, d.AuctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = d.AuctionId,
                nameAr = "مزاد", nameEn = "Auction",
                startsAt = d.StartsAt, endsAt = d.EndsAt,
                openingPriceMinorUnits = d.OpeningPriceMinorUnits,
                minIncrementMinorUnits = d.Increment.MinimumRaise(d.OpeningPriceMinorUnits),
                depositMinorUnits = 100_000_00,
                quietPeriodSeconds = (int?)d.QuietPeriod?.TotalSeconds,
                maxExtensions = d.MaxExtensions,
                channel = d.Channel.ToString()
            }, Json),
            "AuctionApproved", Token);
    }

    public Task PublishReserveAsync(AuctionDefinition d) =>
        Events.PublishAsync(Topics.Sealed, d.AuctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = d.AuctionId,
                reservePriceMinorUnits = d.ReservePriceMinorUnits
            }, Json),
            "AuctionReserveSet", Token);

    public Task PublishDisqualificationAsync(Guid auctionId, Guid bidderId) =>
        Events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId, bidderId, reason = "لم يسدد"
            }, Json),
            "WinnerDisqualified", Token);

    /// <summary>Appends a bid exactly as the catcher would, then waits for the pump.</summary>
    public async Task BidAsync(
        AuctionDefinition d, Guid bidder, long amount, DateTimeOffset at)
    {
        var running = Running(d.AuctionId);
        var before = running.ProcessedBidCount;

        var client = TestAuction.Frame(d.AuctionId, bidder, amount, at);
        await BidLog.AppendAsync(d.AuctionId, TestAuction.AsServerFrame(client, at), Token);

        await WaitFor(() => running.ProcessedBidCount > before,
            $"bid from {bidder} was never processed");
    }

    public RunningAuction Running(Guid auctionId)
    {
        Supervisor.TryGet(auctionId, out var running);
        return running!;
    }

    /// <summary>Feeds the registry by draining the control topics once.</summary>
    public async Task DrainControlAsync()
    {
        foreach (var topic in new[] { Topics.Upcoming, Topics.Sealed })
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            try
            {
                await foreach (var record in Events.ReadAsync(topic, cts.Token))
                    Registry.Apply(record);
            }
            catch (OperationCanceledException) { }
        }
    }

    public async Task DrainLifecycleAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var record in Events.ReadAsync(Topics.Lifecycle, cts.Token))
                await Supervisor.ApplyLifecycleAsync(record, Token);
        }
        catch (OperationCanceledException) { }
    }

    public async Task<List<(string EventType, string Payload)>> LifecycleAsync()
    {
        var found = new List<(string, string)>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var record in Events.ReadAsync(Topics.Lifecycle, cts.Token))
                found.Add((record.EventType, record.Payload));
        }
        catch (OperationCanceledException) { }
        return found;
    }

    public async Task<List<T>> LifecycleOfAsync<T>(string eventType)
    {
        var all = await LifecycleAsync();
        return all.Where(e => e.EventType == eventType)
                  .Select(e => JsonSerializer.Deserialize<T>(e.Payload, Json)!)
                  .ToList();
    }

    public async Task<List<T>> OnTopicAsync<T>(string topic)
    {
        var found = new List<T>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var record in Events.ReadAsync(topic, cts.Token))
                found.Add(JsonSerializer.Deserialize<T>(record.Payload, Json)!);
        }
        catch (OperationCanceledException) { }
        return found;
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
