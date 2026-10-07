namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// One sum the winner paid towards the award, recorded by hand.
///
/// The price of the land is paid to the municipality outside this platform — a bank
/// transfer, SADAD — and full collection integration is out of the first phase. What
/// stays in scope is the record (المرحلة الأولى، الخاصية 11): who entered which
/// amount, against which receipt, when. So a receipt is an entry with a reference,
/// never a charge.
/// </summary>
public sealed class AwardReceipt
{
    public Guid ReceiptId { get; private set; } = Guid.NewGuid();
    public AwardReceiptKind Kind { get; private set; }
    public long AmountMinorUnits { get; private set; }

    /// <summary>The day the money arrived, as the receipt says — not when it was typed in.</summary>
    public DateTimeOffset PaidOn { get; private set; }

    /// <summary>The receipt, transfer or SADAD number. Required: an entry nobody can trace is not a record.</summary>
    public string Reference { get; private set; } = "";

    /// <summary>A scan of the receipt, Private in the document service.</summary>
    public Guid? DocumentId { get; private set; }

    public Guid RecordedByUserId { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    private AwardReceipt() { }

    internal AwardReceipt(
        AwardReceiptKind kind, long amountMinorUnits, DateTimeOffset paidOn, string reference,
        Guid? documentId, Guid recordedByUserId, DateTimeOffset recordedAt)
    {
        Kind = kind;
        AmountMinorUnits = amountMinorUnits;
        PaidOn = paidOn;
        Reference = reference;
        DocumentId = documentId;
        RecordedByUserId = recordedByUserId;
        RecordedAt = recordedAt;
    }
}

public enum AwardReceiptKind
{
    /// <summary>سداد — money the winner paid towards the price.</summary>
    Payment,

    /// <summary>
    /// احتساب التأمين — the winner's deposit counted towards the price. The payment
    /// service applies a paid deposit to the purchase when the auction settles; this
    /// is the same money entered on the award, so the remaining figure is honest
    /// before then. At most once per award.
    /// </summary>
    DepositCredit,
}

/// <summary>
/// الإفراغ — the title passing to the winner at the notary. Done outside the
/// platform; automatic transfer is out of the first phase, so this tracks it.
/// </summary>
public enum TransferStatus
{
    NotStarted,
    InProgress,
    Completed,
}
