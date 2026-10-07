namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// The auction lifecycle (docs/ARCHITECTURE.md §6.1).
///
/// The committee awards, not the system (D-08): reaching <see cref="PendingAward"/>
/// means bidding produced a provisional leader, nothing more. Only
/// <see cref="Awarded"/> is a legal outcome, and only a human puts it there.
/// </summary>
public enum AuctionStatus
{
    /// <summary>Being prepared. The only state in which auction data may be edited.</summary>
    Draft = 0,

    /// <summary>Submitted for approval. إعداد المزاد complete, awaiting sign-off.</summary>
    PendingReview = 1,

    /// <summary>Sent back with comments. Returns to Draft for correction.</summary>
    Rejected = 2,

    /// <summary>Approved. The approval event is in the outbox, not yet relayed.</summary>
    Approved = 3,

    /// <summary>Published to auctions.upcoming. Bidders can see it; the catcher will accept bids in window.</summary>
    Scheduled = 4,

    Live = 5,
    Closing = 6,

    /// <summary>استعراض حالة الأهلية — the committee checks the leader's eligibility.</summary>
    PendingEligibilityReview = 7,

    /// <summary>Awaiting تأكيد ترسية العطاء. Re-entered on every cascade step.</summary>
    PendingAward = 8,

    /// <summary>Committee confirmed. Letters, signature and notification follow.</summary>
    Awarded = 9,

    /// <summary>لم تُرسَ — no bid reached the reserve, or the ladder was exhausted.</summary>
    Unsold = 10,

    /// <summary>Winner failed compliance. Cascades to the next qualifying bidder.</summary>
    WinnerDisqualified = 11,

    Settled = 12,
    Closed = 13,

    /// <summary>
    /// أُلغي — withdrawn by an administrator after approval and before it opened,
    /// with a recorded reason. Terminal: nothing bids on it and every deposit is
    /// released.
    /// </summary>
    Cancelled = 14
}
