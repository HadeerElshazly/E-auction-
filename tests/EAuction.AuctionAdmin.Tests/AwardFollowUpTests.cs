using EAuction.AuctionAdmin.Domain;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// المرحلة الأولى، الخاصية 11: the award's payment and the title transfer are
/// recorded by hand against references; nothing closes without one, and an award
/// past its deadline with money owed is flagged for review rather than acted on.
/// </summary>
public class AwardFollowUpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Receipts_reduce_what_remains_and_settlement_waits_for_the_last_riyal()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        var award = auction.CurrentAward!;

        auction.RecordAwardPayment(1_000_000_00, Now, "SADAD-1", null, Build.Committee, Now);
        Assert.Equal(1_000_000_00, award.PaidMinorUnits);
        Assert.Equal(800_000_00, award.RemainingMinorUnits);

        var refused = Assert.Throws<AuctionValidationException>(() => auction.Settle(Now.AddDays(1)));
        Assert.Contains(refused.Problems, p => p.Contains("المتبقي"));

        auction.RecordAwardPayment(800_000_00, Now, "SADAD-2", null, Build.Committee, Now);
        auction.Settle(Now.AddDays(1));

        Assert.Equal(AuctionStatus.Settled, auction.Status);
    }

    [Fact]
    public void A_receipt_needs_a_reference_and_cannot_overpay()
    {
        var auction = Build.AwaitingSettlement(Now, out _);

        Assert.Throws<AuctionValidationException>(
            () => auction.RecordAwardPayment(100_00, Now, " ", null, Build.Committee, Now));
        Assert.Throws<AuctionValidationException>(
            () => auction.RecordAwardPayment(2_000_000_00, Now, "R-1", null, Build.Committee, Now));
        Assert.Throws<AuctionValidationException>(
            () => auction.RecordAwardPayment(0, Now, "R-1", null, Build.Committee, Now));
    }

    [Fact]
    public void The_deposit_is_credited_at_its_own_amount_and_only_once()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        var award = auction.CurrentAward!;

        auction.CreditDepositToAward("SIM-DEP-1", Build.Committee, Now);

        var credit = Assert.Single(award.Receipts);
        Assert.Equal(AwardReceiptKind.DepositCredit, credit.Kind);
        Assert.Equal(auction.DepositMinorUnits, credit.AmountMinorUnits);
        Assert.Throws<AuctionValidationException>(
            () => auction.CreditDepositToAward("SIM-DEP-1", Build.Committee, Now));
    }

    [Fact]
    public void An_unpaid_award_past_its_deadline_is_overdue_and_a_paid_one_is_not()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        var award = auction.CurrentAward!;

        Assert.False(award.IsOverdue(award.ComplianceDeadline.AddMinutes(-1)));
        Assert.True(award.IsOverdue(award.ComplianceDeadline.AddMinutes(1)));

        Build.PayInFull(auction, Now);
        Assert.False(award.IsOverdue(award.ComplianceDeadline.AddDays(30)));
    }

    [Fact]
    public void The_transfer_closes_only_on_a_paid_award_and_with_a_reference()
    {
        var auction = Build.AwaitingSettlement(Now, out _);

        auction.UpdateTransfer(TransferStatus.InProgress, null, null, Now);

        // Not paid yet.
        Assert.Throws<AuctionValidationException>(
            () => auction.UpdateTransfer(TransferStatus.Completed, "DEED-9", null, Now));

        Build.PayInFull(auction, Now);
        auction.Settle(Now.AddDays(1));

        // Settled, but no deed number and no proof.
        Assert.Throws<AuctionValidationException>(
            () => auction.UpdateTransfer(TransferStatus.Completed, " ", null, Now.AddDays(2)));

        auction.UpdateTransfer(TransferStatus.Completed, "310105099999", null, Now.AddDays(2));

        var award = auction.FollowUpAward!;
        Assert.Equal(TransferStatus.Completed, award.TransferStatus);
        Assert.Equal("310105099999", award.TransferReference);
        Assert.Equal(Now.AddDays(2), award.TransferCompletedAt);
    }

    [Fact]
    public void A_withdrawn_award_is_not_the_one_followed_up()
    {
        var auction = Build.AwaitingSettlement(Now, out _);
        auction.DisqualifyWinner("لم يسدد", forfeitDeposit: true, Now.AddDays(6));

        Assert.Null(auction.FollowUpAward);
        Assert.Throws<InvalidAuctionTransitionException>(
            () => auction.RecordAwardPayment(100_00, Now, "R", null, Build.Committee, Now));
    }

    [Fact]
    public void Every_change_to_the_award_tells_the_winner_where_it_stands()
    {
        var auction = Build.AwaitingSettlement(Now, out var winner);
        auction.ClearEvents();

        auction.RecordAwardPayment(500_000_00, Now, "SADAD-1", null, Build.Committee, Now);

        var snapshot = Assert.IsType<AwardFollowUpUpdated>(Assert.Single(auction.Events));
        Assert.Equal(winner, snapshot.WinnerBidderId);
        Assert.Equal(500_000_00, snapshot.PaidMinorUnits);
        Assert.Equal(auction.CurrentAward!.RemainingMinorUnits, snapshot.RemainingMinorUnits);
        Assert.NotNull(snapshot.SignedLetterDocumentId);
        Assert.NotNull(snapshot.WinnerNotifiedAt);
        Assert.Equal("NotStarted", snapshot.TransferStatus);
    }
}
