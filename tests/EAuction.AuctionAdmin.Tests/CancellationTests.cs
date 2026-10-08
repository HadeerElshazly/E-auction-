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
        // The municipality withdrew the sale: the booklet fees go back with the deposits.
        Assert.False(release.ForfeitAll);
        Assert.True(release.RefundBooklets);
        Assert.True(auction.CancellationRefunded);
    }

    [Fact]
    public void A_cancellation_may_keep_the_bidders_money()
    {
        var auction = Approved();
        auction.MarkScheduled();
        auction.MarkLive();
        auction.ClearEvents();

        auction.Cancel("تواطؤ بين المزايدين", Build.Admin, auction.StartsAt!.Value.AddMinutes(5), refund: false);

        Assert.False(auction.CancellationRefunded);
        Assert.False(Assert.Single(auction.Events.OfType<AuctionCancelled>()).Refund);
        var release = Assert.Single(auction.Events.OfType<DepositsReleasable>());
        Assert.True(release.ForfeitAll);
        Assert.False(release.RefundBooklets);
    }

    [Fact]
    public void A_cancellation_needs_a_reason()
    {
        var auction = Approved();
        Assert.Throws<AuctionValidationException>(() => auction.Cancel("  ", Build.Admin, Now));
        Assert.Equal(AuctionStatus.Approved, auction.Status);
    }

    [Fact]
    public void An_auction_about_to_open_cannot_be_cancelled()
    {
        var auction = Approved();
        var starts = auction.StartsAt!.Value;

        // Inside the cut-off: the processor may already be opening it.
        Assert.Throws<AuctionValidationException>(
            () => auction.Cancel("سبب", Build.Admin, starts - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_running_auction_is_cancelled_with_no_award_and_every_deposit_back()
    {
        var auction = Approved();
        auction.MarkScheduled();
        auction.MarkLive();
        auction.ClearEvents();

        auction.Cancel("خطأ في بيانات القطعة", Build.Admin, auction.StartsAt!.Value.AddMinutes(10));

        Assert.Equal(AuctionStatus.Cancelled, auction.Status);
        Assert.Single(auction.Events.OfType<AuctionCancelled>());
        Assert.Empty(Assert.Single(auction.Events.OfType<DepositsReleasable>()).ForfeitForBidders);
    }

    [Fact]
    public void A_running_auction_closed_early_keeps_its_result_for_the_committee()
    {
        var auction = Approved();
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.CloseEarly(Build.Admin, "سبب", Now));

        auction.MarkScheduled();
        auction.MarkLive();
        auction.ClearEvents();
        Assert.Throws<AuctionValidationException>(() => auction.CloseEarly(Build.Admin, " ", Now));

        auction.CloseEarly(Build.Admin, "اكتمال المنافسة", Now);

        var closed = Assert.Single(auction.Events.OfType<AuctionClosedByAdmin>());
        Assert.Equal("اكتمال المنافسة", closed.Reason);
        // No deposits move and the status waits for the processor's close.
        Assert.Empty(auction.Events.OfType<DepositsReleasable>());
        Assert.Equal(AuctionStatus.Live, auction.Status);
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
