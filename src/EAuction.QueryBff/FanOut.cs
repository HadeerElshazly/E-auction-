using System.Collections.Concurrent;
using System.Threading.Channels;

namespace EAuction.QueryBff;

/// <summary>
/// The push channel (§7.2): who is watching which auction, and what each of them is
/// allowed to be told.
///
/// Replaces polling on the hot path. At the design target of 10,000 concurrent
/// bidders, a one-second poll in the final minute of an auction is ~10,000 requests
/// a second on this service — the same order as the bid load itself — to learn a
/// number that changed perhaps fifty times. Pushing it costs one message per actual
/// change.
///
/// Two rules shape everything here:
///
/// **A snapshot on connect, then deltas.** A reconnecting client is not replayed
/// from a log; it is told the truth as of now and then kept current. That is
/// self-healing — a delta missed while disconnected is irrelevant because the
/// snapshot supersedes it — and it is the only option honestly available, because
/// this service's state is a compacted read model and could not serve arbitrary
/// history.
///
/// **A bid verdict is an event, not state**, so unlike the price it is NOT in the
/// snapshot and a dropped one is simply lost. Each bidder therefore keeps a small
/// bounded buffer of their recent verdicts, replayed on connect. Without it a
/// two-second network blip during a bidding war loses the one message that explains
/// why a bid failed.
/// </summary>
public sealed class FanOut
{
    private readonly ConcurrentDictionary<Guid, AuctionChannel> _auctions = new();

    public int AuctionCount => _auctions.Count;
    public int SubscriberCount => _auctions.Values.Sum(a => a.SubscriberCount);

    /// <summary>
    /// Registers a viewer and returns their stream. Disposing the subscription
    /// removes it; an auction with no subscribers left is forgotten.
    /// </summary>
    public Subscription Subscribe(Guid auctionId, Guid? viewer)
    {
        var channel = _auctions.GetOrAdd(auctionId, _ => new AuctionChannel());
        return channel.Add(auctionId, viewer, this);
    }

    /// <summary>
    /// A price change, for everyone watching.
    ///
    /// Serialised three times rather than once per subscriber: the payload varies
    /// only by what the recipient is entitled to know about identity — one body for
    /// the leader, one for the other signed-in bidders, one for a visitor who gets
    /// no bidder label at all. At ten thousand subscribers that is the difference
    /// between three serialisations per change and ten thousand.
    ///
    /// The anonymous body is a third variant rather than a filter applied per
    /// connection, for the same reason: a per-subscriber rewrite would put the
    /// serialiser back on the hot path.
    /// </summary>
    public void PublishPrice(
        Guid auctionId, string forOthers, string forLeader, string forAnonymous, Guid? leader)
    {
        if (!_auctions.TryGetValue(auctionId, out var channel)) return;
        channel.Broadcast(forOthers, forLeader, forAnonymous, leader);
    }

    /// <summary>One bidder's own verdict. Buffered, then sent to their streams only.</summary>
    public void PublishVerdict(Guid auctionId, Guid bidderId, string payload)
    {
        // Buffered even with nobody connected: the bidder may be mid-reconnect, and
        // this is the message they most need when they come back.
        var channel = _auctions.GetOrAdd(auctionId, _ => new AuctionChannel());
        channel.RecordVerdict(bidderId, payload);
    }

    /// <summary>Verdicts this bidder has not necessarily seen, oldest first.</summary>
    public IReadOnlyList<string> RecentVerdicts(Guid auctionId, Guid? viewer) =>
        viewer is null || !_auctions.TryGetValue(auctionId, out var channel)
            ? []
            : channel.RecentVerdicts(viewer.Value);

    /// <summary>Called when an auction ends, so its buffers and channel are released.</summary>
    public void Forget(Guid auctionId) => _auctions.TryRemove(auctionId, out _);

    internal void Unsubscribe(Guid auctionId, Subscription subscription)
    {
        if (!_auctions.TryGetValue(auctionId, out var channel)) return;

        // Keep the channel while verdicts are buffered: a bidder with no connection
        // is exactly the case the buffer exists for.
        if (channel.Remove(subscription) && !channel.HasBufferedVerdicts)
            _auctions.TryRemove(auctionId, out _);
    }

    private sealed class AuctionChannel
    {
        private readonly ConcurrentDictionary<Subscription, byte> _subscribers = new();
        private readonly ConcurrentDictionary<Guid, VerdictBuffer> _verdicts = new();

        public int SubscriberCount => _subscribers.Count;
        public bool HasBufferedVerdicts => !_verdicts.IsEmpty;

        public Subscription Add(Guid auctionId, Guid? viewer, FanOut owner)
        {
            var subscription = new Subscription(auctionId, viewer, owner);
            _subscribers[subscription] = 0;
            return subscription;
        }

        public bool Remove(Subscription subscription) =>
            _subscribers.TryRemove(subscription, out _) && _subscribers.IsEmpty;

        public void Broadcast(
            string forOthers, string forLeader, string forAnonymous, Guid? leader)
        {
            foreach (var subscriber in _subscribers.Keys)
                subscriber.Offer(
                    // A null viewer is a visitor who never signed in, and the first
                    // branch has to be theirs: they are also "not the leader", so
                    // testing for the leader first would hand them the labelled body.
                    subscriber.Viewer is null ? forAnonymous
                    : subscriber.Viewer == leader ? forLeader
                    : forOthers);
        }

        public void RecordVerdict(Guid bidderId, string payload)
        {
            _verdicts.GetOrAdd(bidderId, _ => new VerdictBuffer()).Add(payload);

            foreach (var subscriber in _subscribers.Keys)
                if (subscriber.Viewer == bidderId)
                    subscriber.Offer(payload);
        }

        public IReadOnlyList<string> RecentVerdicts(Guid bidderId) =>
            _verdicts.TryGetValue(bidderId, out var buffer) ? buffer.Snapshot() : [];
    }

    /// <summary>
    /// The last few verdicts for one bidder in one auction.
    ///
    /// Bounded because it is memory an unauthenticated-ish party influences: a
    /// bidder who submits a thousand doomed bids must not be able to make this grow
    /// with them. Ten is enough to cover a reconnect and nothing more.
    /// </summary>
    private sealed class VerdictBuffer
    {
        private const int Capacity = 10;
        private readonly Queue<string> _items = new(Capacity);
        private readonly object _gate = new();

        public void Add(string payload)
        {
            lock (_gate)
            {
                if (_items.Count == Capacity) _items.Dequeue();
                _items.Enqueue(payload);
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return _items.ToArray();
        }
    }
}

/// <summary>
/// One viewer's stream of one auction.
///
/// The queue is bounded and drops its oldest message when full. For a price that is
/// harmless — the next one supersedes it — and for a verdict the per-bidder buffer
/// covers the gap on reconnect. The alternative, an unbounded queue, means one
/// client on a stalled connection can grow a replica's memory without limit.
/// </summary>
public sealed class Subscription : IDisposable
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Guid _auctionId;
    private readonly FanOut _owner;
    private int _disposed;

    internal Subscription(Guid auctionId, Guid? viewer, FanOut owner)
    {
        _auctionId = auctionId;
        Viewer = viewer;
        _owner = owner;
    }

    /// <summary>The authenticated subject, or null for an anonymous watcher.</summary>
    public Guid? Viewer { get; }

    internal void Offer(string payload) => _queue.Writer.TryWrite(payload);

    /// <summary>
    /// Completes when something is readable, or false when the stream is finished.
    ///
    /// This pair rather than <c>ReadAllAsync</c> because the SSE handler has to race
    /// a read against a keep-alive timer, and the enumerator <c>ReadAllAsync</c>
    /// returns cannot be disposed outside an <c>await foreach</c> — doing so throws
    /// <see cref="NotSupportedException"/> and takes the connection down. That is
    /// also why the returned ValueTask is converted to a Task once by the caller and
    /// held: a ValueTask may be consumed exactly once.
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) =>
        _queue.Reader.WaitToReadAsync(ct);

    public bool TryRead(out string payload) => _queue.Reader.TryRead(out payload!);

    public void Dispose()
    {
        // Idempotent: ASP.NET disposes the subscription when the request ends, and
        // the handler's own finally block does too when the client disconnects.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _queue.Writer.TryComplete();
        _owner.Unsubscribe(_auctionId, this);
    }
}
