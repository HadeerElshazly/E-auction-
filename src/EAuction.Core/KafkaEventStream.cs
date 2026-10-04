using Confluent.Kafka;

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
/// NOT YET EXERCISED AGAINST A LIVE BROKER — no Docker daemon in the build
/// environment. Behaviour is validated through <see cref="InMemoryEventStream"/>,
/// which mirrors the same ordering contract. Run this against a real cluster
/// before deployment.
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
                    eventType, result.Offset.Value);
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
