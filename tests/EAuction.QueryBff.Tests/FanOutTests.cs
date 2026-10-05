using EAuction.QueryBff;
using Xunit;

namespace EAuction.QueryBff.Tests;

/// <summary>
/// The push channel's routing rules. Each of these is a way a viewer could be told
/// something they are not entitled to, or not told something they need.
/// </summary>
public class FanOutTests
{
    private static readonly Guid Auction = Guid.NewGuid();

    /// <summary>
    /// Drains with the same pair the SSE handler uses, so the tests exercise the
    /// production read path rather than a convenience one.
    /// </summary>
    private static async Task<List<string>> DrainAsync(Subscription s, int expected)
    {
        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            while (received.Count < expected && await s.WaitToReadAsync(cts.Token))
                while (received.Count < expected && s.TryRead(out var message))
                    received.Add(message);
        }
        catch (OperationCanceledException)
        {
            // Fewer than expected; the assertion that follows says so usefully.
        }

        return received;
    }

    [Fact]
    public async Task A_price_change_reaches_every_watcher()
    {
        var fanOut = new FanOut();
        using var anonymous = fanOut.Subscribe(Auction, null);
        using var bidder = fanOut.Subscribe(Auction, Guid.NewGuid());

        fanOut.PublishPrice(Auction, "others", "leader", leader: null);

        Assert.Equal(["others"], await DrainAsync(anonymous, 1));
        Assert.Equal(["others"], await DrainAsync(bidder, 1));
    }

    [Fact]
    public async Task Only_the_leader_gets_the_leader_payload()
    {
        // The whole of D-22 on this channel. If the leader variant went to everyone,
        // every watcher would learn the leading bidder's own bid id.
        var fanOut = new FanOut();
        var sara = Guid.NewGuid();
        var khalid = Guid.NewGuid();

        using var saraStream = fanOut.Subscribe(Auction, sara);
        using var khalidStream = fanOut.Subscribe(Auction, khalid);
        using var anonymous = fanOut.Subscribe(Auction, null);

        fanOut.PublishPrice(Auction, "masked", "you-lead", leader: sara);

        Assert.Equal(["you-lead"], await DrainAsync(saraStream, 1));
        Assert.Equal(["masked"], await DrainAsync(khalidStream, 1));
        Assert.Equal(["masked"], await DrainAsync(anonymous, 1));
    }

    [Fact]
    public async Task An_anonymous_watcher_is_never_the_leader()
    {
        // Guards a null-equals-null slip: Viewer is null for anonymous, and
        // LeaderBidderId is null before the first bid.
        var fanOut = new FanOut();
        using var anonymous = fanOut.Subscribe(Auction, null);

        fanOut.PublishPrice(Auction, "masked", "you-lead", leader: null);

        Assert.Equal(["masked"], await DrainAsync(anonymous, 1));
    }

    [Fact]
    public async Task A_verdict_reaches_only_its_own_bidder()
    {
        var fanOut = new FanOut();
        var sara = Guid.NewGuid();
        var khalid = Guid.NewGuid();

        using var saraStream = fanOut.Subscribe(Auction, sara);
        using var khalidStream = fanOut.Subscribe(Auction, khalid);
        using var anonymous = fanOut.Subscribe(Auction, null);

        fanOut.PublishVerdict(Auction, sara, "sara-was-outbid");

        Assert.Equal(["sara-was-outbid"], await DrainAsync(saraStream, 1));
        Assert.Empty(await DrainAsync(khalidStream, 1));
        Assert.Empty(await DrainAsync(anonymous, 1));
    }

    [Fact]
    public void A_verdict_is_buffered_for_a_bidder_who_is_not_connected()
    {
        // The case the buffer exists for: a verdict is an event, not state, so it is
        // absent from the snapshot and a two-second blip during a bidding war would
        // otherwise lose the one message explaining why a bid failed.
        var fanOut = new FanOut();
        var sara = Guid.NewGuid();

        fanOut.PublishVerdict(Auction, sara, "rejected-while-you-were-away");

        Assert.Equal(["rejected-while-you-were-away"], fanOut.RecentVerdicts(Auction, sara));
    }

    [Fact]
    public void One_bidder_cannot_read_another_bidders_buffered_verdicts()
    {
        var fanOut = new FanOut();
        var sara = Guid.NewGuid();

        fanOut.PublishVerdict(Auction, sara, "saras-business");

        Assert.Empty(fanOut.RecentVerdicts(Auction, Guid.NewGuid()));
        Assert.Empty(fanOut.RecentVerdicts(Auction, null));
    }

    [Fact]
    public void The_verdict_buffer_is_bounded()
    {
        // Memory a bidder influences directly: someone submitting a thousand doomed
        // bids must not be able to grow a replica's heap with them.
        var fanOut = new FanOut();
        var sara = Guid.NewGuid();

        for (var i = 0; i < 100; i++) fanOut.PublishVerdict(Auction, sara, $"verdict-{i}");

        var kept = fanOut.RecentVerdicts(Auction, sara);
        Assert.Equal(10, kept.Count);

        // The newest, not the oldest: what just happened is what the bidder needs.
        Assert.Equal("verdict-99", kept[^1]);
        Assert.Equal("verdict-90", kept[0]);
    }

    [Fact]
    public async Task A_stalled_subscriber_drops_old_prices_instead_of_growing()
    {
        // An unbounded queue means one client on a dead connection can take a
        // replica down. Dropping the oldest price is safe because the next one
        // supersedes it, and a client this far behind will reconnect and be sent a
        // fresh snapshot anyway.
        var fanOut = new FanOut();
        using var stalled = fanOut.Subscribe(Auction, null);

        for (var i = 0; i < 500; i++)
            fanOut.PublishPrice(Auction, $"price-{i}", "leader", leader: null);

        var received = await DrainAsync(stalled, 32);

        Assert.Equal(32, received.Count);
        Assert.Equal("price-499", received[^1]);
    }

    [Fact]
    public void Disposing_the_last_subscription_releases_the_auction()
    {
        var fanOut = new FanOut();
        var subscription = fanOut.Subscribe(Auction, null);
        Assert.Equal(1, fanOut.SubscriberCount);

        subscription.Dispose();

        Assert.Equal(0, fanOut.SubscriberCount);
        Assert.Equal(0, fanOut.AuctionCount);
    }

    [Fact]
    public void An_auction_with_buffered_verdicts_is_kept_after_the_last_disconnect()
    {
        // Releasing it here would throw away exactly the verdicts a reconnecting
        // bidder is about to ask for.
        var fanOut = new FanOut();
        var sara = Guid.NewGuid();

        var subscription = fanOut.Subscribe(Auction, sara);
        fanOut.PublishVerdict(Auction, sara, "outbid");
        subscription.Dispose();

        Assert.Equal(0, fanOut.SubscriberCount);
        Assert.Equal(1, fanOut.AuctionCount);
        Assert.Single(fanOut.RecentVerdicts(Auction, sara));

        // And Forget releases it, which is what the end of an auction does.
        fanOut.Forget(Auction);
        Assert.Equal(0, fanOut.AuctionCount);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        // ASP.NET disposes the subscription when the request ends and the handler's
        // using block does too; a double release must not corrupt the count.
        var fanOut = new FanOut();
        using var other = fanOut.Subscribe(Auction, null);
        var subscription = fanOut.Subscribe(Auction, null);

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(1, fanOut.SubscriberCount);
    }
}
