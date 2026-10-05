using System.Collections.Concurrent;

namespace EAuction.QueryBff;

/// <summary>
/// Per-auction pseudonyms for bidders (D-22): the public view shows
/// <c>مزايد #4</c>, never a bidder id or a name.
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
                return $"#{ordinal}";
            }
        }
    }
}
