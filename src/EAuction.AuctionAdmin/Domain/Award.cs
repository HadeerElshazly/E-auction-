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
        SettledAt = at;
    }
}
