namespace EAuction.Core;

/// <summary>
/// شهادة مزايدة — a bidder's proof that a particular bid of theirs was accepted
/// and recorded.
///
/// The 202 a bidder gets when they bid carries a <see cref="BidReceipt"/>: enough
/// to show someone, but not enough for anybody to check. The signature on it is an
/// HMAC over the frame, and the frame is not in the receipt — so a holder cannot
/// recompute it, and the key that would let them is secret by design. A receipt
/// nobody can verify is a screenshot.
///
/// So a certificate is not issued from what the holder presents. It is read back
/// out of the append-only bid log at the offset the receipt names, and every field
/// below comes from that record rather than from the request. That makes it a
/// stronger claim than "this MAC checks out": it says the bid is in the legal
/// record, at that position, with that amount.
///
/// What it is not: a document. There is no document service yet (§2 lists MinIO as
/// unused), so this is the data and the portal renders and prints it. A PDF with a
/// municipal seal is a later job, and this is the content it would carry.
/// </summary>
public sealed record BidCertificate
{
    /// <summary>A short human reference, for quoting in correspondence.</summary>
    public required string Reference { get; init; }

    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }

    /// <summary>The bidder's own identifier for the bid, from their client.</summary>
    public required Guid ClientBidId { get; init; }

    /// <summary>Position in the append-only log. This is what makes it checkable.</summary>
    public required long Offset { get; init; }

    public required long AmountMinorUnits { get; init; }

    /// <summary>When the bidder's client says it built the frame.</summary>
    public required long ClientTimestampMs { get; init; }

    /// <summary>
    /// When the service accepted it. The authoritative time: D-03 settles order by
    /// log position, and a client clock is the bidder's own.
    /// </summary>
    public required long ServerTimestampMs { get; init; }

    /// <summary>"Online", or "Onsite" for a bid a clerk entered in the hall.</summary>
    public required string Channel { get; init; }

    /// <summary>
    /// The clerk who entered it, for an onsite bid; null online. An onsite bid
    /// claims something weaker than an online one — that a named clerk recorded it
    /// for this bidder — and a certificate that hid the difference would overstate
    /// what it proves.
    /// </summary>
    public Guid? EnteredByUserId { get; init; }

    /// <summary>
    /// The receipt signature, recomputed from the logged frame. Equal to the one
    /// handed over at the time, which is how a bidder's saved receipt and this
    /// certificate confirm each other.
    /// </summary>
    public required string Signature { get; init; }

    /// <summary>
    /// Whether a signature presented with the request matched. Null when none was
    /// presented — a bidder reading their own certificate does not have to prove
    /// anything, but someone checking a receipt handed to them does.
    /// </summary>
    public bool? PresentedSignatureMatched { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }
}
