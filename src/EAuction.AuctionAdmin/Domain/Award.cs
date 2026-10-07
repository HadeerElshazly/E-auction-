namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// One ترسية attempt. A cascade produces a new Award row rather than mutating
/// the previous one: each step is a full new award with its own committee
/// confirmation, letters and signature, and the history of who was awarded
/// and why it moved on has to survive (§8.2).
/// </summary>
public sealed class Award
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AuctionId { get; private set; }
    public Guid BidderId { get; private set; }
    public long AmountMinorUnits { get; private set; }

    /// <summary>0 for the first winner, 1 for the next after a disqualification, and so on.</summary>
    public int CascadeStep { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }
    public Guid ConfirmedByUserId { get; private set; }
    public DateTimeOffset ComplianceDeadline { get; private set; }

    /// <summary>خطاب الترسية generated for signature.</summary>
    public Guid? LetterDocumentId { get; private set; }

    /// <summary>اعادة رفع الخطاب الموقع.</summary>
    public Guid? SignedLetterDocumentId { get; private set; }

    public DateTimeOffset? WinnerNotifiedAt { get; private set; }
    public DateTimeOffset? DisqualifiedAt { get; private set; }
    public string? DisqualificationReason { get; private set; }
    public bool DepositForfeited { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }

    public bool IsOpen => DisqualifiedAt is null && SettledAt is null;

    // --- follow-up (الخاصية 11) ---------------------------------------------

    private readonly List<AwardReceipt> _receipts = new();
    public IReadOnlyList<AwardReceipt> Receipts => _receipts;

    public long PaidMinorUnits => _receipts.Sum(r => r.AmountMinorUnits);
    public long RemainingMinorUnits => Math.Max(0, AmountMinorUnits - PaidMinorUnits);

    public TransferStatus TransferStatus { get; private set; } = TransferStatus.NotStarted;

    /// <summary>The new deed number or the notary's reference. Required to close the transfer.</summary>
    public string? TransferReference { get; private set; }
    public Guid? TransferDocumentId { get; private set; }
    public DateTimeOffset? TransferUpdatedAt { get; private set; }
    public DateTimeOffset? TransferCompletedAt { get; private set; }

    /// <summary>
    /// متعثر — past the payment deadline with money still owed. Flagged for a person
    /// to review rather than acted on: the first phase does not re-award by itself.
    /// </summary>
    public bool IsOverdue(DateTimeOffset now) =>
        IsOpen && RemainingMinorUnits > 0 && now > ComplianceDeadline;

    internal void RecordReceipt(
        AwardReceiptKind kind, long amountMinorUnits, DateTimeOffset paidOn, string reference,
        Guid? documentId, Guid recordedByUserId, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new InvalidOperationException("This award is already closed.");

        var problems = new List<string>();
        if (amountMinorUnits <= 0) problems.Add("المبلغ يجب أن يكون أكبر من صفر.");
        else if (amountMinorUnits > RemainingMinorUnits)
            problems.Add("المبلغ أكبر من المتبقي من مبلغ الترسية.");
        if (string.IsNullOrWhiteSpace(reference)) problems.Add("رقم الإيصال أو المرجع مطلوب.");
        else if (reference.Trim().Length > 100) problems.Add("المرجع طويل جداً.");
        if (paidOn > now.AddDays(1)) problems.Add("تاريخ السداد في المستقبل.");
        if (kind == AwardReceiptKind.DepositCredit && _receipts.Any(r => r.Kind == kind))
            problems.Add("احتُسب التأمين من قبل.");
        if (problems.Count > 0) throw new AuctionValidationException(problems);

        _receipts.Add(new AwardReceipt(
            kind, amountMinorUnits, paidOn, reference.Trim(), documentId, recordedByUserId, now));
    }

    internal void UpdateTransfer(
        TransferStatus status, string? reference, Guid? documentId, DateTimeOffset now)
    {
        if (DisqualifiedAt is not null)
            throw new InvalidOperationException("This award was withdrawn.");
        if (TransferStatus == TransferStatus.Completed)
            throw new InvalidOperationException("The transfer is already complete.");

        if (status == TransferStatus.Completed)
        {
            var problems = new List<string>();
            // «يتطلب إغلاق … الإفراغ مرجعاً أو إثباتاً»
            if (string.IsNullOrWhiteSpace(reference) && documentId is null)
                problems.Add("إغلاق الإفراغ يتطلب رقم الصك الجديد أو إرفاق إثبات.");
            if (RemainingMinorUnits > 0)
                problems.Add("لا يكتمل الإفراغ قبل سداد كامل مبلغ الترسية.");
            if (problems.Count > 0) throw new AuctionValidationException(problems);
            TransferCompletedAt = now;
        }

        TransferStatus = status;
        if (!string.IsNullOrWhiteSpace(reference)) TransferReference = reference.Trim();
        if (documentId is not null) TransferDocumentId = documentId;
        TransferUpdatedAt = now;
    }

    private Award() { }

    internal Award(Guid auctionId, Guid bidderId, long amountMinorUnits,
        int cascadeStep, Guid confirmedByUserId, DateTimeOffset confirmedAt,
        TimeSpan complianceWindow)
    {
        AuctionId = auctionId;
        BidderId = bidderId;
        AmountMinorUnits = amountMinorUnits;
        CascadeStep = cascadeStep;
        ConfirmedByUserId = confirmedByUserId;
        ConfirmedAt = confirmedAt;
        ComplianceDeadline = confirmedAt + complianceWindow;
    }

    internal void AttachLetter(Guid documentId) => LetterDocumentId = documentId;

    internal void AttachSignedLetter(Guid documentId)
    {
        if (LetterDocumentId is null)
            throw new InvalidOperationException(
                "Cannot upload a signed letter before the letter has been generated.");
        SignedLetterDocumentId = documentId;
    }

    internal void MarkWinnerNotified(DateTimeOffset at)
    {
        // Slide 6 puts notification after the signed letter is back: the
        // winner is told once the award is actually executed, not when the
        // committee first votes.
        if (SignedLetterDocumentId is null)
            throw new InvalidOperationException(
                "Cannot notify the winner before the signed award letter is uploaded.");
        WinnerNotifiedAt = at;
    }

    internal void Disqualify(string reason, bool forfeitDeposit, DateTimeOffset at)
    {
        if (!IsOpen)
            throw new InvalidOperationException("This award is already closed.");
        DisqualifiedAt = at;
        DisqualificationReason = reason;
        DepositForfeited = forfeitDeposit;
    }

    internal void Settle(DateTimeOffset at)
    {
        if (!IsOpen)
            throw new InvalidOperationException("This award is already closed.");
        if (WinnerNotifiedAt is null)
            throw new InvalidOperationException(
                "Cannot settle before the winner has been notified.");
        // Settling releases every other bidder's deposit, so it must not happen on a
        // price that has not been paid: the receipts are what say it has.
        if (RemainingMinorUnits > 0)
            throw new AuctionValidationException(new[]
            {
                "لا يمكن التسوية قبل تسجيل سداد كامل مبلغ الترسية. المتبقي "
                + $"{RemainingMinorUnits / 100m:N2} ر.س."
            });
        SettledAt = at;
    }
}
