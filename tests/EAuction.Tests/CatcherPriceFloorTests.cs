using EAuction.BidCatcher;
using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The catcher's price screen before the first verdict.
///
/// Found by the end-to-end smoke test, not by this suite: the screen only applied a
/// floor when auctions.current-winner had already given it a price, and that topic
/// is empty until the processor judges the first bid. So for the opening moments of
/// every auction there was no floor at all, and a bid of one halala on a
/// million-riyal auction was accepted into the append-only ledger that is the legal
/// record of the sale.
/// </summary>
public class CatcherPriceFloorTests
{
    private static readonly byte[] Master = Enumerable.Range(0, 32).Select(i => (byte)(i + 7)).ToArray();

    private static (CatcherState State, AuctionDefinition Auction, Guid Bidder, byte[] Secret) Ready()
    {
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-1), now.AddMinutes(10));
        var bidder = Guid.NewGuid();

        var state = new CatcherState(Master) { MaxBidsPerSecondPerBidder = 1_000_000 };
        state.UpsertAuction(auction);
        state.GrantEligibility(auction.AuctionId, bidder, keyEpoch: 1);

        var secret = BidderKeys.Derive(Master, auction.AuctionId, bidder, 1);
        return (state, auction, bidder, secret);
    }

    [Theory]
    [InlineData(1L)]                 // one halala
    [InlineData(999_999_99L)]        // one halala under the opening price
    public void A_bid_under_the_opening_price_is_refused_before_any_verdict(long amount)
    {
        var (state, auction, bidder, secret) = Ready();

        var frame = TestAuction.Frame(
            auction.AuctionId, bidder, amount, DateTimeOffset.UtcNow, secret: secret);

        Assert.Equal(RejectionReason.BelowOpeningPrice,
            state.Screen(frame, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_opening_price_itself_is_accepted()
    {
        var (state, auction, bidder, secret) = Ready();

        var frame = TestAuction.Frame(
            auction.AuctionId, bidder, auction.OpeningPriceMinorUnits,
            DateTimeOffset.UtcNow, secret: secret);

        Assert.Equal(RejectionReason.None, state.Screen(frame, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Once_a_price_arrives_the_increment_rule_takes_over()
    {
        var (state, auction, bidder, secret) = Ready();
        state.UpdateCurrentPrice(auction.AuctionId, auction.OpeningPriceMinorUnits);

        // Above the opening price but not by a full increment.
        var frame = TestAuction.Frame(
            auction.AuctionId, bidder, auction.OpeningPriceMinorUnits + 1,
            DateTimeOffset.UtcNow, secret: secret);

        Assert.Equal(RejectionReason.BelowMinimumIncrement,
            state.Screen(frame, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_floor_is_checked_after_eligibility_so_it_cannot_probe_the_price()
    {
        // An ineligible caller must learn nothing about the auction's economics. If the
        // price check ran first, the difference between BelowOpeningPrice and NotEligible
        // would let anyone with a token binary-search the opening price of an auction
        // they are not in.
        var (state, auction, _, _) = Ready();
        var stranger = Guid.NewGuid();

        var frame = TestAuction.Frame(
            auction.AuctionId, stranger, 1, DateTimeOffset.UtcNow, secret: TestAuction.Secret);

        Assert.Equal(RejectionReason.NotEligible, state.Screen(frame, DateTimeOffset.UtcNow));
    }
}
