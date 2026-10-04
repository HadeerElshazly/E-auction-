namespace EAuction.Core;

/// <summary>
/// Why a bid was turned away. The catcher can produce the first five from
/// in-memory state; the rest are the processor's authoritative verdicts.
/// </summary>
public enum RejectionReason
{
    None = 0,
    MalformedFrame,
    BadSignature,
    UnknownAuction,
    OutsideWindow,
    NotEligible,
    RateLimited,
    BelowMinimumIncrement,
    SelfOutbid,
    DuplicateBidId,
    AuctionClosed
}

/// <summary>
/// Phase 1 of acceptance (§7.2): the bid is durably recorded, not yet judged.
/// The client must not show a win on this alone — the processor's verdict
/// arrives separately over the push channel.
/// </summary>
public readonly record struct BidReceipt(
    Guid AuctionId,
    Guid BidderId,
    Guid ClientBidId,
    long Offset,
    long ServerTimestampMs,
    string Signature);

/// <summary>Phase 2: the processor's authoritative verdict.</summary>
public readonly record struct BidVerdict(
    Guid AuctionId,
    Guid BidderId,
    Guid ClientBidId,
    long Offset,
    bool Accepted,
    RejectionReason Reason,
    long PriceAfterMinorUnits,
    DateTimeOffset EffectiveEndsAt,
    int ExtensionsUsed);
