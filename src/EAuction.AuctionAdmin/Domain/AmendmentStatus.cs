namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// Where a published auction stands with respect to a change made after it was
/// approved (docs/ARCHITECTURE.md §6.5).
///
/// Orthogonal to <see cref="AuctionStatus"/> on purpose. An auction being amended is
/// still <c>Scheduled</c>: it is on the catalogue, bidders are buying its booklet and
/// paying its deposit, and the processor will open it on the published date. What
/// this records is that the administrator's copy has moved ahead of the published
/// one, and whether the committee has been asked to approve the difference.
/// </summary>
public enum AmendmentStatus
{
    /// <summary>The published terms are the terms. Also every auction that was never published.</summary>
    None = 0,

    /// <summary>
    /// An administrator has changed a published auction. Bidders still see the
    /// approved version; the change goes out only when the committee approves it.
    /// </summary>
    Editing = 1,

    /// <summary>The change is before the committee. Nothing more is edited until it decides.</summary>
    PendingReview = 2,
}
