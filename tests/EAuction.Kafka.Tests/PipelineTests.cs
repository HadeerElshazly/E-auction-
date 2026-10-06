using EAuction.Outbox;
using System.Text.Json;
using EAuction.BidProcessor;
using EAuction.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Kafka.Tests;

/// <summary>
/// The processor driven end to end over a real broker: control topics in,
/// bids through the log, a close, and a candidate for the committee out.
///
/// Everything here was previously exercised only against in-memory stand-ins.
/// </summary>
[Collection("kafka")]
public class PipelineTests(KafkaFixture kafka) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly CancellationTokenSource _cts = new();
    private KafkaBidLog? _log;
    private KafkaEventStream? _events;

    private async Task<(AuctionDefinition Definition, AuctionSupervisor Supervisor,
                        AuctionRegistry Registry, IEventStream Events)> SetUpAsync(
        TimeSpan? quiet = null, int endsInMinutes = 30)
    {
        var auctionId = Guid.NewGuid();

        // Auto-create is off, as in deployment: the approval workflow makes
        // these before anything is published.
        foreach (var topic in new[]
        {
            Topics.Upcoming, Topics.Sealed, Topics.Lifecycle,
            Topics.CurrentWinner, Topics.BidsRejected, Topics.Checkpoints
        })
        {
            await kafka.CreateTopicAsync(topic, partitions: 1, compacted: false);
        }
        await kafka.CreateTopicAsync(Topics.BidTopicFor(auctionId), partitions: 1);

        _log = new KafkaBidLog(new KafkaBidLogOptions
        {
            BootstrapServers = kafka.Bootstrap!,
            ConsumerGroup = "pipeline-" + Guid.NewGuid().ToString("N")[..8]
        });

        _events = new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = kafka.Bootstrap!,
            ConsumerGroup = "pipeline-" + Guid.NewGuid().ToString("N")[..8]
        });

        var definition = new AuctionDefinition
        {
            AuctionId = auctionId,
            StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndsAt = DateTimeOffset.UtcNow.AddMinutes(endsInMinutes),
            OpeningPriceMinorUnits = 1_000_000_00,
            ReservePriceMinorUnits = 1_500_000_00,
            Increment = new IncrementPolicy.Fixed(50_000_00),
            QuietPeriod = quiet,
            MaxExtensions = 3
        };

        await _events.PublishAsync(Topics.Upcoming, auctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId,
                startsAt = definition.StartsAt,
                endsAt = definition.EndsAt,
                openingPriceMinorUnits = definition.OpeningPriceMinorUnits,
                minIncrementMinorUnits = 50_000_00L,
                depositMinorUnits = 100_000_00L,
                bookletPriceMinorUnits = 1_000_00L,
                quietPeriodSeconds = (int?)quiet?.TotalSeconds,
                maxExtensions = 3,
                channel = "Online"
            }, Json),
            "AuctionApproved", _cts.Token);

        await _events.PublishAsync(Topics.Sealed, auctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId,
                reservePriceMinorUnits = definition.ReservePriceMinorUnits
            }, Json),
            "AuctionReserveSet", _cts.Token);

        var checkpoints = new CheckpointStore(_events);
        var registry = new AuctionRegistry(NullLogger<AuctionRegistry>.Instance);
        var supervisor = new AuctionSupervisor(
            _log, _events, checkpoints,
            new SupervisorOptions
            {
                CloseGrace = TimeSpan.FromSeconds(1),
                Checkpoint = new CheckpointPolicy { EveryRecords = 1, EveryInterval = TimeSpan.Zero }
            },
            NullLogger<AuctionSupervisor>.Instance);

        registry.AuctionReady += d => supervisor.Start(d, _cts.Token);

        // The startup sequence: replay the control topics, then resume.
        foreach (var topic in new[] { Topics.Upcoming, Topics.Sealed })
            await DrainAsync(_events, topic, r => registry.Apply(r), TimeSpan.FromSeconds(15));

        await supervisor.ResumeAsync(_cts.Token);
        return (definition, supervisor, registry, _events);
    }

    /// <summary>
    /// Retries a read until it satisfies the predicate, or gives up and returns the
    /// last value so the assertion that follows reports something useful.
    /// </summary>
    private static async Task<T> EventuallyAsync<T>(
        Func<Task<T>> read, Func<T, bool> satisfied, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var last = await read();

        while (!satisfied(last) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            last = await read();
        }

        return last;
    }

    private static async Task DrainAsync(
        IEventStream events, string topic, Action<StreamEvent> apply, TimeSpan quietFor)
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(quietFor);
        try
        {
            await foreach (var record in events.ReadAsync(topic, cts.Token))
            {
                apply(record);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task BidAsync(
        AuctionDefinition d, AuctionSupervisor supervisor, Guid bidder, long amount, DateTimeOffset at)
    {
        supervisor.TryGet(d.AuctionId, out var running);
        var before = running.ProcessedBidCount;

        var client = BidFrame.BuildClientFrame(
            d.AuctionId, bidder, amount, at.ToUnixTimeMilliseconds(),
            Guid.NewGuid(), Random.Shared.NextInt64(), Secret);

        var server = new byte[BidFrame.ServerLength];
        client.CopyTo(server, 0);
        BidFrame.AppendServerMetadata(
            server, at.ToUnixTimeMilliseconds(), 0, BidChannel.Online, Guid.Empty);

        await _log!.AppendAsync(d.AuctionId, server, _cts.Token);
        await WaitFor(() => running.ProcessedBidCount > before, $"bid of {amount} never processed");
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private async Task<List<T>> LifecycleOfAsync<T>(IEventStream events, string eventType)
    {
        var found = new List<T>();
        await DrainAsync(events, Topics.Lifecycle,
            r => { if (r.EventType == eventType) found.Add(JsonSerializer.Deserialize<T>(r.Payload, Json)!); },
            TimeSpan.FromSeconds(10));
        return found;
    }

    [RequiresKafkaFact]
    public async Task An_auction_runs_from_approval_through_to_a_candidate_for_the_committee()
    {
        var (d, supervisor, _, events) = await SetUpAsync();

        Assert.True(supervisor.TryGet(d.AuctionId, out _),
            "the registry never assembled the auction from its two control topics");

        var khalid = Guid.NewGuid();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        await BidAsync(d, supervisor, khalid, 1_100_000_00, DateTimeOffset.UtcNow);
        await BidAsync(d, supervisor, ahmad, 1_550_000_00, DateTimeOffset.UtcNow);
        await BidAsync(d, supervisor, sara, 1_800_000_00, DateTimeOffset.UtcNow);

        await supervisor.TickAsync(d.EndsAt.AddSeconds(5), _cts.Token);

        var mine = (await LifecycleOfAsync<CandidateOffered>(events, nameof(CandidateOffered)))
            .Where(c => c.AuctionId == d.AuctionId).ToList();

        var offered = Assert.Single(mine);
        Assert.Equal(sara, offered.BidderId);
        Assert.Equal(1_800_000_00, offered.AmountMinorUnits);

        // Khalid never cleared the reserve, so the cascade would skip him.
        Assert.NotEqual(khalid, offered.BidderId);
    }

    [RequiresKafkaFact]
    public async Task Current_winner_updates_reach_the_topic_the_catcher_reads()
    {
        var (d, supervisor, _, events) = await SetUpAsync();

        await BidAsync(d, supervisor, Guid.NewGuid(), 1_200_000_00, DateTimeOffset.UtcNow);
        await BidAsync(d, supervisor, Guid.NewGuid(), 1_400_000_00, DateTimeOffset.UtcNow);

        var winners = new List<CurrentWinner>();
        await DrainAsync(events, Topics.CurrentWinner,
            r =>
            {
                var w = JsonSerializer.Deserialize<CurrentWinner>(r.Payload, Json)!;
                if (w.AuctionId == d.AuctionId) winners.Add(w);
            },
            TimeSpan.FromSeconds(15));

        // One update per bid, counted by the bid that took the lead rather than by
        // record. On a broker shared with a running bid processor (the smoke stack's,
        // left up while this suite runs) that service also picks this auction up from
        // auctions.upcoming and publishes its own copy of each update, so counting
        // records gives four and fails a test whose property holds. Every processor
        // stamps the same client bid id on its update for the same bid, and publishes
        // in bid order, so the first copy of each still arrives in the order the bids
        // were placed.
        var perBid = winners
            .GroupBy(w => w.LeaderClientBidId)
            .Select(g => g.First())
            .ToList();

        Assert.Equal(2, perBid.Count);
        Assert.Equal(1_400_000_00, perBid[^1].PriceMinorUnits);
    }

    [RequiresKafkaFact]
    public async Task A_quiet_period_bid_pushes_the_close_out_on_a_real_broker()
    {
        var (d, supervisor, _, _) = await SetUpAsync(
            quiet: TimeSpan.FromMinutes(2), endsInMinutes: 1);

        await BidAsync(d, supervisor, Guid.NewGuid(), 1_800_000_00, d.EndsAt.AddSeconds(-30));

        supervisor.TryGet(d.AuctionId, out var running);
        Assert.Equal(1, running.Pump!.Engine.ExtensionsUsed);

        await supervisor.TickAsync(d.EndsAt.AddSeconds(5), _cts.Token);
        Assert.False(running.Closed);

        await supervisor.TickAsync(d.EndsAt.AddMinutes(2).AddSeconds(5), _cts.Token);
        Assert.True(running.Closed);
    }

    [RequiresKafkaFact]
    public async Task Checkpoints_survive_on_the_topic_so_a_restart_stays_quiet()
    {
        var (d, supervisor, _, events) = await SetUpAsync();

        for (var i = 0; i < 4; i++)
            await BidAsync(d, supervisor, Guid.NewGuid(),
                1_000_000_00 + (i + 1) * 50_000_00, DateTimeOffset.UtcNow);

        // A fresh checkpoint store, as a restarted process would build.
        //
        // Retried until the checkpoint is there rather than drained once for a fixed
        // window. The property is "the checkpoint reaches the topic", not "within
        // fifteen seconds of one drain" — and on a loaded machine the single-drain
        // version failed intermittently while being perfectly correct, which is the
        // worst kind of test.
        var published = await EventuallyAsync(
            async () =>
            {
                var reloaded = new CheckpointStore(events);
                await DrainAsync(
                    events, Topics.Checkpoints, r => reloaded.Apply(r), TimeSpan.FromSeconds(10));
                return reloaded.PublishedThrough(d.AuctionId);
            },
            through => through == 3,
            TimeSpan.FromSeconds(90));

        Assert.Equal(3, published);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_log is not null) await _log.DisposeAsync();
        if (_events is not null) await _events.DisposeAsync();
        _cts.Dispose();
    }
}

/// <summary>
/// <see cref="IEventStream.LatestOffsetAsync"/> against a real broker.
///
/// It exists for one reason and that reason is only reachable here: a consumer
/// takes a second or two to be assigned a partition, which is indistinguishable
/// from an empty topic by waiting. The in-memory stream answers instantly and so
/// cannot show whether this works, and the notification service sent the platform's
/// entire history to every bidder when it was wrong.
/// </summary>
[Collection("kafka")]
public class EndOffsetTests(KafkaFixture kafka)
{
    /// <summary>A deadline, so a broker that stops answering fails rather than hangs.</summary>
    private static readonly CancellationTokenSource Timeout = new(TimeSpan.FromMinutes(2));

    private static CancellationToken Ct => Timeout.Token;

    private KafkaEventStream Stream() => new(new KafkaEventStreamOptions
    {
        BootstrapServers = kafka.Bootstrap!,
        ConsumerGroup = "endoffset-" + Guid.NewGuid().ToString("N")[..8],
    });

    /// <summary>
    /// Creates the topic before writing to it.
    ///
    /// Not a formality: auto-creation is asynchronous, so the first produce to a
    /// topic that does not exist fails with "Unknown topic or partition" and the
    /// second succeeds. The platform provisions its topics up front for its own
    /// reasons (tools/topics, and the cleanup policy each one needs), so a test
    /// that leaned on auto-creation would be testing something the deployment does
    /// not do.
    /// </summary>
    private async Task<string> NewTopicAsync()
    {
        var topic = "eauction.endoffset." + Guid.NewGuid().ToString("N")[..12];

        using var publisher = new KafkaTopicPublisher(
            new KafkaPublisherOptions { BootstrapServers = kafka.Bootstrap!, ReplicationFactor = 1 },
            NullLogger<KafkaTopicPublisher>.Instance);

        await publisher.EnsureTopicAsync(topic, Ct);
        return topic;
    }

    [RequiresKafkaFact]
    public async Task A_topic_that_does_not_exist_has_no_last_offset()
    {
        // The honest answer for "nothing has been written", and the one the
        // notification service reads as "no history to absorb".
        await using var stream = Stream();

        Assert.Equal(
            -1, await stream.LatestOffsetAsync(
                "eauction.absent." + Guid.NewGuid().ToString("N"), Ct));
    }

    [RequiresKafkaFact]
    public async Task A_topic_that_exists_but_is_empty_has_no_last_offset()
    {
        // The case a quiet period cannot tell apart from a slow broker, and the one
        // that matters: an empty topic really has no history, and a
        // provisioned-but-unused topic is the state of a fresh cluster.
        await using var stream = Stream();

        Assert.Equal(-1, await stream.LatestOffsetAsync(await NewTopicAsync(), Ct));
    }

    [RequiresKafkaFact]
    public async Task The_last_offset_is_the_offset_of_the_last_record()
    {
        await using var stream = Stream();
        var topic = await NewTopicAsync();

        for (var i = 0; i < 5; i++)
            await stream.PublishAsync(topic, "k", $"{{\"n\":{i}}}", "Test", Ct);

        // Five records at offsets 0..4.
        Assert.Equal(4, await stream.LatestOffsetAsync(topic, Ct));

        await stream.PublishAsync(topic, "k", "{\"n\":5}", "Test", Ct);

        Assert.Equal(5, await stream.LatestOffsetAsync(topic, Ct));
    }

    [RequiresKafkaFact]
    public async Task Reading_up_to_the_last_offset_sees_every_record_exactly_once()
    {
        // The pattern the notification service's first run uses: ask where the end
        // is, then read until you reach it. If the answer were off by one this
        // would hang or drop the last record, and both failures look like
        // something else entirely a hundred lines downstream.
        await using var stream = Stream();
        var topic = await NewTopicAsync();

        for (var i = 0; i < 8; i++)
            await stream.PublishAsync(topic, "k", $"{{\"n\":{i}}}", "Test", Ct);

        var end = await stream.LatestOffsetAsync(topic, Ct);
        var seen = new List<long>();

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));

        await foreach (var record in stream.ReadAsync(topic, bounded.Token))
        {
            seen.Add(record.Offset);
            if (record.Offset >= end) break;
        }

        Assert.Equal(Enumerable.Range(0, 8).Select(i => (long)i), seen);
    }
}
