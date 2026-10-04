using System.Collections.Concurrent;
using System.Text.Json;
using EAuction.Core;

namespace EAuction.BidProcessor;

/// <summary>
/// How far the processor has published side effects for each auction.
///
/// This is not a replacement for replaying the bid log. The engine's state —
/// price, leader, ladder, extensions, ledger — is rebuilt by replaying every
/// bid from offset 0, which is deterministic and reproduces the same result
/// every time. The checkpoint answers a different question: which of those
/// bids have already had their <c>current-winner</c> and <c>bids.rejected</c>
/// events published, so the replay can stay silent over them.
///
/// Lives on a compacted Kafka topic rather than local disk because the
/// processor must be restartable on any node.
/// </summary>
public sealed class CheckpointStore(IEventStream events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, long> _published = new();

    /// <summary>
    /// The highest bid-log offset whose side effects are known to be published.
    /// -1 means nothing has been published for this auction.
    /// </summary>
    public long PublishedThrough(Guid auctionId) =>
        _published.TryGetValue(auctionId, out var offset) ? offset : -1;

    public void Apply(StreamEvent record)
    {
        if (record.EventType != nameof(BidCheckpoint)) return;

        var payload = JsonSerializer.Deserialize<BidCheckpoint>(record.Payload, Json);
        if (payload is null) return;

        // Compaction keeps the last value per key, but a replay can still see
        // older ones; never move a checkpoint backwards.
        _published.AddOrUpdate(payload.AuctionId, payload.PublishedThroughOffset,
            (_, existing) => Math.Max(existing, payload.PublishedThroughOffset));
    }

    public async Task CommitAsync(Guid auctionId, long offset, CancellationToken ct)
    {
        _published.AddOrUpdate(auctionId, offset, (_, existing) => Math.Max(existing, offset));

        await events.PublishAsync(Topics.Checkpoints, auctionId.ToString(),
            JsonSerializer.Serialize(new BidCheckpoint
            {
                AuctionId = auctionId,
                PublishedThroughOffset = offset
            }, Json),
            nameof(BidCheckpoint), ct);
    }
}

public sealed record BidCheckpoint
{
    public Guid AuctionId { get; init; }
    public long PublishedThroughOffset { get; init; }
}

/// <summary>
/// How often the checkpoint is written. Committing on every bid would double
/// the write load on the hot path for no real gain: delivery is at-least-once
/// either way, so the only cost of a stale checkpoint is republishing the last
/// few records after a restart, which consumers already have to tolerate.
/// </summary>
public sealed record CheckpointPolicy
{
    public int EveryRecords { get; init; } = 100;
    public TimeSpan EveryInterval { get; init; } = TimeSpan.FromSeconds(2);
}
