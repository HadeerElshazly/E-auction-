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
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AuctionTerms() { }

    public AuctionTerms(
        Guid auctionId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        long depositMinorUnits, long bookletPriceMinorUnits)
    {
        AuctionId = auctionId;
        StartsAt = startsAt;
        EndsAt = endsAt;
        DepositMinorUnits = depositMinorUnits;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
    }

    public void Update(
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        long depositMinorUnits, long bookletPriceMinorUnits, DateTimeOffset now)
    {
        StartsAt = startsAt;
        EndsAt = endsAt;
        DepositMinorUnits = depositMinorUnits;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
        UpdatedAt = now;
    }
}
