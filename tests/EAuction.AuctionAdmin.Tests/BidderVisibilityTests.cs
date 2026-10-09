using EAuction.AuctionAdmin.Domain;
using EAuction.Core;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// Whether an auction names its bidders or masks them (D-22), as the administrator
/// decides it.
///
/// Two promises are made to a bidder who is about to put down a deposit: that the
/// default is to mask them, and that the answer cannot change once they have relied
/// on it. The second is the one that needs a test — it is a rule about *when* a
/// field may be written, which nothing in the type system enforces.
/// </summary>
public class BidderVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_draft_masks_its_bidders()
    {
        // Before anybody has chosen anything. An auction created by a path that never
        // thought about this must not name people.
        var auction = Auction.CreateDraft(Build.Admin, "أ", "A");

        Assert.Equal(BidderVisibility.Masked, auction.BidderVisibility);
    }

    [Fact]
    public void An_administrator_can_choose_to_name_bidders_while_it_is_a_draft()
    {
        var auction = Build.ReadyAuction(Now, visibility: BidderVisibility.Named);

        Assert.Equal(BidderVisibility.Named, auction.BidderVisibility);
    }

    [Fact]
    public void It_cannot_be_changed_once_the_auction_is_approved()
    {
        // The promise that matters. A bidder paid a deposit having been told their
        // name would not be shown; turning that around afterwards is not an edit —
        // and not an amendment either (§6.5): the aggregate refuses it with the reason.
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        var refused = Assert.Throws<AuctionValidationException>(() => auction.UpdateDetails(
            nameAr: "أ", nameEn: "A",
            channel: BidChannel.Online,
            bidderVisibility: BidderVisibility.Named,
            startsAt: Now.AddDays(7), endsAt: Now.AddDays(8),
            openingPriceMinorUnits: 1_000_000_00, reservePriceMinorUnits: null,
            minIncrementMinorUnits: 50_000_00, depositMinorUnits: 100_000_00,
            brokerageFeePercent: 2.5m, bookletPriceMinorUnits: 1_000_00,
            quietPeriodSeconds: 120, maxExtensions: 3));

        Assert.Contains(refused.Problems, p => p.Contains("ظهور المزايدين"));
        Assert.Equal(BidderVisibility.Masked, auction.BidderVisibility);
        Assert.Equal(AmendmentStatus.None, auction.Amendment);
    }

    [Theory]
    [InlineData(BidderVisibility.Masked, "Masked")]
    [InlineData(BidderVisibility.Named, "Named")]
    public void The_choice_reaches_the_public_topic(BidderVisibility chosen, string expected)
    {
        // The read path builds the label from this and nothing else, so an event that
        // did not carry it would silently mask a named auction — or, far worse, the
        // reverse if the default ever changed.
        var auction = Build.ReadyAuction(Now, visibility: chosen);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        var approved = Assert.Single(auction.Events.OfType<AuctionApproved>());
        Assert.Equal(expected, approved.BidderVisibility);
    }

    [Fact]
    public void Naming_bidders_does_not_put_the_reserve_on_the_public_topic()
    {
        // Two separate decisions that both concern what the public may see. Nothing
        // about naming bidders relaxes D-23, and this says so out loud because the
        // two will be read together by whoever changes one of them next.
        var auction = Build.ReadyAuction(Now, visibility: BidderVisibility.Named);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        var approved = Assert.Single(auction.Events.OfType<AuctionApproved>());
        var json = System.Text.Json.JsonSerializer.Serialize(approved);

        Assert.DoesNotContain("eserve", json);
        Assert.DoesNotContain("1500000", json);
    }
}
