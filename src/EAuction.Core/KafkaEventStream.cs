using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace EAuction.Core;

public sealed record KafkaEventStreamOptions
{
    public required string BootstrapServers { get; init; }
    public required string ConsumerGroup { get; init; }
}

/// <summary>
/// Kafka-backed control-plane stream.
/// </summary>
/// <remarks>
/// Exercised against a live single-node broker by EAuction.Kafka.Tests (which skip
/// without KAFKA_BOOTSTRAP) and by the end-to-end smoke test. Not yet run against a
/// multi-broker cluster, so replication and leader failover are still unverified.
/// </remarks>
public sealed class KafkaEventStream(KafkaEventStreamOptions options) : IEventStream
{
    private readonly IProducer<string, string> _producer =
        new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true
        }).Build();

    public async Task PublishAsync(
        string topic, string key, string payload, string eventType, CancellationToken ct)
    {
        var result = await _producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = key,
            Value = payload,
            Headers = new Headers { { "eventType", System.Text.Encoding.UTF8.GetBytes(eventType) } }
        }, ct).ConfigureAwait(false);

        if (result.Status != PersistenceStatus.Persisted)
            throw new InvalidOperationException($"Event not persisted to {topic}: {result.Status}");
    }

    /// <summary>
    /// Asks the broker where the topic ends, over a short-lived consumer.
    ///
    /// <c>QueryWatermarkOffsets</c> rather than <c>GetWatermarkOffsets</c>: the
    /// second returns what this client has already learned from its own fetches,
    /// which for a consumer that has not read anything is nothing at all.
    ///
    /// The bid path's topics have exactly one partition and the control topics are
    /// provisioned with more, so this sums the partitions' ends and returns the
    /// largest single offset. A caller comparing a record's offset to it is
    /// therefore conservative on a multi-partition topic — it may treat a few of
    /// the newest records as history — which is the direction to be wrong in: the
    /// alternative is announcing the past.
    /// </summary>
    public Task<long> LatestOffsetAsync(string topic, CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = $"{options.ConsumerGroup}-watermark-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
        }).Build();

        // The admin client borrows the consumer's handle, so this is one connection
        // to the broker rather than two.
        using var admin = new DependentAdminClientBuilder(consumer.Handle).Build();

        var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var found = metadata.Topics.FirstOrDefault(t => t.Topic == topic);

        if (found is null || found.Partitions.Count == 0)
            return Task.FromResult(-1L);

        var last = -1L;

        foreach (var partition in found.Partitions)
        {
            var offsets = consumer.QueryWatermarkOffsets(
                new TopicPartition(topic, new Partition(partition.PartitionId)),
                TimeSpan.FromSeconds(10));

            // High is the offset the next record will get, so the last existing
            // one is High - 1. An empty partition has High == Low and gives -1.
            last = Math.Max(last, offsets.High.Value - 1);
        }

        return Task.FromResult(last);
    }

    public async IAsyncEnumerable<StreamEvent> ReadAsync(
        string topic,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            // Each consumer keeps its own full copy of the compacted control
            // topics, so the group must be unique per instance.
            GroupId = $"{options.ConsumerGroup}-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();

        consumer.Subscribe(topic);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await Task.Run(() =>
                {
                    try { return consumer.Consume(TimeSpan.FromMilliseconds(250)); }
                    catch (ConsumeException) { return null; }
                }, ct).ConfigureAwait(false);

                if (result?.Message is null) continue;

                var eventType = "";
                if (result.Message.Headers is not null
                    && result.Message.Headers.TryGetLastBytes("eventType", out var raw))
                    eventType = System.Text.Encoding.UTF8.GetString(raw);

                yield return new StreamEvent(
                    topic, result.Message.Key ?? "", result.Message.Value ?? "",
                    eventType, result.Offset.Value,
                    result.Message.Timestamp.Type == Confluent.Kafka.TimestampType.NotAvailable
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(result.Message.Timestamp.UnixTimestampMs));
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
