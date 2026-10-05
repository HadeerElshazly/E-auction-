using System.Collections.Concurrent;

namespace EAuction.QueryBff;

/// <summary>
/// Per-auction pseudonyms for bidders: the masked public view shows
/// <c>مزايد #4</c>, never a bidder id or a name (D-22, the default).
///
/// Numbered **per auction**, assigned on first sight. A single global numbering
/// would be worse than none: the same alias appearing in two auctions tells a
/// watcher those two leaders are the same person, which is exactly the collusion
/// signal the masking exists to remove. A number that is only meaningful inside
/// one auction carries no information outside it.
///
/// The ordinal is the order a bidder first *led*, not the order they joined, so it
/// does not reveal how many people are subscribed.
/// </summary>
public sealed class BidderAliases
{
    private readonly ConcurrentDictionary<Guid, AuctionAliases> _byAuction = new();

    public string For(Guid auctionId, Guid bidderId) =>
        _byAuction.GetOrAdd(auctionId, _ => new AuctionAliases()).For(bidderId);

    private sealed class AuctionAliases
    {
        private readonly Dictionary<Guid, int> _ordinals = [];
        private readonly object _gate = new();

        public string For(Guid bidderId)
        {
            lock (_gate)
            {
                if (!_ordinals.TryGetValue(bidderId, out var ordinal))
                {
                    ordinal = _ordinals.Count + 1;
                    _ordinals[bidderId] = ordinal;
                }
                // The whole label, not a bare number: the slot it fills may instead
                // hold a person's name, and a portal that prefixed "مزايد" itself
                // would read "مزايد سارة الحربي" the moment an auction named them.
                return $"مزايد #{ordinal}";
            }
        }
    }
}

/// <summary>
/// The names of bidders in auctions that name them (D-22), and of nobody else.
///
/// A name only ever arrives here for an auction the administrator set to
/// <c>Named</c> — the participant service does not put one on the topic otherwise —
/// and this service drops it again if it ever sees one for a masked auction. Two
/// locks on the same door, because this is the public read path: whatever is in
/// here is one careless endpoint away from being on the internet.
/// </summary>
public sealed class BidderNames
{
    private readonly ConcurrentDictionary<(Guid Auction, Guid Bidder), string> _names = new();

    public int Count => _names.Count;

    /// <summary>
    /// Remembers a name, if this auction names its bidders. Returns whether it did,
    /// so the consumer can say so in a log rather than silently dropping it.
    /// </summary>
    public bool Remember(AuctionEntry auction, Guid bidderId, string? nameAr)
    {
        if (!auction.NamesBidders || string.IsNullOrWhiteSpace(nameAr))
        {
            Forget(auction.AuctionId, bidderId);
            return false;
        }

        _names[(auction.AuctionId, bidderId)] = nameAr.Trim();
        return true;
    }

    /// <summary>A revoked bidder, or an auction that stopped naming them.</summary>
    public void Forget(Guid auctionId, Guid bidderId) =>
        _names.TryRemove((auctionId, bidderId), out _);

    public string? For(Guid auctionId, Guid bidderId) =>
        _names.TryGetValue((auctionId, bidderId), out var name) ? name : null;
}

/// <summary>
/// What a watcher is told about whoever is leading — the one place that is decided.
///
/// Every live view has exactly one slot for the leader, and this fills it. Masked
/// auctions get a per-auction pseudonym; named ones get the bidder's name, falling
/// back to the pseudonym if the name has not arrived yet, because a view that is
/// briefly less revealing is harmless and one that throws is not.
///
/// Keeping it to a single slot and a single decision is deliberate: D-22 is then a
/// property of the shape, the way D-23 is for the reserve price, instead of a rule
/// each new endpoint has to remember.
/// </summary>
public sealed class LeaderLabels(BidderAliases aliases, BidderNames names)
{
    public string? For(AuctionEntry auction)
    {
        if (auction.LeaderBidderId is not { } leader) return null;

        return auction.NamesBidders
            ? names.For(auction.AuctionId, leader) ?? aliases.For(auction.AuctionId, leader)
            : aliases.For(auction.AuctionId, leader);
    }
}
