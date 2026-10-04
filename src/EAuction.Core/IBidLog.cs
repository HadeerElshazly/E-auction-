namespace EAuction.Core;

/// <summary>
/// Append-only ordered log of bid frames, one logical stream per auction.
///
/// This is the ordering authority (docs/ARCHITECTURE.md D-03): the offset a
/// record receives on append defines the total order of bids for that auction.
/// Timestamps are audit attributes, never the ordering key.
///
/// Two implementations: <see cref="KafkaBidLog"/> for deployment and
/// <see cref="InMemoryBidLog"/> for local runs and tests. Both guarantee
/// monotonic per-auction offsets and durability-before-acknowledgement.
/// </summary>
public interface IBidLog : IAsyncDisposable
{
    /// <summary>
    /// Appends a frame and returns its offset once the write is durable.
    /// Must not return before durability is confirmed — "accepted" has to mean
    /// "recorded" (D-11).
    /// </summary>
    ValueTask<long> AppendAsync(Guid auctionId, ReadOnlyMemory<byte> frame, CancellationToken ct);

    /// <summary>Reads the stream for an auction from <paramref name="fromOffset"/>.</summary>
    IAsyncEnumerable<LoggedBid> ReadAsync(Guid auctionId, long fromOffset, CancellationToken ct);

    /// <summary>
    /// The offset one past the last record, so a reader can tell when it has
    /// caught up. Returns 0 for an auction with no bids.
    ///
    /// Needed on restart: the engine is rebuilt by replay, and anything that
    /// reads the rebuilt state — the candidate the cascade owes, for one —
    /// has to wait for that replay rather than read a half-built ladder.
    /// </summary>
    ValueTask<long> GetEndOffsetAsync(Guid auctionId, CancellationToken ct);
}

public readonly record struct LoggedBid(long Offset, ReadOnlyMemory<byte> Frame);
