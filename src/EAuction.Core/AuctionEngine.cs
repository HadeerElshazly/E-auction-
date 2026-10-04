namespace EAuction.Core;

/// <summary>
/// Authoritative winner determination for one auction.
///
/// Bids are applied strictly in log offset order, which is the total order for
/// the auction (D-03). Because of that, ties resolve themselves (D-05): two
/// identical amounts are applied in offset order, and the second fails the
/// minimum-increment check. No separate tie-break rule is needed.
///
/// The engine is single-threaded by contract — one instance per auction, fed
/// by one consumer.
/// </summary>
public sealed class AuctionEngine
{
    private readonly AuctionDefinition _auction;
    private readonly LedgerChain _ledger = new();
    private readonly HashSet<Guid> _seenBidIds = new();
    private readonly List<LadderEntry> _ladder = new();

    public AuctionEngine(AuctionDefinition auction)
    {
        _auction = auction;
        CurrentPrice = auction.OpeningPriceMinorUnits;
        EffectiveEndsAt = auction.EndsAt;
    }

    /// <summary>Highest accepted bid, or the opening price if none yet.</summary>
    public long CurrentPrice { get; private set; }

    /// <summary>
    /// Provisional leader only. The committee awards, not the system (D-08) —
    /// this never becomes an awarded winner without the ترسية workflow.
    /// </summary>
    public Guid? ProvisionalLeader { get; private set; }

    public DateTimeOffset EffectiveEndsAt { get; private set; }
    public int ExtensionsUsed { get; private set; }
    public bool HasAnyBid => _ladder.Count > 0;
    public ReadOnlySpan<byte> LedgerHead => _ledger.Head;

    /// <summary>
    /// The full ranked list, highest first, ties broken by earliest offset.
    /// Persisted permanently: without it there is nothing for the reserve
    /// cascade to walk down (§8.1).
    /// </summary>
    public IReadOnlyList<LadderEntry> Ladder =>
        _ladder.OrderByDescending(e => e.AmountMinorUnits)
               .ThenBy(e => e.Offset)
               .ToList();

    /// <summary>Did the auction clear its reserve? Never exposed publicly (D-06).</summary>
    public bool ReserveMet => CurrentPrice >= _auction.ReservePriceMinorUnits;

    public BidVerdict Apply(long offset, ReadOnlySpan<byte> frame)
    {
        var bidderId = BidFrame.BidderId(frame);
        var clientBidId = BidFrame.ClientBidId(frame);
        var amount = BidFrame.Amount(frame);
        var serverTs = DateTimeOffset.FromUnixTimeMilliseconds(BidFrame.ServerTimestamp(frame));

        // Every frame that reached the log is chained, accepted or not: the
        // ledger records what was received, not only what won.
        _ledger.Append(frame);

        BidVerdict Reject(RejectionReason reason) => new(
            _auction.AuctionId, bidderId, clientBidId, offset,
            false, reason, CurrentPrice, EffectiveEndsAt, ExtensionsUsed);

        // Idempotency: mobile networks drop mid-request and the client retries
        // with the same clientBidId. The retry must not become a second bid.
        if (!_seenBidIds.Add(clientBidId))
            return Reject(RejectionReason.DuplicateBidId);

        if (serverTs < _auction.StartsAt)
            return Reject(RejectionReason.OutsideWindow);

        // Authoritative cutoff: the live end time, not the catcher's ceiling.
        if (serverTs > EffectiveEndsAt)
            return Reject(RejectionReason.AuctionClosed);

        // A leader raising their own bid is rejected (B-04): it is almost
        // always a double-click, and it costs the bidder money for nothing.
        if (ProvisionalLeader == bidderId)
            return Reject(RejectionReason.SelfOutbid);

        var required = HasAnyBid
            ? CurrentPrice + _auction.Increment.MinimumRaise(CurrentPrice)
            : _auction.OpeningPriceMinorUnits;

        if (amount < required)
            return Reject(RejectionReason.BelowMinimumIncrement);

        CurrentPrice = amount;
        ProvisionalLeader = bidderId;
        _ladder.Add(new LadderEntry(bidderId, amount, offset, serverTs));

        ApplyQuietPeriodExtension(serverTs);

        return new BidVerdict(
            _auction.AuctionId, bidderId, clientBidId, offset,
            true, RejectionReason.None, CurrentPrice, EffectiveEndsAt, ExtensionsUsed);
    }

    /// <summary>
    /// A bid inside the last Q extends the end by Q, up to MaxExtensions (D-04).
    /// Anti-sniping: it removes most last-second timing disputes.
    /// </summary>
    private void ApplyQuietPeriodExtension(DateTimeOffset bidTime)
    {
        if (_auction.QuietPeriod is not { } quiet) return;
        if (ExtensionsUsed >= _auction.MaxExtensions) return;
        if (bidTime < EffectiveEndsAt - quiet) return;

        EffectiveEndsAt += quiet;
        ExtensionsUsed++;
    }

    /// <summary>
    /// Walks the ladder for the reserve cascade (§8.2). Returns bidders in
    /// award order, skipping those already disqualified, and stops at the
    /// first bid below reserve — comparison is &gt;= ("مش أقل من الاحتياطي").
    /// </summary>
    public IEnumerable<LadderEntry> CascadeCandidates(IReadOnlySet<Guid> disqualified)
    {
        var seen = new HashSet<Guid>();
        foreach (var entry in Ladder)
        {
            if (entry.AmountMinorUnits < _auction.ReservePriceMinorUnits) yield break;
            if (disqualified.Contains(entry.BidderId)) continue;
            if (!seen.Add(entry.BidderId)) continue;   // a bidder's best bid only
            yield return entry;
        }
    }
}

public readonly record struct LadderEntry(
    Guid BidderId,
    long AmountMinorUnits,
    long Offset,
    DateTimeOffset AcceptedAt);
