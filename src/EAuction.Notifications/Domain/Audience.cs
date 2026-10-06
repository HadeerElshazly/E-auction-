namespace EAuction.Notifications.Domain;

/// <summary>
/// Who to tell about an auction-wide event.
///
/// This service keeps its own copy of the roster rather than asking the participant
/// service, for the same reason the bid catcher does (D-12): "the auction has
/// started" has to reach every eligible bidder at the moment it starts, and a
/// synchronous call per bidder to another service at exactly that moment is the
/// shape of an outage.
///
/// Built from <c>auctions.participants</c>, which is compacted — so a replay from
/// offset 0 gives the current truth and nothing older.
/// </summary>
public sealed class Audience
{
    public Guid AuctionId { get; private set; }
    public Guid BidderId { get; private set; }

    /// <summary>
    /// Kept rather than deleted on revocation, because a revoked bidder is still
    /// owed the notice that they were revoked — and a row that was deleted could
    /// not say so.
    /// </summary>
    public bool Eligible { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private Audience() { }

    public Audience(Guid auctionId, Guid bidderId, bool eligible, DateTimeOffset now)
    {
        AuctionId = auctionId;
        BidderId = bidderId;
        Eligible = eligible;
        UpdatedAt = now;
    }

    public void Set(bool eligible, DateTimeOffset now)
    {
        Eligible = eligible;
        UpdatedAt = now;
    }
}

/// <summary>
/// An auction's name, so a message can say which auction it is about.
///
/// A notification that read "the auction you registered for has started" without
/// naming it is useless to a bidder registered for three.
/// </summary>
public sealed class AuctionName
{
    public Guid AuctionId { get; private set; }
    public string NameAr { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AuctionName() { }

    public AuctionName(Guid auctionId, string nameAr, DateTimeOffset now)
    {
        AuctionId = auctionId;
        NameAr = nameAr;
        UpdatedAt = now;
    }

    public void Rename(string nameAr, DateTimeOffset now)
    {
        NameAr = nameAr;
        UpdatedAt = now;
    }
}
