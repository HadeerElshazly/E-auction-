using EAuction.Participant.Domain;
using Xunit;

namespace EAuction.Participant.Tests;

public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = Build.Now;

    [Fact]
    public void Identity_cannot_be_self_asserted_without_a_national_id()
    {
        Assert.Throws<ParticipantValidationException>(
            () => Bidder.FromNafath("", "سارة", "Sara", Now));
    }

    [Fact]
    public void A_profile_needs_a_usable_phone_and_email()
    {
        var bidder = Bidder.FromNafath("1234567890", "سارة", "Sara", Now);

        Assert.Throws<ParticipantValidationException>(
            () => bidder.CompleteProfile("", "sara@example.com", Now));
        Assert.Throws<ParticipantValidationException>(
            () => bidder.CompleteProfile("+966500000000", "not-an-email", Now));

        bidder.CompleteProfile("+966500000000", "sara@example.com", Now);
        Assert.True(bidder.IsProfileComplete);
    }

    [Fact]
    public void The_subscription_steps_cannot_be_taken_out_of_order()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);
        var s = Subscription.Start(auctionId, bidder.Id);

        // Terms before the booklet, deposit before the terms.
        Assert.Throws<InvalidSubscriptionTransitionException>(() => s.AcceptTerms(Now));
        Assert.Throws<InvalidSubscriptionTransitionException>(
            () => s.ChooseDeposit(DepositMethod.Payment, terms, Now));

        s.PurchaseBooklet("ref", Now);
        Assert.Throws<InvalidSubscriptionTransitionException>(
            () => s.ChooseDeposit(DepositMethod.Payment, terms, Now));

        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.Payment, terms, Now);
        Assert.Equal(SubscriptionStatus.AwaitingDeposit, s.Status);
    }

    [Fact]
    public void Choosing_a_deposit_asks_the_payment_service_for_the_auction_s_amount()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        var s = Subscription.Start(auctionId, bidder.Id);
        s.PurchaseBooklet("ref", Now);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.Payment, terms, Now);

        var requested = Assert.Single(s.Events.OfType<DepositRequested>());
        Assert.Equal(100_000_00, requested.AmountMinorUnits);
        Assert.Equal("Payment", requested.Method);
    }

    [Fact]
    public void Settling_the_deposit_makes_the_bidder_eligible_and_tells_the_catcher()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var s = Build.EligibleByPayment(auctionId, bidder, Build.Terms(auctionId));

        Assert.Equal(SubscriptionStatus.Eligible, s.Status);

        var published = Assert.Single(s.Events.OfType<ParticipantEligibilityChanged>());
        Assert.True(published.Eligible);
        Assert.Equal(0, published.KeyEpoch);
        Assert.Equal($"{auctionId}:{bidder.Id}", published.AggregateId);
    }

    [Fact]
    public void An_unverified_identity_never_becomes_eligible()
    {
        // Everything else can be bought or clicked. This one cannot.
        var auctionId = Guid.NewGuid();
        var terms = Build.Terms(auctionId);
        var unverified = Bidder.FromNafath("1234567890", "سارة", "Sara", Now);

        var s = Subscription.Start(auctionId, unverified.Id);
        s.PurchaseBooklet("ref", Now);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.Payment, terms, Now);

        var ex = Assert.Throws<ParticipantValidationException>(
            () => s.ConfirmDepositPayment("deposit", unverified, Now));

        Assert.Contains(ex.Problems, p => p.Contains("profile is incomplete"));
        Assert.NotEqual(SubscriptionStatus.Eligible, s.Status);
        Assert.Empty(s.Events.OfType<ParticipantEligibilityChanged>());
    }

    [Fact]
    public void A_guarantee_expiring_before_the_auction_ends_is_refused()
    {
        // It would be worthless at exactly the moment it is needed.
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        var s = Subscription.Start(auctionId, bidder.Id);
        s.PurchaseBooklet("ref", Now);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.BankGuarantee, terms, Now);

        Assert.Throws<ParticipantValidationException>(
            () => s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddHours(-1), terms));

        s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddDays(30), terms);
        Assert.NotNull(s.GuaranteeDocumentId);
    }

    [Fact]
    public void Uploading_a_guarantee_is_not_settling_it()
    {
        // A guarantee is worth nothing until someone has checked it is real.
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        var s = Subscription.Start(auctionId, bidder.Id);
        s.PurchaseBooklet("ref", Now);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.BankGuarantee, terms, Now);
        s.SubmitBankGuarantee(Guid.NewGuid(), terms.EndsAt.AddDays(30), terms);

        Assert.Equal(SubscriptionStatus.AwaitingDeposit, s.Status);
        Assert.Empty(s.Events.OfType<ParticipantEligibilityChanged>());

        s.VerifyBankGuarantee(Guid.NewGuid(), bidder, Now);

        Assert.Equal(SubscriptionStatus.Eligible, s.Status);
        Assert.NotNull(s.GuaranteeVerifiedByUserId);
        Assert.True(Assert.Single(s.Events.OfType<ParticipantEligibilityChanged>()).Eligible);
    }

    [Fact]
    public void The_two_deposit_methods_do_not_cross_over()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        var s = Subscription.Start(auctionId, bidder.Id);
        s.PurchaseBooklet("ref", Now);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.BankGuarantee, terms, Now);

        Assert.Throws<ParticipantValidationException>(
            () => s.ConfirmDepositPayment("deposit", bidder, Now));
    }

    [Fact]
    public void Revoking_tells_the_catcher_to_stop_accepting_the_bidder()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var s = Build.EligibleByPayment(auctionId, bidder, Build.Terms(auctionId));
        s.ClearEvents();

        s.Revoke("شيك مرتجع", Now.AddDays(1));

        Assert.Equal(SubscriptionStatus.Revoked, s.Status);
        var published = Assert.Single(s.Events.OfType<ParticipantEligibilityChanged>());
        Assert.False(published.Eligible);
    }

    [Fact]
    public void Rotating_the_key_keeps_the_bidder_eligible_under_a_new_epoch()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var s = Build.EligibleByPayment(auctionId, bidder, Build.Terms(auctionId));
        s.ClearEvents();

        s.RotateKey();

        Assert.Equal(SubscriptionStatus.Eligible, s.Status);
        Assert.Equal(1, s.KeyEpoch);
        var published = Assert.Single(s.Events.OfType<ParticipantEligibilityChanged>());
        Assert.True(published.Eligible);
        Assert.Equal(1, published.KeyEpoch);
    }

    [Fact]
    public void A_subscription_cannot_start_after_the_auction_has_ended()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        var s = Subscription.Start(auctionId, bidder.Id);
        s.PurchaseBooklet("ref", Now);
        s.AcceptTerms(Now);

        Assert.Throws<ParticipantValidationException>(
            () => s.ChooseDeposit(DepositMethod.Payment, terms, terms.EndsAt.AddMinutes(1)));
    }

    [Fact]
    public void Deposit_resolution_is_applied_once_however_often_it_arrives()
    {
        // auctions.deposits is at-least-once like everything else.
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var s = Build.EligibleByPayment(auctionId, bidder, Build.Terms(auctionId));

        s.ResolveDeposit(forfeited: true, Now.AddDays(10));
        var first = s.DepositResolvedAt;

        s.ResolveDeposit(forfeited: false, Now.AddDays(11));

        Assert.Equal(first, s.DepositResolvedAt);
        Assert.True(s.DepositForfeited);
    }

    [Fact]
    public void The_eligibility_event_carries_an_epoch_and_never_a_secret()
    {
        var properties = typeof(ParticipantEligibilityChanged)
            .GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains(nameof(ParticipantEligibilityChanged.KeyEpoch), properties);
        Assert.DoesNotContain(properties,
            n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
              || n.Contains("Key", StringComparison.OrdinalIgnoreCase) && n != "KeyEpoch");
    }
}
