using System.Collections.Concurrent;
using System.Threading.Channels;

namespace EAuction.Core;

/// <summary>
/// In-process bid log that mirrors Kafka's per-partition semantics: a single
/// monotonic offset sequence per auction, append-only, readers see records in
/// append order and never miss one.
///
/// Used for local development and tests, where running a broker is not
/// practical. It is NOT a production store — it holds no data across restarts.
/// </summary>
public sealed class InMemoryBidLog : IBidLog
{
    private sealed class Stream
    {
        public readonly List<ReadOnlyMemory<byte>> Records = new();
        public readonly List<Channel<LoggedBid>> Subscribers = new();
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<Guid, Stream> _streams = new();

    public ValueTask<long> AppendAsync(
        Guid auctionId, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var stream = _streams.GetOrAdd(auctionId, _ => new Stream());

        // Copy: the caller may reuse its buffer the moment this returns.
        var stored = frame.ToArray().AsMemory();

        long offset;
        List<Channel<LoggedBid>> subscribers;
        lock (stream.Gate)
        {
            stream.Records.Add(stored);
            offset = stream.Records.Count - 1;
            subscribers = stream.Subscribers.ToList();
        }

        var logged = new LoggedBid(offset, stored);
        foreach (var s in subscribers) s.Writer.TryWrite(logged);

        return ValueTask.FromResult(offset);
    }

    public async IAsyncEnumerable<LoggedBid> ReadAsync(
        Guid auctionId, long fromOffset,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var stream = _streams.GetOrAdd(auctionId, _ => new Stream());
        var channel = Channel.CreateUnbounded<LoggedBid>();

        List<ReadOnlyMemory<byte>> backlog;
        lock (stream.Gate)
        {
            backlog = stream.Records.Skip((int)fromOffset).ToList();
            stream.Subscribers.Add(channel);
        }

        // Replay what was already there, then follow the tail.
        for (var i = 0; i < backlog.Count; i++)
            yield return new LoggedBid(fromOffset + i, backlog[i]);

        var nextExpected = fromOffset + backlog.Count;
        await foreach (var record in channel.Reader.ReadAllAsync(ct))
        {
            // Drop anything the backlog already covered.
            if (record.Offset < nextExpected) continue;
            nextExpected = record.Offset + 1;
            yield return record;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
