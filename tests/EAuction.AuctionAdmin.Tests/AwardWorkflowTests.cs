using EAuction.AuctionAdmin.Domain;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

public class AwardWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Compliance = TimeSpan.FromDays(5);

    private static Auction AtEligibilityReview(out Auction a)
    {
        a = Build.ReadyAuction(Now);
        a.SubmitForReview(Now);
        a.Approve(Now);
        a.MarkScheduled();
        a.MarkLive();
        a.MarkClosing();
        a.MarkPendingEligibilityReview();
        return a;
    }

    [Fact]
    public void The_committee_confirms_the_award_the_system_only_offers_a_candidate()
    {
        AtEligibilityReview(out var auction);
        var bidder = Guid.NewGuid();

        auction.OfferCandidate(bidder, 1_800_000_00);
        Assert.Equal(AuctionStatus.PendingAward, auction.Status);
        Assert.Equal(bidder, auction.PendingCandidateBidderId);

        var award = auction.ConfirmAward(Build.Committee, Now, Compliance);

        Assert.Equal(AuctionStatus.Awarded, auction.Status);
        Assert.Equal(bidder, award.BidderId);
        Assert.Equal(0, award.CascadeStep);
        Assert.Equal(Now + Compliance, award.ComplianceDeadline);
        Assert.Single(auction.Events.OfType<AwardConfirmed>());
    }

    [Fact]
    public void An_award_cannot_be_confirmed_without_a_candidate()
    {
        AtEligibilityReview(out var auction);
        Assert.Throws<InvalidAuctionTransitionException>(
            () => auction.ConfirmAward(Build.Committee, Now, Compliance));
    }

    [Fact]
    public void The_winner_is_notified_only_after_the_signed_letter_comes_back()
    {
        // Slide 6's order: print for signature, re-upload signed, then notify.
        // Telling the winner before the letter is executed would be announcing
        // a decision that is not yet binding.
        AtEligibilityReview(out var auction);
        auction.OfferCandidate(Guid.NewGuid(), 1_800_000_00);
        auction.ConfirmAward(Build.Committee, Now, Compliance);

        Assert.Throws<InvalidOperationException>(() => auction.NotifyWinner(Now));

        auction.GenerateAwardLetter(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => auction.NotifyWinner(Now));

        auction.UploadSignedAwardLetter(Guid.NewGuid());
        auction.NotifyWinner(Now);

        Assert.NotNull(auction.CurrentAward!.WinnerNotifiedAt);
    }

    [Fact]
    public void A_signed_letter_cannot_be_uploaded_before_a_letter_exists()
    {
        AtEligibilityReview(out var auction);
        auction.OfferCandidate(Guid.NewGuid(), 1_800_000_00);
        auction.ConfirmAward(Build.Committee, Now, Compliance);

        Assert.Throws<InvalidOperationException>(
            () => auction.UploadSignedAwardLetter(Guid.NewGuid()));
    }

    [Fact]
    public void Disqualifying_the_winner_cascades_to_a_fresh_award_not_an_edited_one()
    {
        // Each cascade step is a full new ترسية: new committee confirmation,
        // new letters, new signature. The previous award stays on record.
        var auction = Build.AwaitingSettlement(Now, out var first);

        auction.DisqualifyWinner("لم يسدد خلال المدة", forfeitDeposit: true, Now.AddDays(6));
        Assert.Equal(AuctionStatus.WinnerDisqualified, auction.Status);
        Assert.Null(auction.CurrentAward);

        var second = Guid.NewGuid();
        auction.OfferCandidate(second, 1_700_000_00);
        var secondAward = auction.ConfirmAward(Build.Committee, Now.AddDays(7), Compliance);

        Assert.Equal(2, auction.Awards.Count);
        Assert.Equal(1, secondAward.CascadeStep);
        Assert.Equal(second, secondAward.BidderId);

        // The first award is preserved with its reason, not overwritten.
        var firstAward = auction.Awards.Single(a => a.CascadeStep == 0);
        Assert.Equal(first, firstAward.BidderId);
        Assert.Equal("لم يسدد خلال المدة", firstAward.DisqualificationReason);
        Assert.True(firstAward.DepositForfeited);
    }

    [Fact]
    public void Disqualification_requires_a_reason()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        Assert.Throws<AuctionValidationException>(
            () => auction.DisqualifyWinner("", true, Now));
    }

    [Fact]
    public void Deposits_are_not_releasable_until_the_award_is_final()
    {
        // The trap in §8.3: releasing losers' deposits when bidding closes
        // leaves nothing to cascade to.
        AtEligibilityReview(out var auction);
        auction.OfferCandidate(Guid.NewGuid(), 1_800_000_00);
        auction.ConfirmAward(Build.Committee, Now, Compliance);

        Assert.Empty(auction.Events.OfType<DepositsReleasable>());

        auction.GenerateAwardLetter(Guid.NewGuid());
        auction.UploadSignedAwardLetter(Guid.NewGuid());
        auction.NotifyWinner(Now);

        Assert.Empty(auction.Events.OfType<DepositsReleasable>());

        auction.Settle(Now.AddDays(3));

        var release = Assert.Single(auction.Events.OfType<DepositsReleasable>());
        Assert.Equal(AuctionStatus.Settled, auction.Status);
        Assert.Empty(release.ForfeitForBidders);
        Assert.NotNull(release.AppliedToPurchaseForBidder);
    }

    [Fact]
    public void Settlement_forfeits_the_deposits_of_everyone_disqualified_along_the_way()
    {
        var auction = Build.AwaitingSettlement(Now, out var first);

        auction.DisqualifyWinner("مستندات غير مطابقة", forfeitDeposit: true, Now.AddDays(6));

        var second = Guid.NewGuid();
        auction.OfferCandidate(second, 1_700_000_00);
        auction.ConfirmAward(Build.Committee, Now.AddDays(7), Compliance);
        auction.GenerateAwardLetter(Guid.NewGuid());
        auction.UploadSignedAwardLetter(Guid.NewGuid());
        auction.NotifyWinner(Now.AddDays(8));
        auction.Settle(Now.AddDays(9));

        var release = auction.Events.OfType<DepositsReleasable>().Last();

        Assert.Equal(new[] { first }, release.ForfeitForBidders);
        Assert.Equal(second, release.AppliedToPurchaseForBidder);
    }

    [Fact]
    public void An_auction_nobody_bid_the_reserve_on_is_marked_unsold()
    {
        AtEligibilityReview(out var auction);
        auction.MarkUnsold();

        Assert.Equal(AuctionStatus.Unsold, auction.Status);
        Assert.Single(auction.Events.OfType<AuctionUnsold>());

        // Deposits are resolved here too — the auction is final, just not sold.
        Assert.Single(auction.Events.OfType<DepositsReleasable>());
    }

    [Fact]
    public void The_unsold_event_does_not_reveal_the_reserve_it_failed_to_reach()
    {
        var properties = typeof(AuctionUnsold).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(properties, n => n.Contains("Reserve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Settling_before_the_winner_is_notified_is_refused()
    {
        AtEligibilityReview(out var auction);
        auction.OfferCandidate(Guid.NewGuid(), 1_800_000_00);
        auction.ConfirmAward(Build.Committee, Now, Compliance);

        Assert.Throws<InvalidOperationException>(() => auction.Settle(Now));
    }

    [Fact]
    public void Lifecycle_transitions_cannot_be_skipped()
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        Assert.Throws<InvalidAuctionTransitionException>(() => auction.MarkLive());

        auction.MarkScheduled();
        auction.MarkLive();

        Assert.Throws<InvalidAuctionTransitionException>(() => auction.MarkLive());
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.MarkPendingEligibilityReview());
    }
}
