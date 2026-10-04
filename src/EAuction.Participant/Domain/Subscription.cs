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

    public DateTimeOffset? BookletPurchasedAt { get; private set; }
    public string? BookletPaymentRef { get; private set; }
    public DateTimeOffset? TermsAcceptedAt { get; private set; }

    public DepositMethod? DepositMethod { get; private set; }
    public DateTimeOffset? DepositPaidAt { get; private set; }
    public string? DepositPaymentRef { get; private set; }

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

    /// <summary>شراء كراسة الشروط. Non-refundable, so it is recorded with its payment reference.</summary>
    public void PurchaseBooklet(string paymentRef, DateTimeOffset now)
    {
        Require(SubscriptionStatus.Draft, "purchase a booklet for");
        if (string.IsNullOrWhiteSpace(paymentRef))
            throw new ParticipantValidationException(new[] { "A payment reference is required." });

        BookletPaymentRef = paymentRef.Trim();
        BookletPurchasedAt = now;
        Status = SubscriptionStatus.BookletPurchased;
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

    public void ChooseDeposit(DepositMethod method, AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.TermsAccepted, "choose a deposit method for");
        if (now >= terms.EndsAt)
            throw new ParticipantValidationException(
                new[] { "This auction has already ended." });

        DepositMethod = method;
        Status = SubscriptionStatus.AwaitingDeposit;

        _events.Add(new DepositRequested
        {
            AuctionId = AuctionId,
            BidderId = BidderId,
            AmountMinorUnits = terms.DepositMinorUnits,
            Method = method.ToString()
        });
    }

    /// <summary>دفع مبلغ التأمين إلكترونيا — confirmed by the payment service.</summary>
    public void ConfirmDepositPayment(string paymentRef, Bidder bidder, DateTimeOffset now)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "confirm a deposit payment for");
        if (DepositMethod != Domain.DepositMethod.Payment)
            throw new ParticipantValidationException(
                new[] { "This subscription is settling by bank guarantee, not payment." });
        if (string.IsNullOrWhiteSpace(paymentRef))
            throw new ParticipantValidationException(new[] { "A payment reference is required." });

        DepositPaymentRef = paymentRef.Trim();
        DepositPaidAt = now;
        BecomeEligible(bidder, now);
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
    public void VerifyBankGuarantee(Guid verifiedByUserId, Bidder bidder, DateTimeOffset now)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "verify a bank guarantee for");
        if (GuaranteeDocumentId is null)
            throw new ParticipantValidationException(new[] { "No guarantee has been submitted." });

        GuaranteeVerifiedAt = now;
        GuaranteeVerifiedByUserId = verifiedByUserId;
        BecomeEligible(bidder, now);
    }

    /// <summary>
    /// The only path to eligibility. Everything the booklet requires is
    /// re-checked here rather than trusted from the status, because this is
    /// the moment the catcher starts accepting the bidder's money.
    /// </summary>
    private void BecomeEligible(Bidder bidder, DateTimeOffset now)
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
        PublishEligibility(true);
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        Require(SubscriptionStatus.Eligible, "revoke");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ParticipantValidationException(new[] { "A revocation reason is required." });

        Status = SubscriptionStatus.Revoked;
        RevokedAt = now;
        RevocationReason = reason.Trim();
        PublishEligibility(false);
    }

    /// <summary>
    /// Rotates the signing key — a lost phone, a suspected leak. The bidder
    /// stays eligible and gets a new secret; anything signed with the old one
    /// stops verifying the moment the catcher sees the new epoch.
    /// </summary>
    public void RotateKey()
    {
        Require(SubscriptionStatus.Eligible, "rotate the key of");
        KeyEpoch++;
        PublishEligibility(true);
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

    private void PublishEligibility(bool eligible) =>
        _events.Add(new ParticipantEligibilityChanged
        {
            AuctionId = AuctionId,
            BidderId = BidderId,
            Eligible = eligible,
            KeyEpoch = KeyEpoch
        });
}
