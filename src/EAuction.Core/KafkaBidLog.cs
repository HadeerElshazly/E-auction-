using Confluent.Kafka;

namespace EAuction.Core;

public sealed record KafkaBidLogOptions
{
    public required string BootstrapServers { get; init; }

    /// <summary>Topic naming strategy. Topic-per-auction (D-02 scale is small).</summary>
    public string TopicPrefix { get; init; } = "bids.";

    /// <summary>
    /// acks=all is not negotiable (D-11): losing a bid in a government land
    /// auction is a legal problem, not a performance footnote.
    /// </summary>
    public Acks Acks { get; init; } = Acks.All;

    public int LingerMs { get; init; } = 5;
    public string ConsumerGroup { get; init; } = "bid-processor";
}

/// <summary>
/// Kafka-backed bid log. Produces with acks=all and returns only once the
/// delivery report confirms durability.
/// </summary>
/// <remarks>
/// NOT YET EXERCISED AGAINST A LIVE BROKER — the development container has no
/// Docker daemon, so the end-to-end pipeline is validated through
/// <see cref="InMemoryBidLog"/>, which mirrors the same ordering contract.
/// This implementation must be run against a real cluster before deployment.
/// </remarks>
public sealed class KafkaBidLog : IBidLog
{
    private readonly KafkaBidLogOptions _options;
    private readonly IProducer<byte[], byte[]> _producer;

    public KafkaBidLog(KafkaBidLogOptions options)
    {
        _options = options;
        _producer = new ProducerBuilder<byte[], byte[]>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = options.Acks,
            LingerMs = options.LingerMs,
            EnableIdempotence = true,
            CompressionType = CompressionType.None
        }).Build();
    }

    private string TopicFor(Guid auctionId) => _options.TopicPrefix + auctionId.ToString("N");

    public async ValueTask<long> AppendAsync(
        Guid auctionId, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var result = await _producer.ProduceAsync(
            TopicFor(auctionId),
            new Message<byte[], byte[]>
            {
                Key = auctionId.ToByteArray(),
                Value = frame.ToArray()
            },
            ct).ConfigureAwait(false);

        if (result.Status != PersistenceStatus.Persisted)
            throw new InvalidOperationException(
                $"Bid not persisted for auction {auctionId}: {result.Status}");

        return result.Offset.Value;
    }

    public async IAsyncEnumerable<LoggedBid> ReadAsync(
        Guid auctionId, long fromOffset,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();

        var partition = new TopicPartitionOffset(
            TopicFor(auctionId), new Partition(0), new Offset(fromOffset));
        consumer.Assign(partition);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Consume blocks, so it is pushed off the async iterator's
                // thread rather than stalling the caller's scheduler.
                var result = await Task.Run(() =>
                {
                    try
                    {
                        return consumer.Consume(TimeSpan.FromMilliseconds(250));
                    }
                    catch (ConsumeException)
                    {
                        return null;
                    }
                }, ct).ConfigureAwait(false);

                if (result?.Message is null) continue;
                yield return new LoggedBid(result.Offset.Value, result.Message.Value);
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
