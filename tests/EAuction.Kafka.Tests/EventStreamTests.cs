using EAuction.Core;
using EAuction.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Kafka.Tests;

[Collection("kafka")]
public class EventStreamTests(KafkaFixture kafka)
{
    private KafkaEventStream Stream() => new(new KafkaEventStreamOptions
    {
        BootstrapServers = kafka.Bootstrap!,
        ConsumerGroup = "stream-test"
    });

    private static async Task<List<StreamEvent>> TakeAsync(
        IEventStream stream, string topic, int count, TimeSpan timeout)
    {
        var found = new List<StreamEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var record in stream.ReadAsync(topic, cts.Token))
            {
                found.Add(record);
                if (found.Count == count) break;
            }
        }
        catch (OperationCanceledException) { }
        return found;
    }

    [RequiresKafkaFact]
    public async Task A_published_event_round_trips_with_its_key_and_type()
    {
        var topic = "test.roundtrip." + Guid.NewGuid().ToString("N")[..8];
        await kafka.CreateTopicAsync(topic);

        await using var stream = Stream();
        await stream.PublishAsync(topic, "key-1", """{"value":42}""", "ThingHappened",
            CancellationToken.None);

        var record = Assert.Single(await TakeAsync(stream, topic, 1, TimeSpan.FromSeconds(20)));

        Assert.Equal("key-1", record.Key);
        Assert.Equal("ThingHappened", record.EventType);
        Assert.Contains("42", record.Payload);
    }

    [RequiresKafkaFact]
    public async Task A_consumer_replays_a_topic_from_the_beginning()
    {
        // Control topics are rebuilt by replay on every start — the catcher and
        // the processor both depend on it instead of a database.
        var topic = "test.replay." + Guid.NewGuid().ToString("N")[..8];
        await kafka.CreateTopicAsync(topic);

        await using var writer = Stream();
        for (var i = 0; i < 12; i++)
            await writer.PublishAsync(topic, $"k{i}", $$"""{"n":{{i}}}""", "N",
                CancellationToken.None);

        // A consumer that was not running while any of that happened.
        await using var cold = Stream();
        var records = await TakeAsync(cold, topic, 12, TimeSpan.FromSeconds(25));

        Assert.Equal(12, records.Count);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"k{i}"), records.Select(r => r.Key));
    }

    [RequiresKafkaFact]
    public async Task Two_consumers_each_get_a_full_copy()
    {
        // Every catcher pod holds the whole control plane, so they must not
        // share a consumer group and split the partitions between them.
        var topic = "test.broadcast." + Guid.NewGuid().ToString("N")[..8];
        await kafka.CreateTopicAsync(topic, partitions: 3);

        await using var writer = Stream();
        for (var i = 0; i < 9; i++)
            await writer.PublishAsync(topic, $"k{i}", "{}", "N", CancellationToken.None);

        await using var first = Stream();
        await using var second = Stream();

        var a = TakeAsync(first, topic, 9, TimeSpan.FromSeconds(25));
        var b = TakeAsync(second, topic, 9, TimeSpan.FromSeconds(25));
        await Task.WhenAll(a, b);

        Assert.Equal(9, a.Result.Count);
        Assert.Equal(9, b.Result.Count);
    }
}

[Collection("kafka")]
public class TopicPublisherTests(KafkaFixture kafka)
{
    private KafkaTopicPublisher Publisher() => new(
        new KafkaPublisherOptions { BootstrapServers = kafka.Bootstrap!, ReplicationFactor = 1 },
        NullLogger<KafkaTopicPublisher>.Instance);

    [RequiresKafkaFact]
    public async Task Ensuring_a_topic_creates_it_with_exactly_one_partition()
    {
        var auctionId = Guid.NewGuid();
        var topic = Topics.BidTopicFor(auctionId);

        using var publisher = Publisher();
        await publisher.EnsureTopicAsync(topic, CancellationToken.None);

        Assert.Equal(1, kafka.PartitionCountOf(topic));
    }

    [RequiresKafkaFact]
    public async Task Ensuring_a_topic_twice_is_not_an_error()
    {
        // The relay is at-least-once, so re-creating after a crash is the
        // expected path, not a failure.
        var topic = Topics.BidTopicFor(Guid.NewGuid());

        using var publisher = Publisher();
        await publisher.EnsureTopicAsync(topic, CancellationToken.None);
        await publisher.EnsureTopicAsync(topic, CancellationToken.None);

        Assert.Equal(1, kafka.PartitionCountOf(topic));
    }

    [RequiresKafkaFact]
    public async Task A_published_outbox_row_is_readable_with_its_event_type()
    {
        var topic = "test.outbox." + Guid.NewGuid().ToString("N")[..8];
        await kafka.CreateTopicAsync(topic);

        using var publisher = Publisher();
        await publisher.PublishAsync(topic, "agg-1", """{"ok":true}""", "AuctionApproved",
            CancellationToken.None);

        await using var stream = new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = kafka.Bootstrap!,
            ConsumerGroup = "outbox-read"
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        StreamEvent? found = null;
        await foreach (var record in stream.ReadAsync(topic, cts.Token)) { found = record; break; }

        Assert.NotNull(found);
        Assert.Equal("agg-1", found!.Value.Key);
        Assert.Equal("AuctionApproved", found.Value.EventType);
    }
}
