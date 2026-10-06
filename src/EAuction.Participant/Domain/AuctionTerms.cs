using EAuction.Core;

namespace EAuction.Participant.Domain;

/// <summary>
/// What this service needs to know about an auction, materialised from
/// <c>auctions.upcoming</c>.
///
/// Persisted rather than held in memory: a bidder mid-subscription when the
/// pod restarts should not find the auction has vanished. It carries no
/// reserve price — that topic is not readable here, by design (D-23).
/// </summary>
public sealed class AuctionTerms
{
    public Guid AuctionId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public long DepositMinorUnits { get; private set; }
    public long BookletPriceMinorUnits { get; private set; }

    /// <summary>
    /// Whether this auction names its bidders (D-22). Held here, in the service that
    /// knows people's names, so that a masked auction's eligibility event can be
    /// published without a name in it at all — rather than published with one and
    /// filtered out downstream by whoever remembers to.
    /// </summary>
    public BidderVisibility BidderVisibility { get; private set; } = BidderVisibility.Masked;

    /// <summary>
    /// كراسة الشروط in the document service, so this service can mint a grant for
    /// a bidder who has paid for it.
    ///
    /// Held here because the rule — "has this bidder paid?" — is this service's,
    /// and the document service must not have to learn what a subscription is.
    /// </summary>
    public Guid? BookletDocumentId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AuctionTerms() { }

    public AuctionTerms(
        Guid auctionId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        long depositMinorUnits, long bookletPriceMinorUnits,
        BidderVisibility bidderVisibility = BidderVisibility.Masked,
        Guid? bookletDocumentId = null)
    {
        AuctionId = auctionId;
        BidderVisibility = bidderVisibility;
        StartsAt = startsAt;
        EndsAt = endsAt;
        DepositMinorUnits = depositMinorUnits;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
        BookletDocumentId = bookletDocumentId;
    }

    public void Update(
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        long depositMinorUnits, long bookletPriceMinorUnits, DateTimeOffset now,
        BidderVisibility bidderVisibility = BidderVisibility.Masked,
        Guid? bookletDocumentId = null)
    {
        BidderVisibility = bidderVisibility;
        StartsAt = startsAt;
        EndsAt = endsAt;
        DepositMinorUnits = depositMinorUnits;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
        BookletDocumentId = bookletDocumentId;
        UpdatedAt = now;
    }
}
