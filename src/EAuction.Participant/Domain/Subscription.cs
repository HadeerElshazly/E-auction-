using EAuction.Core;
namespace EAuction.Participant.Domain;

/// <summary>
/// One bidder's participation in one auction — the citizen lane from slide 6.
///
/// Its only outward job is to decide eligibility, because that is the single
/// fact the bid catcher consumes. Everything else here exists to make that
/// decision defensible: who paid for the booklet, when they accepted the
/// terms, how the deposit was settled, and who verified it.
/// </summary>
public sealed class Subscription
{
    private readonly List<DomainEvent> _events = new();

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AuctionId { get; private set; }
    public Guid BidderId { get; private set; }
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Draft;

    /// <summary>When the booklet fee was sent to the payment service, not when it was paid.</summary>
    public DateTimeOffset? BookletRequestedAt { get; private set; }
    public DateTimeOffset? BookletPurchasedAt { get; private set; }
    public string? BookletPaymentRef { get; private set; }
    public DateTimeOffset? TermsAcceptedAt { get; private set; }

    public DepositMethod? DepositMethod { get; private set; }

    /// <summary>When the deposit was sent to the payment service, not when it was paid.</summary>
    public DateTimeOffset? DepositRequestedAt { get; private set; }
    public DateTimeOffset? DepositPaidAt { get; private set; }
    public string? DepositPaymentRef { get; private set; }

    /// <summary>
    /// The last refusal the gateway sent back, so the bidder is told why rather
    /// than left looking at a button that did nothing.
    ///
    /// Overwritten rather than accumulated: what a bidder needs is the reason the
    /// payment they just tried failed, and a growing list of past declines on a
    /// citizen's record is data nobody asked this service to keep.
    /// </summary>
    public string? PaymentFailurePurpose { get; private set; }
    public string? PaymentFailureReason { get; private set; }
    public DateTimeOffset? PaymentFailedAt { get; private set; }

    public Guid? GuaranteeDocumentId { get; private set; }
    public DateTimeOffset? GuaranteeExpiresAt { get; private set; }
    public DateTimeOffset? GuaranteeVerifiedAt { get; private set; }
    public Guid? GuaranteeVerifiedByUserId { get; private set; }

    /// <summary>
    /// Bumped to rotate this bidder's signing key without touching the master
    /// key or anyone else's. The catcher re-derives from the new epoch.
    /// </summary>
    public int KeyEpoch { get; private set; }

    public DateTimeOffset? EligibleAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }

    /// <summary>Set when auction-admin resolves deposits after the award is final (§8.3).</summary>
    public DateTimeOffset? DepositResolvedAt { get; private set; }
    public bool DepositForfeited { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<DomainEvent> Events => _events;
    public void ClearEvents() => _events.Clear();

    private Subscription() { }

    public static Subscription Start(Guid auctionId, Guid bidderId) =>
        new() { AuctionId = auctionId, BidderId = bidderId };

    private void Require(SubscriptionStatus expected, string action)
    {
        if (Status != expected) throw new InvalidSubscriptionTransitionException(Status, action);
    }

    /// <summary>
    /// شراء كراسة الشروط — asks the payment service to charge the booklet fee.
    ///
    /// It does not buy the booklet. Until this split the caller passed in a payment
    /// reference of their own invention and the subscription believed it, which meant
    /// a bidder could reach the terms, the deposit and eventually the bid floor
    /// without a riyal having moved. The reference now comes back from the gateway on
    /// <c>payments.settlements</c>, and <see cref="ConfirmBookletPayment"/> is the
    /// only thing that advances the status.
    /// </summary>
    public void RequestBooklet(AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.Draft, "request a booklet for");

        BookletRequestedAt = now;
        ClearPaymentFailure();

        _events.Add(new BookletFeeRequested
        {
            AuctionId = AuctionId,
            BidderId = BidderId,
            AmountMinorUnits = terms.BookletPriceMinorUnits
        });
    }

    /// <summary>
    /// The booklet fee was taken. Non-refundable, so it is recorded with the
    /// gateway's reference.
    ///
    /// Idempotent: the settlements topic is replayed from offset 0 on every start,
    /// so this is called again for every booklet ever paid for, each time this
    /// service comes up.
    /// </summary>
    public void ConfirmBookletPayment(string paymentRef, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paymentRef))
            throw new ParticipantValidationException(new[] { "A payment reference is required." });

        if (BookletPurchasedAt is not null) return;

        Require(SubscriptionStatus.Draft, "confirm a booklet payment for");

        BookletPaymentRef = paymentRef.Trim();
        BookletPurchasedAt = now;
        Status = SubscriptionStatus.BookletPurchased;
        ClearPaymentFailure();
    }

    /// <summary>
    /// الموافقة على الشروط والأحكام. Recorded with a timestamp because the
    /// booklet is the contract the cascade and forfeit rules live in — if a
    /// bidder later disputes a forfeited deposit, this is the answer.
    /// </summary>
    public void AcceptTerms(DateTimeOffset now)
    {
        Require(SubscriptionStatus.BookletPurchased, "accept terms for");
        TermsAcceptedAt = now;
        Status = SubscriptionStatus.TermsAccepted;
    }

    /// <summary>
    /// Picks how the deposit will be settled. Charges nothing.
    ///
    /// The charge moved out of here into <see cref="AuthoriseDeposit"/> for a reason
    /// that is not tidiness: choosing a method is an ordinary click and is gated as
    /// one, while taking a hundred thousand riyals off a citizen needs a second
    /// factor. With the request raised here, the step-up on the deposit endpoint
    /// guarded a confirmation of a charge that had already been sent.
    /// </summary>
    public void ChooseDeposit(DepositMethod method, AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.TermsAccepted, "choose a deposit method for");
        if (now >= terms.EndsAt)
            throw new ParticipantValidationException(
                new[] { "This auction has already ended." });

        DepositMethod = method;
        Status = SubscriptionStatus.AwaitingDeposit;
    }

    /// <summary>
    /// دفع مبلغ التأمين إلكترونيا — asks the payment service for the deposit.
    ///
    /// This is what the bidder's second factor actually authorises, and the reason
    /// this method takes no reference: there is nothing for the caller to assert.
    /// </summary>
    public void AuthoriseDeposit(AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "authorise a deposit for");
        if (DepositMethod != Domain.DepositMethod.Payment)
            throw new ParticipantValidationException(
                new[] { "This subscription is settling by bank guarantee, not payment." });
        if (now >= terms.EndsAt)
            throw new ParticipantValidationException(
                new[] { "This auction has already ended." });

        DepositRequestedAt = now;
        ClearPaymentFailure();

        _events.Add(new DepositRequested
        {
            AuctionId = AuctionId,
            BidderId = BidderId,
            AmountMinorUnits = terms.DepositMinorUnits,
            Method = DepositMethod.Value.ToString()
        });
    }

    /// <summary>
    /// The deposit was taken, as reported by the payment service. Idempotent for
    /// the same reason <see cref="ConfirmBookletPayment"/> is.
    /// </summary>
    public void ConfirmDepositPayment(
        string paymentRef, Bidder bidder, AuctionTerms terms, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paymentRef))
            throw new ParticipantValidationException(new[] { "A payment reference is required." });

        if (DepositPaidAt is not null) return;

        Require(SubscriptionStatus.AwaitingDeposit, "confirm a deposit payment for");
        if (DepositMethod != Domain.DepositMethod.Payment)
            throw new ParticipantValidationException(
                new[] { "This subscription is settling by bank guarantee, not payment." });

        DepositPaymentRef = paymentRef.Trim();
        DepositPaidAt = now;
        ClearPaymentFailure();
        BecomeEligible(bidder, terms, now);
    }

    /// <summary>
    /// The gateway refused. Nothing moves backwards — the bidder stays where they
    /// are and may try again — but the reason is kept so the portal can say what
    /// happened instead of showing a step that quietly failed.
    /// </summary>
    public void RecordPaymentRefused(string purpose, string reason, DateTimeOffset now)
    {
        PaymentFailurePurpose = purpose;
        PaymentFailureReason = string.IsNullOrWhiteSpace(reason) ? "Refused" : reason.Trim();
        PaymentFailedAt = now;

        // The request is no longer outstanding: leaving the timestamp set would
        // leave the portal waiting on a gateway that has already answered.
        if (purpose == nameof(PaymentPurposes.Deposit)) DepositRequestedAt = null;
        if (purpose == nameof(PaymentPurposes.Booklet)) BookletRequestedAt = null;
    }

    private void ClearPaymentFailure()
    {
        PaymentFailurePurpose = null;
        PaymentFailureReason = null;
        PaymentFailedAt = null;
    }

    /// <summary>
    /// رفع الضمان البنكي. Uploading is not settling: a guarantee is worth
    /// nothing until someone has checked it is real and still valid at the
    /// auction's end, so this leaves the bidder awaiting verification.
    /// </summary>
    public void SubmitBankGuarantee(
        Guid documentId, DateTimeOffset expiresAt, AuctionTerms terms)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "submit a bank guarantee for");
        if (DepositMethod != Domain.DepositMethod.BankGuarantee)
            throw new ParticipantValidationException(
                new[] { "This subscription is settling by payment, not bank guarantee." });

        if (expiresAt <= terms.EndsAt)
            throw new ParticipantValidationException(new[]
            {
                "The guarantee expires before the auction ends; it would be worthless "
                + "exactly when it is needed."
            });

        GuaranteeDocumentId = documentId;
        GuaranteeExpiresAt = expiresAt;
        GuaranteeVerifiedAt = null;
        GuaranteeVerifiedByUserId = null;
    }

    /// <summary>Manual verification by an administrator — no bank integration in v1.</summary>
    public void VerifyBankGuarantee(
        Guid verifiedByUserId, Bidder bidder, AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "verify a bank guarantee for");
        if (GuaranteeDocumentId is null)
            throw new ParticipantValidationException(new[] { "No guarantee has been submitted." });

        GuaranteeVerifiedAt = now;
        GuaranteeVerifiedByUserId = verifiedByUserId;
        BecomeEligible(bidder, terms, now);
    }

    /// <summary>
    /// The only path to eligibility. Everything the booklet requires is
    /// re-checked here rather than trusted from the status, because this is
    /// the moment the catcher starts accepting the bidder's money.
    /// </summary>
    private void BecomeEligible(Bidder bidder, AuctionTerms terms, DateTimeOffset now)
    {
        var problems = new List<string>();
        if (!bidder.IsVerified) problems.Add("The bidder's identity is not verified by Nafath.");
        if (!bidder.IsProfileComplete) problems.Add("The bidder's profile is incomplete.");
        if (BookletPurchasedAt is null) problems.Add("The terms booklet has not been purchased.");
        if (TermsAcceptedAt is null) problems.Add("The terms and conditions have not been accepted.");
        if (DepositPaidAt is null && GuaranteeVerifiedAt is null)
            problems.Add("No deposit has been settled.");
        if (problems.Count > 0) throw new ParticipantValidationException(problems);

        Status = SubscriptionStatus.Eligible;
        EligibleAt = now;
        RevokedAt = null;
        RevocationReason = null;
        PublishEligibility(true, bidder, terms);
    }

    public void Revoke(string reason, Bidder bidder, AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.Eligible, "revoke");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ParticipantValidationException(new[] { "A revocation reason is required." });

        Status = SubscriptionStatus.Revoked;
        RevokedAt = now;
        RevocationReason = reason.Trim();
        PublishEligibility(false, bidder, terms);
    }

    /// <summary>
    /// Rotates the signing key — a lost phone, a suspected leak. The bidder
    /// stays eligible and gets a new secret; anything signed with the old one
    /// stops verifying the moment the catcher sees the new epoch.
    /// </summary>
    public void RotateKey(Bidder bidder, AuctionTerms terms)
    {
        Require(SubscriptionStatus.Eligible, "rotate the key of");
        KeyEpoch++;
        PublishEligibility(true, bidder, terms);
    }

    /// <summary>
    /// auction-admin resolved deposits once the award was final. Never at the
    /// gavel: while the cascade can still reach a losing bidder, their deposit
    /// is held through the compliance window of everyone above them (§8.3).
    /// </summary>
    public void ResolveDeposit(bool forfeited, DateTimeOffset now)
    {
        if (DepositResolvedAt is not null) return;   // at-least-once delivery
        DepositResolvedAt = now;
        DepositForfeited = forfeited;
    }

    /// <summary>
    /// Publishes the eligibility fact, and the bidder's name only when this auction
    /// names its bidders (D-22).
    ///
    /// The filter is here, at the point of publication, rather than in whoever reads
    /// the topic. A masked auction's name is then not merely hidden — it never
    /// leaves this service, and no downstream consumer, present or future, can
    /// expose what it was never sent. The same argument as D-23 makes for the
    /// reserve price, applied to a citizen's name.
    /// </summary>
    private void PublishEligibility(bool eligible, Bidder bidder, AuctionTerms terms) =>
        _events.Add(new ParticipantEligibilityChanged
        {
            AuctionId = AuctionId,
            BidderId = BidderId,
            Eligible = eligible,
            KeyEpoch = KeyEpoch,
            DisplayNameAr = terms.BidderVisibility == BidderVisibility.Named
                ? bidder.NameAr
                : null
        });
}
