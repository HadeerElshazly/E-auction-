// A type cannot precede the top-level statements in Program.cs, so this lives in
// its own file rather than at the bottom of that one — where it would be a long
// way from the endpoint it serves.
//
// Deliberately in the global namespace, like the rest of this project's
// Program.cs-adjacent helpers, so the endpoint reads without a using.

/// <summary>
/// The last N bid frames read for a certificate, so reading one twice costs one
/// Kafka seek.
///
/// Sound because of what a frame is: the record at one offset of an append-only
/// log, which cannot change once written. There is no invalidation to get wrong
/// and no staleness to reason about — a hit and a miss return the same bytes.
///
/// Bounded by eviction in insertion order rather than by time. A certificate is
/// looked at in the minutes after a bid and then rarely, so the useful window is
/// short and recency is the right thing to keep; an unbounded dictionary on a
/// service with a latency budget is a slow leak, and a time-based expiry would
/// throw away entries that are still correct.
/// </summary>
sealed class FrameCache(int capacity)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, long), ReadOnlyMemory<byte>> _frames = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Guid, long)> _order = new();

    /// <summary>
    /// True and the frame if it is held, false otherwise.
    ///
    /// A Try pattern and not a <c>ReadOnlyMemory&lt;byte&gt;?</c>, because that
    /// signature cannot express a miss. <c>ReadOnlyMemory&lt;T&gt;</c> has an
    /// implicit conversion from <c>T[]</c>, and the null literal converts to
    /// <c>byte[]</c> — so in <c>found ? frame : null</c> the compiler finds a
    /// natural type of <c>ReadOnlyMemory&lt;byte&gt;</c> rather than treating the
    /// branches as nullable, and the miss comes back as a non-null nullable holding
    /// an empty frame. It compiles, <c>is null</c> is false for every lookup, and
    /// the caller then signs nothing: eight certificate tests went from a signature
    /// to an ArgumentOutOfRangeException. No overload here can be read that way.
    /// </summary>
    public bool TryGet(Guid auctionId, long offset, out ReadOnlyMemory<byte> frame) =>
        _frames.TryGetValue((auctionId, offset), out frame);

    public void Put(Guid auctionId, long offset, ReadOnlyMemory<byte> frame)
    {
        if (!_frames.TryAdd((auctionId, offset), frame)) return;

        _order.Enqueue((auctionId, offset));

        // One eviction per insertion, so the queue cannot outgrow the dictionary by
        // more than the handful of entries in flight.
        while (_frames.Count > capacity && _order.TryDequeue(out var oldest))
            _frames.TryRemove(oldest, out _);
    }
}
