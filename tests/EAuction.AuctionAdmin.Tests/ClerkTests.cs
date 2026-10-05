using EAuction.AuctionAdmin.Domain;
using EAuction.Core;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// The clerk on the floor of a hall auction (§29).
///
/// A clerk holds more power than anyone else touching a live auction: they enter
/// bids on other people's behalf and they decide when bidding stops. So almost
/// every rule here is about narrowing that — to one auction, to one person, and to
/// the window in which an auction is actually running.
/// </summary>
public class ClerkTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Clerk = Guid.NewGuid();

    private static Auction Hall(DateTimeOffset now)
    {
        var auction = Build.ReadyAuction(now);
        auction.UpdateDetails(
            nameAr: "قاعة", nameEn: "Hall",
            channel: BidChannel.Onsite,
            bidderVisibility: BidderVisibility.Masked,
            startsAt: now.AddDays(7), endsAt: now.AddDays(8),
            openingPriceMinorUnits: 1_000_000_00, reservePriceMinorUnits: 1_500_000_00,
            minIncrementMinorUnits: 50_000_00, depositMinorUnits: 100_000_00,
            brokerageFeePercent: 2.5m, bookletPriceMinorUnits: 1_000_00,
            quietPeriodSeconds: 120, maxExtensions: 3);
        return auction;
    }

    private static Auction LiveHall(DateTimeOffset now)
    {
        var auction = Hall(now);
        auction.AssignClerk(Clerk);
        auction.SubmitForReview(now);
        auction.Approve(now);
        auction.MarkScheduled();
        auction.MarkLive();
        return auction;
    }

    // --- putting somebody on the floor --------------------------------------

    [Fact]
    public void An_online_auction_has_no_clerk()
    {
        var auction = Build.ReadyAuction(Now);

        Assert.Throws<AuctionValidationException>(() => auction.AssignClerk(Clerk));
    }

    [Fact]
    public void Assigning_a_clerk_publishes_who_may_sign_and_under_which_epoch()
    {
        var auction = Hall(Now);
        auction.ClearEvents();

        auction.AssignClerk(Clerk);

        var published = Assert.Single(auction.Events.OfType<AuctionClerkAssigned>());
        Assert.Equal(Clerk, published.ClerkUserId);
        Assert.True(published.Assigned);

        // The epoch and never a key: the catcher derives one from a master it
        // already holds, so the topic says who is on the floor, not how to sign.
        Assert.DoesNotContain("ecret", System.Text.Json.JsonSerializer.Serialize(published));
    }

    [Fact]
    public void Replacing_the_clerk_rotates_the_key()
    {
        // Otherwise the outgoing clerk's terminal keeps entering bids after they
        // have been taken off the floor.
        var auction = Hall(Now);
        auction.AssignClerk(Clerk);
        var first = auction.ClerkKeyEpoch;

        auction.AssignClerk(Guid.NewGuid());

        Assert.True(auction.ClerkKeyEpoch > first);
    }

    [Fact]
    public void Re_assigning_the_same_clerk_does_not_rotate_the_key()
    {
        // An idempotent retry should not invalidate the key of the terminal that is
        // currently running the room.
        var auction = Hall(Now);
        auction.AssignClerk(Clerk);
        var epoch = auction.ClerkKeyEpoch;

        auction.AssignClerk(Clerk);

        Assert.Equal(epoch, auction.ClerkKeyEpoch);
    }

    [Fact]
    public void Taking_the_clerk_off_the_floor_revokes_their_key()
    {
        var auction = Hall(Now);
        auction.AssignClerk(Clerk);
        auction.ClearEvents();

        auction.UnassignClerk();

        var published = Assert.Single(auction.Events.OfType<AuctionClerkAssigned>());
        Assert.False(published.Assigned);
        Assert.Null(auction.ClerkUserId);
    }

    [Fact]
    public void A_clerk_can_be_assigned_after_approval()
    {
        // Deliberately unlike the dates and the deposit. A clerk falls ill and a
        // shift changes; an auction cannot be re-approved to deal with that.
        var auction = Hall(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        auction.AssignClerk(Clerk);

        Assert.Equal(Clerk, auction.ClerkUserId);
    }

    // --- the two verbs ------------------------------------------------------

    [Fact]
    public void The_assigned_clerk_extends_and_closes()
    {
        var auction = LiveHall(Now);
        auction.ClearEvents();

        auction.ExtendByClerk(Clerk, 300);
        auction.CloseByClerk(Clerk);

        var extended = Assert.Single(auction.Events.OfType<AuctionExtendedByClerk>());
        Assert.Equal(300, extended.ExtendBySeconds);

        var closed = Assert.Single(auction.Events.OfType<AuctionClosedByClerk>());
        Assert.Equal(Clerk, closed.ClerkUserId);
    }

    [Fact]
    public void A_clerk_from_another_hall_cannot_bring_this_hammer_down()
    {
        // They hold the operator role and a valid token. Neither entitles them to
        // end an auction they are not running.
        var auction = LiveHall(Now);

        Assert.Throws<AuctionValidationException>(() => auction.CloseByClerk(Guid.NewGuid()));
        Assert.Throws<AuctionValidationException>(() => auction.ExtendByClerk(Guid.NewGuid(), 60));
    }

    [Fact]
    public void An_auction_with_nobody_on_the_floor_cannot_be_closed_by_a_clerk()
    {
        var auction = Hall(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);
        auction.MarkScheduled();
        auction.MarkLive();

        Assert.Throws<AuctionValidationException>(() => auction.CloseByClerk(Clerk));
    }

    [Fact]
    public void An_online_auction_cannot_be_closed_by_hand()
    {
        // Its clock is the authority, and a manual close would be a way around the
        // quiet-period extension bidders were promised.
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);
        auction.MarkScheduled();
        auction.MarkLive();

        Assert.Throws<AuctionValidationException>(() => auction.CloseByClerk(Clerk));
    }

    [Fact]
    public void An_auction_that_has_not_opened_cannot_be_closed()
    {
        var auction = Hall(Now);
        auction.AssignClerk(Clerk);

        Assert.Throws<InvalidAuctionTransitionException>(() => auction.CloseByClerk(Clerk));
    }

    [Fact]
    public void An_extension_must_be_a_positive_length_of_time()
    {
        var auction = LiveHall(Now);

        Assert.Throws<AuctionValidationException>(() => auction.ExtendByClerk(Clerk, 0));
        Assert.Throws<AuctionValidationException>(() => auction.ExtendByClerk(Clerk, -60));
    }
}
