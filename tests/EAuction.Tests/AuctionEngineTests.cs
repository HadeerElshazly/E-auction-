using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

public class AuctionEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);

    private static (AuctionEngine Engine, AuctionDefinition Auction) NewEngine(
        TimeSpan? quiet = null, int maxExtensions = 3)
    {
        var auction = TestAuction.Build(Start, End, quiet, maxExtensions);
        return (new AuctionEngine(auction), auction);
    }

    private static BidVerdict Bid(
        AuctionEngine engine, AuctionDefinition auction, long offset,
        Guid bidder, long amount, DateTimeOffset at, Guid? clientBidId = null)
    {
        var client = TestAuction.Frame(auction.AuctionId, bidder, amount, at, clientBidId);
        return engine.Apply(offset, TestAuction.AsServerFrame(client, at));
    }

    [Fact]
    public void First_bid_must_reach_the_opening_price()
    {
        var (engine, auction) = NewEngine();
        var low = Bid(engine, auction, 0, Guid.NewGuid(), 900_000_00, Start.AddMinutes(1));

        Assert.False(low.Accepted);
        Assert.Equal(RejectionReason.BelowMinimumIncrement, low.Reason);
        Assert.Null(engine.ProvisionalLeader);
    }

    [Fact]
    public void Subsequent_bids_must_clear_the_minimum_increment()
    {
        var (engine, auction) = NewEngine();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        Assert.True(Bid(engine, auction, 0, ahmad, 1_200_000_00, Start.AddMinutes(1)).Accepted);

        // 1,220,000 is a raise, but less than the 50,000 increment requires.
        var tooSmall = Bid(engine, auction, 1, sara, 1_220_000_00, Start.AddMinutes(2));
        Assert.False(tooSmall.Accepted);
        Assert.Equal(RejectionReason.BelowMinimumIncrement, tooSmall.Reason);

        Assert.True(Bid(engine, auction, 2, sara, 1_250_000_00, Start.AddMinutes(3)).Accepted);
        Assert.Equal(sara, engine.ProvisionalLeader);
    }

    [Fact]
    public void Equal_bids_resolve_by_offset_order_without_a_tie_break_rule()
    {
        // D-05: the earlier offset is applied, and the later identical amount
        // then fails the increment check. No separate tie-break is needed.
        var (engine, auction) = NewEngine();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();
        var khalid = Guid.NewGuid();

        Bid(engine, auction, 0, ahmad, 1_200_000_00, Start.AddMinutes(1));

        var first = Bid(engine, auction, 1, sara, 1_450_000_00, Start.AddMinutes(2));
        var second = Bid(engine, auction, 2, khalid, 1_450_000_00, Start.AddMinutes(2));

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(RejectionReason.BelowMinimumIncrement, second.Reason);
        Assert.Equal(sara, engine.ProvisionalLeader);
    }

    [Fact]
    public void Leader_cannot_outbid_themselves()
    {
        var (engine, auction) = NewEngine();
        var sara = Guid.NewGuid();

        Assert.True(Bid(engine, auction, 0, sara, 1_200_000_00, Start.AddMinutes(1)).Accepted);

        var again = Bid(engine, auction, 1, sara, 1_300_000_00, Start.AddMinutes(2));
        Assert.False(again.Accepted);
        Assert.Equal(RejectionReason.SelfOutbid, again.Reason);
        Assert.Equal(1_200_000_00, engine.CurrentPrice);
    }

    [Fact]
    public void A_retried_bid_with_the_same_client_id_is_not_counted_twice()
    {
        // Mobile networks drop mid-request; the retry carries the same
        // clientBidId and must not become a second bid.
        var (engine, auction) = NewEngine();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();
        var retryId = Guid.NewGuid();

        Bid(engine, auction, 0, ahmad, 1_200_000_00, Start.AddMinutes(1));

        var original = Bid(engine, auction, 1, sara, 1_250_000_00, Start.AddMinutes(2), retryId);
        var retry = Bid(engine, auction, 2, sara, 1_250_000_00, Start.AddMinutes(2), retryId);

        Assert.True(original.Accepted);
        Assert.False(retry.Accepted);
        Assert.Equal(RejectionReason.DuplicateBidId, retry.Reason);
        Assert.Equal(1_250_000_00, engine.CurrentPrice);
    }

    [Fact]
    public void Bid_inside_the_quiet_period_slides_the_end_time()
    {
        var (engine, auction) = NewEngine(quiet: TimeSpan.FromMinutes(2));
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        // 19:59 — inside [19:58, 20:00].
        Bid(engine, auction, 0, ahmad, 1_200_000_00, End.AddMinutes(-1));

        Assert.Equal(End.AddMinutes(2), engine.EffectiveEndsAt);
        Assert.Equal(1, engine.ExtensionsUsed);

        // 20:01 — inside the new window [20:00, 20:02].
        Bid(engine, auction, 1, sara, 1_300_000_00, End.AddMinutes(1));

        Assert.Equal(End.AddMinutes(4), engine.EffectiveEndsAt);
        Assert.Equal(2, engine.ExtensionsUsed);
    }

    [Fact]
    public void A_bid_outside_the_quiet_period_does_not_extend()
    {
        var (engine, auction) = NewEngine(quiet: TimeSpan.FromMinutes(2));
        Bid(engine, auction, 0, Guid.NewGuid(), 1_200_000_00, Start.AddMinutes(5));

        Assert.Equal(End, engine.EffectiveEndsAt);
        Assert.Equal(0, engine.ExtensionsUsed);
    }

    [Fact]
    public void Extensions_stop_at_the_configured_maximum()
    {
        var (engine, auction) = NewEngine(quiet: TimeSpan.FromMinutes(2), maxExtensions: 3);
        var bidders = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        var amount = 1_200_000_00L;
        var at = End.AddMinutes(-1);

        for (var i = 0; i < 4; i++)
        {
            Bid(engine, auction, i, bidders[i], amount, at);
            amount += 100_000_00;
            at = engine.EffectiveEndsAt.AddMinutes(-1);
        }

        Assert.Equal(3, engine.ExtensionsUsed);
        Assert.Equal(End.AddMinutes(6), engine.EffectiveEndsAt);
    }

    [Fact]
    public void A_bid_after_the_effective_end_is_closed_out()
    {
        var (engine, auction) = NewEngine(quiet: TimeSpan.FromMinutes(2));
        Bid(engine, auction, 0, Guid.NewGuid(), 1_200_000_00, Start.AddMinutes(5));

        var late = Bid(engine, auction, 1, Guid.NewGuid(), 1_300_000_00, End.AddMinutes(1));
        Assert.False(late.Accepted);
        Assert.Equal(RejectionReason.AuctionClosed, late.Reason);
    }

    [Fact]
    public void Extension_lets_a_bid_land_that_the_original_end_would_have_refused()
    {
        // The same instant is closed without extension and open with it. This
        // is why the catcher must gate on the hard ceiling rather than ends_at.
        var at = End.AddSeconds(30);

        var (noQuiet, a1) = NewEngine();
        Bid(noQuiet, a1, 0, Guid.NewGuid(), 1_200_000_00, Start.AddMinutes(5));
        Assert.False(Bid(noQuiet, a1, 1, Guid.NewGuid(), 1_300_000_00, at).Accepted);

        var (withQuiet, a2) = NewEngine(quiet: TimeSpan.FromMinutes(2));
        Bid(withQuiet, a2, 0, Guid.NewGuid(), 1_200_000_00, End.AddMinutes(-1));
        Assert.True(Bid(withQuiet, a2, 1, Guid.NewGuid(), 1_300_000_00, at).Accepted);
    }
}
