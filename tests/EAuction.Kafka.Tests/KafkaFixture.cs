using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Xunit;

namespace EAuction.Kafka.Tests;

/// <summary>
/// Runs against a real broker, which is the point: three Kafka
/// implementations and a Debezium connector were written without one ever
/// being available, and in-memory stand-ins cannot tell you whether the
/// client is configured correctly.
///
/// Set KAFKA_BOOTSTRAP to run. Without it these tests skip rather than fail,
/// so the suite still works where no broker exists.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    public string? Bootstrap { get; } = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP");

    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Bootstrap)) return;

        try
        {
            using var admin = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = Bootstrap }).Build();
            admin.GetMetadata(TimeSpan.FromSeconds(10));
            Available = true;
        }
        catch
        {
            Available = false;
        }

        await Task.CompletedTask;
    }

    public async Task CreateTopicAsync(string name, int partitions = 1, bool compacted = false)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = Bootstrap }).Build();

        try
        {
            await admin.CreateTopicsAsync(new[]
            {
                new TopicSpecification
                {
                    Name = name,
                    NumPartitions = partitions,
                    ReplicationFactor = 1,
                    Configs = new Dictionary<string, string>
                    {
                        ["cleanup.policy"] = compacted ? "compact" : "delete"
                    }
                }
            });
        }
        catch (CreateTopicsException ex)
            when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists)) { }
    }

    public int PartitionCountOf(string topic)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = Bootstrap }).Build();

        var meta = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        return meta.Topics.Single(t => t.Topic == topic).Partitions.Count;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("kafka")]
public sealed class KafkaCollection : ICollectionFixture<KafkaFixture>;

/// <summary>Skips the test when no broker is configured, rather than failing.</summary>
public sealed class RequiresKafkaFactAttribute : FactAttribute
{
    public RequiresKafkaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP")))
            Skip = "KAFKA_BOOTSTRAP is not set.";
    }
}
