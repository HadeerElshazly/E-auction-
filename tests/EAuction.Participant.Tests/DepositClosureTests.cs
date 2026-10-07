using EAuction.Participant.Domain;
using Xunit;

namespace EAuction.Participant.Tests;

/// <summary>
/// المرحلة الأولى، الخاصية 11: a deposit is refunded, released or forfeited outside
/// the platform and closed here by hand — never without a reference, and never
/// while the award that decides its fate is still open.
/// </summary>
public class DepositClosureTests
{
    private static readonly DateTimeOffset Now = Build.Now;
    private static readonly Guid Staff = Guid.NewGuid();

    private static Subscription Eligible(out Bidder bidder, out AuctionTerms terms)
    {
        var auctionId = Guid.NewGuid();
        bidder = Build.VerifiedBidder();
        terms = Build.Terms(auctionId);
        return Build.EligibleByPayment(auctionId, bidder, terms);
    }

    [Fact]
    public void A_deposit_cannot_be_closed_while_it_is_still_held()
    {
        var s = Eligible(out _, out _);

        Assert.Equal(DepositSettlement.Held, s.DepositSettlement);
        Assert.Throws<ParticipantValidationException>(() => s.CloseDeposit("REF-1", Staff, Now));
    }

    [Fact]
    public void A_losers_paid_deposit_is_closed_against_a_reference()
    {
        var s = Eligible(out _, out _);
        s.ResolveDeposit(forfeited: false, Now);

        Assert.Equal(DepositSettlement.ToRefund, s.DepositSettlement);
        Assert.Throws<ParticipantValidationException>(() => s.CloseDeposit("  ", Staff, Now));

        s.CloseDeposit("SIM-REF-77", Staff, Now);

        Assert.Equal(DepositSettlement.Closed, s.DepositSettlement);
        Assert.Equal("SIM-REF-77", s.DepositClosureReference);
        Assert.Throws<ParticipantValidationException>(() => s.CloseDeposit("again", Staff, Now));
    }

    [Fact]
    public void A_forfeited_deposit_still_needs_its_disposal_recorded()
    {
        var s = Eligible(out _, out _);
        s.ResolveDeposit(forfeited: true, Now);

        Assert.Equal(DepositSettlement.ToForfeit, s.DepositSettlement);
        s.CloseDeposit("قرار مصادرة 12/1448", Staff, Now);
        Assert.Equal(DepositSettlement.Closed, s.DepositSettlement);
    }

    [Fact]
    public void The_winners_deposit_goes_to_the_price_and_is_not_closed_as_a_refund()
    {
        var s = Eligible(out _, out _);
        s.ResolveDeposit(forfeited: false, Now, appliedToPurchase: true);

        Assert.Equal(DepositSettlement.AppliedToPurchase, s.DepositSettlement);
        Assert.Throws<ParticipantValidationException>(() => s.CloseDeposit("REF", Staff, Now));
    }

    [Fact]
    public void A_bank_guarantee_is_released_rather_than_refunded()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);
        var s = Subscription.Start(auctionId, bidder.Id);
        Build.PayForBooklet(s, terms);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.BankGuarantee, terms, Now);
        s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddDays(30), terms);
        s.VerifyBankGuarantee(Staff, bidder, terms, Now);

        s.ResolveDeposit(forfeited: false, Now);

        Assert.Equal(DepositSettlement.ToRelease, s.DepositSettlement);
    }
}
