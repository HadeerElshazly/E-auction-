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

    /// <summary>
    /// Which كراسة الشروط was accepted, not only when. Documents are immutable — a
    /// revised booklet is a new upload with a new id — so the id is the version, and
    /// the document service still holds those exact bytes and their SHA-256. A
    /// timestamp alone could not say which text a disputed forfeit was agreed under
    /// once the booklet had been replaced.
    /// </summary>
    public Guid? AcceptedBookletDocumentId { get; private set; }

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

    /// <summary>Stands in for a gateway reference on a booklet that cost nothing.</summary>
    public const string FreeBookletRef = "FREE";

    public Guid? GuaranteeDocumentId { get; private set; }
    public DateTimeOffset? GuaranteeExpiresAt { get; private set; }
    public DateTimeOffset? GuaranteeVerifiedAt { get; private set; }
    public Guid? GuaranteeVerifiedByUserId { get; private set; }

    /// <summary>
    /// Why staff refused the last guarantee, kept until the bidder submits another.
    /// The bidder is told the reason rather than left awaiting a review that ended.
    /// </summary>
    public string? GuaranteeRejectionReason { get; private set; }
    public DateTimeOffset? GuaranteeRejectedAt { get; private set; }

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

    /// <summary>The winner's paid deposit, which goes towards the price rather than back.</summary>
    public bool DepositAppliedToPurchase { get; private set; }

    /// <summary>
    /// When staff recorded the refund, the guarantee's release or the forfeit as
    /// done, and against what. The requirements (الخاصية 11) will not have one closed
    /// without a reference or proof — the refund's gateway reference, the bank's
    /// release letter number.
    /// </summary>
    public DateTimeOffset? DepositClosedAt { get; private set; }
    public string? DepositClosureReference { get; private set; }
    public Guid? DepositClosedByUserId { get; private set; }

    /// <summary>Where this bidder's deposit stands once there is one.</summary>
    public DepositSettlement DepositSettlement =>
        DepositPaidAt is null && GuaranteeVerifiedAt is null ? DepositSettlement.None
        : DepositClosedAt is not null ? DepositSettlement.Closed
        : DepositResolvedAt is null ? DepositSettlement.Held
        : DepositAppliedToPurchase ? DepositSettlement.AppliedToPurchase
        : DepositForfeited ? DepositSettlement.ToForfeit
        : DepositMethod == Domain.DepositMethod.BankGuarantee ? DepositSettlement.ToRelease
        : DepositSettlement.ToRefund;

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
        terms.RequireOpen();

        BookletRequestedAt = now;
        ClearPaymentFailure();

        // A free booklet (the policy may waive the fee) is obtained, not bought:
        // there is nothing for the gateway to settle, so waiting on a settlement for
        // zero riyals would strand the bidder at this step for ever.
        if (terms.BookletPriceMinorUnits == 0)
        {
            BookletPaymentRef = FreeBookletRef;
            BookletPurchasedAt = now;
            Status = SubscriptionStatus.BookletPurchased;
            return;
        }

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
    public void AcceptTerms(DateTimeOffset now, Guid? bookletDocumentId = null)
    {
        Require(SubscriptionStatus.BookletPurchased, "accept terms for");
        TermsAcceptedAt = now;
        AcceptedBookletDocumentId = bookletDocumentId;
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
        terms.RequireOpen();
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
        terms.RequireOpen();
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
        terms.RequireOpen();
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
        GuaranteeRejectionReason = null;
        GuaranteeRejectedAt = null;
    }

    /// <summary>
    /// Staff refuse a submitted guarantee — forged, wrong amount, wrong bank. The
    /// subscription stays awaiting its deposit so the bidder can submit another;
    /// refusing one piece of paper is not refusing the person.
    /// </summary>
    public void RejectBankGuarantee(string reason, DateTimeOffset now)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "reject a bank guarantee for");
        if (GuaranteeDocumentId is null)
            throw new ParticipantValidationException(new[] { "No guarantee has been submitted." });
        if (string.IsNullOrWhiteSpace(reason))
            throw new ParticipantValidationException(new[] { "A rejection reason is required." });

        GuaranteeDocumentId = null;
        GuaranteeExpiresAt = null;
        GuaranteeRejectionReason = reason.Trim();
        GuaranteeRejectedAt = now;

        _events.Add(new BankGuaranteeRejected
        {
            AuctionId = AuctionId,
            BidderId = BidderId,
            Reason = GuaranteeRejectionReason,
            At = now
        });
    }

    /// <summary>
    /// Where the bidder stands, in the four words the requirements use: still
    /// completing steps, under review, accepted, or rejected with a reason.
    ///
    /// Derived rather than stored, because every one of them is already a fact on
    /// this row — a second column would be a second place for them to disagree.
    /// </summary>
    public (Eligibility State, string? Reason) Eligibility => Status switch
    {
        SubscriptionStatus.Eligible => (Domain.Eligibility.Accepted, null),
        SubscriptionStatus.Revoked => (Domain.Eligibility.Rejected, RevocationReason),
        SubscriptionStatus.AwaitingDeposit when GuaranteeRejectionReason is not null
            => (Domain.Eligibility.Rejected, GuaranteeRejectionReason),
        SubscriptionStatus.AwaitingDeposit when GuaranteeDocumentId is not null
            => (Domain.Eligibility.UnderReview, null),
        SubscriptionStatus.AwaitingDeposit when DepositRequestedAt is not null
                                             && PaymentFailedAt is null
            => (Domain.Eligibility.UnderReview, null),
        _ => (Domain.Eligibility.Incomplete, null),
    };

    /// <summary>Manual verification by an administrator — no bank integration in v1.</summary>
    public void VerifyBankGuarantee(
        Guid verifiedByUserId, Bidder bidder, AuctionTerms terms, DateTimeOffset now)
    {
        Require(SubscriptionStatus.AwaitingDeposit, "verify a bank guarantee for");
        if (now >= terms.EndsAt)
            throw new ParticipantValidationException(
                new[] { "This auction has already ended." });
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
        if (!bidder.IsVerified) problems.Add("لم يتم التحقق من هوية المزايد عبر نفاذ.");
        if (!bidder.IsProfileComplete) problems.Add("بيانات المزايد غير مكتملة.");
        if (BookletPurchasedAt is null) problems.Add("لم يتم شراء كراسة الشروط.");
        if (TermsAcceptedAt is null) problems.Add("لم تتم الموافقة على الشروط والأحكام.");
        if (DepositPaidAt is null && GuaranteeVerifiedAt is null)
            problems.Add("لم يُسدَّد التأمين.");
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
        // Eligibility is permission to bid. Once bidding has ended there is nothing
        // left to withdraw, and the result is the committee's: a non-compliant
        // winner is disqualified on the award (سحب الفوز), which moves the deposit
        // and the ladder correctly — a revocation here would do neither.
        if (now >= terms.EndsAt)
            throw new ParticipantValidationException(
                new[] { "Bidding has ended; a non-compliant winner is disqualified on the award instead." });
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
    public void ResolveDeposit(bool forfeited, DateTimeOffset now, bool appliedToPurchase = false)
    {
        if (DepositResolvedAt is not null) return;   // at-least-once delivery
        DepositResolvedAt = now;
        DepositForfeited = forfeited;
        DepositAppliedToPurchase = appliedToPurchase && !forfeited;
    }

    /// <summary>
    /// Records the deposit as dealt with — refunded, its guarantee released, or its
    /// forfeit carried out — against a reference. Done by hand: automatic bank
    /// integration is out of the first phase, and this is the record that stays in it.
    /// </summary>
    public void CloseDeposit(string reference, Guid closedByUserId, DateTimeOffset now)
    {
        var problems = new List<string>();
        if (DepositSettlement is DepositSettlement.None)
            problems.Add("لا يوجد تأمين مسدَّد لهذا المشارك.");
        else if (DepositSettlement is DepositSettlement.Held)
            problems.Add("التأمين ما زال محتجزاً — يُحسم بعد التسوية أو عدم البيع.");
        else if (DepositSettlement is DepositSettlement.Closed)
            problems.Add("أُغلق التأمين من قبل.");
        else if (DepositSettlement is DepositSettlement.AppliedToPurchase)
            problems.Add("التأمين احتُسب من ثمن الترسية ولا يُرد.");
        if (string.IsNullOrWhiteSpace(reference))
            problems.Add("رقم المرجع أو الإثبات مطلوب لإغلاق التأمين.");
        else if (reference.Trim().Length > 100)
            problems.Add("المرجع طويل جداً.");
        if (problems.Count > 0) throw new ParticipantValidationException(problems);

        DepositClosedAt = now;
        DepositClosureReference = reference.Trim();
        DepositClosedByUserId = closedByUserId;
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
