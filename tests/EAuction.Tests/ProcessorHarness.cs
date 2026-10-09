using System.Text.Json;
using EAuction.BidProcessor;
using EAuction.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EAuction.Tests;

/// <summary>
/// Wires a registry and supervisor over in-memory transports and drives the
/// clock by hand, so close timing is tested without sleeping through it.
///
/// The transports can be handed to a second harness to simulate a restart:
/// the Kafka topics survive a process dying, so the test keeps them and throws
/// away everything else.
/// </summary>
internal sealed class ProcessorHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();

    public InMemoryBidLog BidLog { get; }
    public InMemoryEventStream Events { get; }
    public AuctionRegistry Registry { get; }
    public AuctionSupervisor Supervisor { get; }
    public CheckpointStore Checkpoints { get; }

    public ProcessorHarness(
        TimeSpan? closeGrace = null,
        CheckpointPolicy? checkpointPolicy = null,
        InMemoryBidLog? bidLog = null,
        InMemoryEventStream? events = null)
    {
        BidLog = bidLog ?? new InMemoryBidLog();
        Events = events ?? new InMemoryEventStream();
        Checkpoints = new CheckpointStore(Events);

        Registry = new AuctionRegistry(NullLogger<AuctionRegistry>.Instance);
        Supervisor = new AuctionSupervisor(
            BidLog, Events, Checkpoints,
            new SupervisorOptions
            {
                CloseGrace = closeGrace ?? TimeSpan.FromSeconds(5),
                // Commit on every published record unless a test says otherwise,
                // so restart behaviour is exercised deterministically.
                Checkpoint = checkpointPolicy
                    ?? new CheckpointPolicy { EveryRecords = 1, EveryInterval = TimeSpan.Zero }
            },
            NullLogger<AuctionSupervisor>.Instance);

        Registry.AuctionReady += definition => Supervisor.Start(definition, _cts.Token);
        Registry.AuctionRedefined += definition => Supervisor.Redefine(definition, _cts.Token);
    }

    /// <summary>A fresh processor over the same topics — i.e. a restart.</summary>
    public ProcessorHarness Restart(TimeSpan? closeGrace = null) =>
        new(closeGrace, bidLog: BidLog, events: Events);

    public CancellationToken Token => _cts.Token;

    public async Task PublishApprovalAsync(AuctionDefinition d) =>
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
            InboundEvents.AuctionApproved, Token);

    public Task PublishReserveAsync(AuctionDefinition d) =>
        Events.PublishAsync(Topics.Sealed, d.AuctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = d.AuctionId,
                reservePriceMinorUnits = d.ReservePriceMinorUnits
            }, Json),
            InboundEvents.AuctionReserveSet, Token);

    public Task PublishDisqualificationAsync(Guid auctionId, Guid bidderId) =>
        Events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new { auctionId, bidderId, reason = "لم يسدد" }, Json),
            InboundEvents.WinnerDisqualified, Token);

    /// <summary>A clerk moving their hall auction's end time (§29).</summary>
    public Task PublishClerkExtensionAsync(Guid auctionId, Guid clerkUserId, int seconds) =>
        Events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new { auctionId, clerkUserId, extendBySeconds = seconds }, Json),
            InboundEvents.AuctionExtendedByClerk, Token);

    /// <summary>The hammer.</summary>
    public Task PublishClerkCloseAsync(Guid auctionId, Guid clerkUserId) =>
        Events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new { auctionId, clerkUserId }, Json),
            InboundEvents.AuctionClosedByClerk, Token);

    /// <summary>An administrator ending a running auction early, keeping its result.</summary>
    public Task PublishAdminCloseAsync(Guid auctionId) =>
        Events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new { auctionId, closedByUserId = Guid.NewGuid(), reason = "سبب", at = DateTimeOffset.UtcNow }, Json),
            InboundEvents.AuctionClosedByAdmin, Token);

    /// <summary>An administrator withdrawing the auction — the sale itself.</summary>
    public Task PublishCancelAsync(Guid auctionId) =>
        Events.PublishAsync(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(new { auctionId, reason = "سبب", cancelledByUserId = Guid.NewGuid() }, Json),
            InboundEvents.AuctionCancelled, Token);

    /// <summary>Appends a bid exactly as the catcher would, then waits for the pump.</summary>
    public async Task BidAsync(AuctionDefinition d, Guid bidder, long amount, DateTimeOffset at)
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

    /// <summary>
    /// The startup sequence: replay the control topics in the order
    /// ProcessorService uses, then resume. Recovering before acting is what
    /// keeps a restart from re-announcing and re-offering.
    /// </summary>
    public async Task RecoverAsync()
    {
        await DrainAsync(Topics.Checkpoints, r => { Supervisor.ApplyCheckpoint(r); return Task.CompletedTask; });
        await DrainAsync(Topics.Upcoming, r => { Registry.Apply(r); return Task.CompletedTask; });
        await DrainAsync(Topics.Sealed, r => { Registry.Apply(r); return Task.CompletedTask; });
        await DrainAsync(Topics.Lifecycle, r => Supervisor.ApplyLifecycleAsync(r, Token));
        await Supervisor.ResumeAsync(Token);
    }

    /// <summary>Applies new lifecycle records after recovery, so they are acted on.</summary>
    public Task DrainLifecycleAsync() =>
        DrainAsync(Topics.Lifecycle, r => Supervisor.ApplyLifecycleAsync(r, Token));

    private async Task DrainAsync(string topic, Func<StreamEvent, Task> handle)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            await foreach (var record in Events.ReadAsync(topic, cts.Token))
                await handle(record);
        }
        catch (OperationCanceledException) { }
    }

    public async Task<List<(string EventType, string Payload)>> LifecycleAsync()
    {
        var found = new List<(string, string)>();
        await DrainAsync(Topics.Lifecycle, r => { found.Add((r.EventType, r.Payload)); return Task.CompletedTask; });
        return found;
    }

    public async Task<List<T>> LifecycleOfAsync<T>(string eventType) =>
        (await LifecycleAsync())
            .Where(e => e.EventType == eventType)
            .Select(e => JsonSerializer.Deserialize<T>(e.Payload, Json)!)
            .ToList();

    public async Task<List<T>> OnTopicAsync<T>(string topic)
    {
        var found = new List<T>();
        await DrainAsync(topic, r =>
        {
            found.Add(JsonSerializer.Deserialize<T>(r.Payload, Json)!);
            return Task.CompletedTask;
        });
        return found;
    }

    public Task<int> CountOnAsync(string topic) => CountAsync(topic);

    private async Task<int> CountAsync(string topic)
    {
        var count = 0;
        await DrainAsync(topic, _ => { count++; return Task.CompletedTask; });
        return count;
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        // Generous on purpose: the wait returns the moment its condition
        // holds, so a long deadline costs nothing when the machine is idle
        // and stops the suite flaking when several assemblies share it.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(message);
    }

    /// <summary>
    /// Stops the pumps and waits for them, so a restart test does not end up
    /// with two processors consuming the same bid log.
    /// </summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync();

        var pumps = Supervisor.Running
            .Select(r => r.Task)
            .Where(t => t is not null)
            .Cast<Task>()
            .ToArray();

        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) await _cts.CancelAsync();
        _cts.Dispose();
    }
}
