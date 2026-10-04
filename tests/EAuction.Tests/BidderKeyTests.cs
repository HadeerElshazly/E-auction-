using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

public class BidderKeyTests
{
    private static readonly byte[] Master = BidderKeys.NewMasterKey();

    [Fact]
    public void The_same_inputs_always_derive_the_same_secret()
    {
        // The whole point: two services compute the key independently and must
        // agree, without the key ever travelling between them.
        var auction = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        Assert.Equal(
            BidderKeys.Derive(Master, auction, bidder, 0),
            BidderKeys.Derive(Master, auction, bidder, 0));
    }

    [Fact]
    public void A_bidder_gets_a_different_key_in_every_auction()
    {
        var bidder = Guid.NewGuid();
        var first = BidderKeys.Derive(Master, Guid.NewGuid(), bidder, 0);
        var second = BidderKeys.Derive(Master, Guid.NewGuid(), bidder, 0);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Two_bidders_in_one_auction_get_different_keys()
    {
        var auction = Guid.NewGuid();
        Assert.NotEqual(
            BidderKeys.Derive(Master, auction, Guid.NewGuid(), 0),
            BidderKeys.Derive(Master, auction, Guid.NewGuid(), 0));
    }

    [Fact]
    public void Bumping_the_epoch_rotates_one_bidder_without_touching_anyone_else()
    {
        var auction = Guid.NewGuid();
        var sara = Guid.NewGuid();
        var ahmad = Guid.NewGuid();

        var ahmadBefore = BidderKeys.Derive(Master, auction, ahmad, 0);

        Assert.NotEqual(
            BidderKeys.Derive(Master, auction, sara, 0),
            BidderKeys.Derive(Master, auction, sara, 1));

        Assert.Equal(ahmadBefore, BidderKeys.Derive(Master, auction, ahmad, 0));
    }

    [Fact]
    public void A_different_master_key_derives_a_different_secret()
    {
        var auction = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        Assert.NotEqual(
            BidderKeys.Derive(Master, auction, bidder, 0),
            BidderKeys.Derive(BidderKeys.NewMasterKey(), auction, bidder, 0));
    }

    [Fact]
    public void A_short_master_key_is_refused()
    {
        // A weak master would weaken every derived secret at once.
        Assert.Throws<ArgumentException>(() =>
            BidderKeys.Derive(new byte[16], Guid.NewGuid(), Guid.NewGuid(), 0));
    }

    [Fact]
    public void Derived_secrets_are_full_length()
    {
        Assert.Equal(32, BidderKeys.Derive(Master, Guid.NewGuid(), Guid.NewGuid(), 0).Length);
        Assert.Equal(32, BidderKeys.NewMasterKey().Length);
    }
}
