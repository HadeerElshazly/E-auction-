namespace EAuction.Core;

/// <summary>
/// Whether the people bidding in an auction are visible to each other.
///
/// D-22 masks the leading bidder as <c>مزايد #4</c>, for privacy, against collusion,
/// and because a national-ID-verified citizen's name on a public page is personal
/// data under PDPL. That remains the default and the safe answer. It is not,
/// however, the only lawful answer: a hall auction is held in public and everyone
/// in the room can see who raised their paddle, so a masked onsite auction is a
/// fiction, and the client may have a legal basis for naming bidders in some sales
/// and not others.
///
/// So this is the administrator's decision, per auction, taken while the auction is
/// still a draft — never afterwards. Bidders put down a deposit having been told who
/// else would be able to see them; changing that once they have relied on it is a
/// different auction, exactly as with the dates and the deposit.
///
/// <see cref="Masked"/> is the default everywhere it is not stated, so an auction
/// created by a path that has not thought about this does not name anybody.
/// </summary>
public enum BidderVisibility
{
    /// <summary>A per-auction pseudonym and nothing else. The default (D-22).</summary>
    Masked = 0,

    /// <summary>
    /// The leading bidder's name, as the national registry gave it.
    ///
    /// Chosen deliberately and recorded against the administrator who chose it: it
    /// publishes a citizen's name alongside what they are willing to pay for state
    /// land, and that needs a reason.
    /// </summary>
    Named = 1,
}
