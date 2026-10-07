using EAuction.AuctionAdmin.Domain;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// الخاصية 05 «توثيق الإلغاء المصرح به» and الخاصية 08 «رفضها مع السبب».
/// </summary>
public class CancellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static Auction Approved()
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);
        auction.ClearEvents();
        return auction;
    }

    [Fact]
    public void An_approved_auction_is_cancelled_with_its_reason_and_releases_every_deposit()
    {
        var auction = Approved();

        auction.Cancel("تعديل المخطط من الأمانة", Build.Admin, Now);

        Assert.Equal(AuctionStatus.Cancelled, auction.Status);
        Assert.Equal("تعديل المخطط من الأمانة", auction.CancellationReason);
        var cancelled = Assert.Single(auction.Events.OfType<AuctionCancelled>());
        Assert.Equal(Build.Admin, cancelled.CancelledByUserId);
        var release = Assert.Single(auction.Events.OfType<DepositsReleasable>());
        Assert.Empty(release.ForfeitForBidders);
        Assert.Null(release.AppliedToPurchaseForBidder);
    }

    [Fact]
    public void A_cancellation_needs_a_reason()
    {
        var auction = Approved();
        Assert.Throws<AuctionValidationException>(() => auction.Cancel("  ", Build.Admin, Now));
        Assert.Equal(AuctionStatus.Approved, auction.Status);
    }

    [Fact]
    public void An_auction_about_to_open_or_already_live_cannot_be_cancelled()
    {
        var auction = Approved();
        var starts = auction.StartsAt!.Value;

        // Inside the cut-off: the processor may already be opening it.
        Assert.Throws<AuctionValidationException>(
            () => auction.Cancel("سبب", Build.Admin, starts - TimeSpan.FromMinutes(1)));

        auction.MarkScheduled();
        auction.MarkLive();
        Assert.Throws<InvalidAuctionTransitionException>(
            () => auction.Cancel("سبب", Build.Admin, Now));
    }

    [Fact]
    public void A_draft_is_not_cancelled_it_is_simply_not_submitted()
    {
        var auction = Build.ReadyAuction(Now);
        Assert.Throws<InvalidAuctionTransitionException>(
            () => auction.Cancel("سبب", Build.Admin, Now));
    }

    [Fact]
    public void Publication_confirmed_after_a_cancellation_does_not_resurrect_it()
    {
        var auction = Approved();
        auction.Cancel("سبب", Build.Admin, Now);

        auction.MarkScheduled();

        Assert.Equal(AuctionStatus.Cancelled, auction.Status);
    }

    [Fact]
    public void The_committee_refuses_a_preliminary_result_with_a_reason_and_nobody_is_awarded()
    {
        var auction = Approved();
        auction.MarkScheduled();
        auction.MarkLive();
        auction.MarkClosing();
        auction.MarkPendingEligibilityReview();
        auction.OfferCandidate(Guid.NewGuid(), 1_600_000_00);

        Assert.Throws<AuctionValidationException>(() => auction.RejectResult(" "));

        auction.RejectResult("شبهة تواطؤ بين المزايدين");

        Assert.Equal(AuctionStatus.Unsold, auction.Status);
        Assert.Equal("شبهة تواطؤ بين المزايدين", auction.ResultRejectionReason);
        Assert.Null(auction.CurrentAward);
        Assert.Single(auction.Events.OfType<AuctionUnsold>());
    }

    [Fact]
    public void After_a_disqualification_the_committee_may_end_it_unsold_instead_of_referring()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        auction.DisqualifyWinner("لم يسدد", forfeitDeposit: true, Now.AddDays(6));
        auction.OfferCandidate(Guid.NewGuid(), 1_700_000_00);

        auction.MarkUnsold();

        Assert.Equal(AuctionStatus.Unsold, auction.Status);
        Assert.Equal(1, auction.Awards.Count);
    }

    [Fact]
    public void There_is_nobody_to_refer_to_before_the_processor_names_one()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        auction.DisqualifyWinner("لم يسدد", forfeitDeposit: true, Now.AddDays(6));

        Assert.Throws<AuctionValidationException>(() => auction.ReferToNextBidder());
    }
}
