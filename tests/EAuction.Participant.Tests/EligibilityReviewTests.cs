using EAuction.Core;
using EAuction.Participant.Domain;
using Xunit;

namespace EAuction.Participant.Tests;

/// <summary>
/// المرحلة الأولى، البند 03: the accepted terms version is kept, a free booklet
/// needs no payment, and eligibility reads as under review, accepted, or rejected
/// with a reason.
/// </summary>
public class EligibilityReviewTests
{
    private static readonly DateTimeOffset Now = Build.Now;

    [Fact]
    public void Accepting_the_terms_records_which_booklet_was_accepted()
    {
        var auctionId = Guid.NewGuid();
        var booklet = Guid.NewGuid();
        var terms = new AuctionTerms(
            auctionId, Now.AddDays(7), Now.AddDays(8), 100_000_00, 1_000_00,
            bookletDocumentId: booklet);
        var s = Subscription.Start(auctionId, Build.VerifiedBidder().Id);

        Build.PayForBooklet(s, terms);
        s.AcceptTerms(Now, terms.BookletDocumentId);

        Assert.Equal(Now, s.TermsAcceptedAt);
        Assert.Equal(booklet, s.AcceptedBookletDocumentId);
    }

    [Fact]
    public void A_free_booklet_is_obtained_without_the_gateway()
    {
        var auctionId = Guid.NewGuid();
        var terms = new AuctionTerms(auctionId, Now.AddDays(7), Now.AddDays(8), 100_000_00, 0);
        var s = Subscription.Start(auctionId, Build.VerifiedBidder().Id);

        s.RequestBooklet(terms, Now);

        Assert.Equal(SubscriptionStatus.BookletPurchased, s.Status);
        Assert.Equal(Now, s.BookletPurchasedAt);
        Assert.Equal(Subscription.FreeBookletRef, s.BookletPaymentRef);
        Assert.Empty(s.Events.OfType<BookletFeeRequested>());
    }

    [Fact]
    public void A_paid_booklet_still_waits_for_the_gateway()
    {
        var auctionId = Guid.NewGuid();
        var terms = Build.Terms(auctionId);
        var s = Subscription.Start(auctionId, Build.VerifiedBidder().Id);

        s.RequestBooklet(terms, Now);

        Assert.Equal(SubscriptionStatus.Draft, s.Status);
        Assert.Single(s.Events.OfType<BookletFeeRequested>());
    }

    [Fact]
    public void Eligibility_moves_from_incomplete_to_under_review_to_accepted()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);
        var s = Subscription.Start(auctionId, bidder.Id);

        Assert.Equal(Eligibility.Incomplete, s.Eligibility.State);

        Build.PayForBooklet(s, terms);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.BankGuarantee, terms, Now);
        Assert.Equal(Eligibility.Incomplete, s.Eligibility.State);

        s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddDays(30), terms);
        Assert.Equal(Eligibility.UnderReview, s.Eligibility.State);

        s.VerifyBankGuarantee(Guid.NewGuid(), bidder, terms, Now);
        Assert.Equal(Eligibility.Accepted, s.Eligibility.State);
    }

    [Fact]
    public void A_rejected_guarantee_carries_its_reason_and_can_be_replaced()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);
        var s = Subscription.Start(auctionId, bidder.Id);
        Build.PayForBooklet(s, terms);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.BankGuarantee, terms, Now);
        s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddDays(30), terms);

        Assert.Throws<ParticipantValidationException>(() => s.RejectBankGuarantee(" ", Now));

        s.RejectBankGuarantee("الضمان لا يغطي مبلغ التأمين", Now);

        Assert.Equal(Eligibility.Rejected, s.Eligibility.State);
        Assert.Equal("الضمان لا يغطي مبلغ التأمين", s.Eligibility.Reason);
        Assert.Null(s.GuaranteeDocumentId);
        Assert.Equal(SubscriptionStatus.AwaitingDeposit, s.Status);

        // A new guarantee clears the refusal and goes back under review.
        s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddDays(30), terms);
        Assert.Equal(Eligibility.UnderReview, s.Eligibility.State);
        Assert.Null(s.Eligibility.Reason);
    }

    [Fact]
    public void A_revoked_bidder_reads_as_rejected_with_the_revocation_reason()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);
        var s = Build.EligibleByPayment(auctionId, bidder, terms);

        s.Revoke("مخالفة شروط المزاد", bidder, terms, Now);

        Assert.Equal(Eligibility.Rejected, s.Eligibility.State);
        Assert.Equal("مخالفة شروط المزاد", s.Eligibility.Reason);
    }
}
