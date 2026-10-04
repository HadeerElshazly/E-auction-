using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace EAuction.AuctionAdmin.Outbox;

public sealed record KafkaPublisherOptions
{
    public required string BootstrapServers { get; init; }
    public short ReplicationFactor { get; init; } = 3;
    public int BidTopicPartitions { get; init; } = 12;
}

/// <summary>
/// Kafka-backed publisher for the outbox relay.
/// </summary>
/// <remarks>
/// NOT YET EXERCISED AGAINST A LIVE BROKER — no Docker daemon in the build
/// environment. The relay's routing and ordering are tested through
/// <see cref="InMemoryTopicPublisher"/>; this class must be run against a real
/// cluster before deployment.
/// </remarks>
public sealed class KafkaTopicPublisher : ITopicPublisher, IDisposable
{
    private readonly KafkaPublisherOptions _options;
    private readonly IProducer<string, string> _producer;
    private readonly IAdminClient _admin;
    private readonly ILogger<KafkaTopicPublisher> _logger;

    public KafkaTopicPublisher(KafkaPublisherOptions options, ILogger<KafkaTopicPublisher> logger)
    {
        _options = options;
        _logger = logger;

        var config = new ClientConfig { BootstrapServers = options.BootstrapServers };
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig(config) { Acks = Acks.All, EnableIdempotence = true }).Build();
        _admin = new AdminClientBuilder(config).Build();
    }

    public async Task EnsureTopicAsync(string topic, CancellationToken ct)
    {
        try
        {
            await _admin.CreateTopicsAsync(new[]
            {
                new TopicSpecification
                {
                    Name = topic,
                    NumPartitions = _options.BidTopicPartitions,
                    ReplicationFactor = _options.ReplicationFactor,
                    Configs = new Dictionary<string, string>
                    {
                        // Bids are the legal record: never compact, never age out
                        // on a schedule shorter than the retention policy allows.
                        ["cleanup.policy"] = "delete",
                        ["min.insync.replicas"] = "2"
                    }
                }
            });
        }
        catch (CreateTopicsException ex)
            when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // The relay is at-least-once, so re-creating after a crash is the
            // expected path, not an error.
            _logger.LogDebug("Topic {Topic} already exists.", topic);
        }
    }

    public async Task PublishAsync(
        string topic, string key, string payload, string eventType, CancellationToken ct)
    {
        var message = new Message<string, string>
        {
            Key = key,
            Value = payload,
            Headers = new Headers { { "eventType", System.Text.Encoding.UTF8.GetBytes(eventType) } }
        };

        var result = await _producer.ProduceAsync(topic, message, ct);
        if (result.Status != PersistenceStatus.Persisted)
            throw new InvalidOperationException(
                $"Outbox message not persisted to {topic}: {result.Status}");
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        _admin.Dispose();
    }
}

/// <summary>Records what was published, in order, so the relay can be tested.</summary>
public sealed class InMemoryTopicPublisher : ITopicPublisher
{
    private readonly List<string> _actions = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Actions { get { lock (_gate) return _actions.ToList(); } }
    public List<(string Topic, string Key, string Payload, string EventType)> Published { get; } = new();

    /// <summary>Set to fail the next publish, to test that the row stays queued.</summary>
    public Func<string, bool>? FailPublishFor { get; set; }

    public Task EnsureTopicAsync(string topic, CancellationToken ct)
    {
        lock (_gate) _actions.Add($"ensure-topic:{topic}");
        return Task.CompletedTask;
    }

    public Task PublishAsync(
        string topic, string key, string payload, string eventType, CancellationToken ct)
    {
        if (FailPublishFor?.Invoke(eventType) == true)
            throw new InvalidOperationException($"Simulated publish failure for {eventType}.");

        lock (_gate)
        {
            _actions.Add($"publish:{topic}:{eventType}:{key}");
            Published.Add((topic, key, payload, eventType));
        }
        return Task.CompletedTask;
    }
}
