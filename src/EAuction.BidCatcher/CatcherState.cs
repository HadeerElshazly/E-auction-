using System.Collections.Concurrent;
using EAuction.Core;

namespace EAuction.BidCatcher;

/// <summary>
/// Everything the catcher needs to accept or reject a bid, held in memory.
///
/// There is deliberately no database here (D-12). This state is rebuilt by
/// replaying the compacted topics from offset 0 on startup, which takes
/// seconds at this size and is exactly what makes "even if the service is
/// down it can read it later" true: an auction approved while the catcher was
/// offline is still in the log when it comes back.
///
/// Every pod keeps a full copy via its own consumer group. Cheap: hundreds of
/// auctions, thousands of eligibility rows, one long per current price.
/// </summary>
public sealed class CatcherState(byte[] bidderMasterKey)
{
    private readonly ConcurrentDictionary<Guid, AuctionDefinition> _auctions = new();
    private readonly ConcurrentDictionary<Guid, long> _currentPrice = new();
    private readonly ConcurrentDictionary<(Guid Auction, Guid Bidder), byte[]> _eligibility = new();
    private readonly ConcurrentDictionary<(Guid Auction, Guid Bidder), TokenBucket> _rateLimits = new();

    /// <summary>
    /// The clerks running hall auctions, and their derived signing keys (§29).
    ///
    /// Separate from eligibility and keyed the same way, because a clerk is not a
    /// participant: they put down no deposit, win nothing, and are allowed to sign
    /// for somebody else. Collapsing the two dictionaries would make "may bid" and
    /// "may bid on another person's behalf" the same question.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid Auction, Guid Clerk), byte[]> _clerks = new();

    public TimeSpan CeilingGrace { get; init; } = TimeSpan.FromMinutes(1);
    public int MaxBidsPerSecondPerBidder { get; init; } = 20;

    public int AuctionCount => _auctions.Count;
    public int EligibilityCount => _eligibility.Count;
    public int ClerkCount => _clerks.Count;

    /// <summary>
    /// Auctions withdrawn before they opened. A set of its own rather than a removal
    /// from the definitions, because the definition replays from another topic and
    /// would put a removed auction straight back.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = new();

    /// <summary>Applied from <c>auctions.lifecycle</c>.</summary>
    public void CancelAuction(Guid auctionId) => _cancelled[auctionId] = 0;

    /// <summary>Applied from <c>auctions.upcoming</c> (compacted).</summary>
    public void UpsertAuction(AuctionDefinition auction) =>
        _auctions[auction.AuctionId] = auction;

    /// <summary>
    /// Applied from <c>auctions.participants</c> (compacted).
    ///
    /// The topic carries an epoch, not a secret. The signing key is derived
    /// here and kept in process memory only — it never reaches a topic or a
    /// disk. Deriving once at grant time rather than per bid keeps the hot
    /// path to the single HMAC that verifies the frame.
    /// </summary>
    public void GrantEligibility(Guid auctionId, Guid bidderId, int keyEpoch) =>
        _eligibility[(auctionId, bidderId)] =
            BidderKeys.Derive(bidderMasterKey, auctionId, bidderId, keyEpoch);

    /// <summary>Test and dev-seed hook: grants with a secret supplied directly.</summary>
    internal void GrantEligibilityWithSecret(Guid auctionId, Guid bidderId, byte[] signingSecret) =>
        _eligibility[(auctionId, bidderId)] = signingSecret;

    /// <summary>
    /// Reads the derived key without touching the rate limiter.
    ///
    /// Tests that wait for a topic to propagate need a signal they can poll;
    /// <see cref="Screen"/> is not one, because it takes a token every call
    /// and a polling loop drains the bucket before the condition can hold.
    /// </summary>
    internal bool TryGetSigningSecret(Guid auctionId, Guid bidderId, out byte[] secret) =>
        _eligibility.TryGetValue((auctionId, bidderId), out secret!);

    /// <summary>Reads the advisory price without touching the rate limiter.</summary>
    internal bool TryGetCurrentPrice(Guid auctionId, out long price) =>
        _currentPrice.TryGetValue(auctionId, out price);

    public void RevokeEligibility(Guid auctionId, Guid bidderId) =>
        _eligibility.TryRemove((auctionId, bidderId), out _);

    /// <summary>
    /// Applied from <c>auctions.participants</c>. Derived here from the master key,
    /// exactly as a bidder's is, so a clerk's signing key never travels either.
    /// </summary>
    public void AssignClerk(Guid auctionId, Guid clerkUserId, int keyEpoch) =>
        _clerks[(auctionId, clerkUserId)] =
            BidderKeys.Derive(bidderMasterKey, auctionId, clerkUserId, keyEpoch);

    public void UnassignClerk(Guid auctionId, Guid clerkUserId) =>
        _clerks.TryRemove((auctionId, clerkUserId), out _);

    /// <summary>Test and dev hook, mirroring the eligibility one.</summary>
    internal void AssignClerkWithSecret(Guid auctionId, Guid clerkUserId, byte[] secret) =>
        _clerks[(auctionId, clerkUserId)] = secret;

    internal bool TryGetClerkSecret(Guid auctionId, Guid clerkUserId, out byte[] secret) =>
        _clerks.TryGetValue((auctionId, clerkUserId), out secret!);

    /// <summary>
    /// Whether this auction is run from the hall. Null when the auction is unknown,
    /// which the caller must treat as "refuse", not as "online".
    /// </summary>
    public bool? IsOnsite(Guid auctionId) =>
        _auctions.TryGetValue(auctionId, out var auction)
            ? auction.Channel == BidChannel.Onsite
            : null;

    public bool IsClerkFor(Guid auctionId, Guid userId) =>
        _clerks.ContainsKey((auctionId, userId));

    /// <summary>Applied from <c>auctions.current-winner</c> (compacted).</summary>
    public void UpdateCurrentPrice(Guid auctionId, long price) =>
        _currentPrice[auctionId] = price;

    /// <summary>
    /// Auctions taking bids now or about to: from shortly before the start to well
    /// after the scheduled end, since late bids extend it.
    /// </summary>
    public IEnumerable<Guid> AuctionsOpenAround(DateTimeOffset now) =>
        _auctions.Values
            .Where(a => !_cancelled.ContainsKey(a.AuctionId)
                && a.StartsAt - TimeSpan.FromMinutes(10) <= now
                && now <= a.EndsAt + TimeSpan.FromHours(1))
            .Select(a => a.AuctionId);

    public bool TryGetAuction(Guid id, out AuctionDefinition auction) =>
        _auctions.TryGetValue(id, out auction!);

    /// <summary>
    /// Screens a bid using only in-memory state — no I/O, no outbound call.
    ///
    /// The price check here is deliberately advisory: this view is
    /// milliseconds stale, so it exists to give instant feedback on obviously
    /// too-low bids and keep junk out of the log. The processor stays
    /// authoritative (§7.1 step 5).
    /// </summary>
    /// <param name="clerk">
    /// The clerk entering this bid for a hall auction, or null when the bidder is
    /// bidding for themselves. It decides whose key must have signed the frame.
    /// </param>
    public RejectionReason Screen(ReadOnlySpan<byte> frame, DateTimeOffset now, Guid? clerk = null)
    {
        var auctionId = BidFrame.AuctionId(frame);
        if (!_auctions.TryGetValue(auctionId, out var auction))
            return RejectionReason.UnknownAuction;

        // A cancelled auction has no window at all.
        if (_cancelled.ContainsKey(auctionId)) return RejectionReason.OutsideWindow;

        var onsite = auction.Channel == BidChannel.Onsite;

        // A hall auction has no upper bound here, because the end time it would be
        // checked against is the clerk's to move and this service does not follow
        // the lifecycle topic. The processor's engine holds the real cutoff and
        // refuses a late bid as AuctionClosed — which is the same arrangement the
        // price floor already has, advisory here and authoritative there (§6.3).
        if (now < auction.StartsAt) return RejectionReason.OutsideWindow;
        if (!onsite && now > auction.HardCeiling(CeilingGrace))
            return RejectionReason.OutsideWindow;

        var bidderId = BidFrame.BidderId(frame);

        // The bidder must be eligible on both channels: standing in the hall does
        // not pay a deposit, and the clerk is not entitled to bid for someone who
        // has not qualified.
        if (!_eligibility.TryGetValue((auctionId, bidderId), out var bidderSecret))
            return RejectionReason.NotEligible;

        byte[] signingSecret;
        if (clerk is { } clerkId)
        {
            if (!onsite) return RejectionReason.NotTheClerk;
            if (!_clerks.TryGetValue((auctionId, clerkId), out var clerkSecret))
                return RejectionReason.NotTheClerk;

            signingSecret = clerkSecret;
        }
        else
        {
            // An onsite auction takes no bid a clerk did not enter. Otherwise a
            // bidder registered for a hall auction could bid from their phone and
            // the room would not know.
            if (onsite) return RejectionReason.NotTheClerk;
            signingSecret = bidderSecret;
        }

        if (!BidFrame.VerifySignature(frame, signingSecret))
            return RejectionReason.BadSignature;

        // Rate-limited per the party actually making the requests: a clerk enters
        // for the whole room, so limiting them per bidder would let one terminal
        // make twenty calls a second for each of fifty bidders.
        var limitKey = clerk is { } who ? (auctionId, who) : (auctionId, bidderId);
        var bucket = _rateLimits.GetOrAdd(
            limitKey, _ => new TokenBucket(MaxBidsPerSecondPerBidder));
        if (!bucket.TryTake(now))
            return RejectionReason.RateLimited;

        // The floor is the opening price until the processor has judged a bid and
        // published a winner. Without the else branch there is NO floor at all before
        // the first verdict: a 1-halala bid on a million-riyal auction is accepted
        // into the append-only ledger that is the legal record, and the only thing
        // stopping a flood of them is the per-bidder rate limit. The opening price is
        // in this service's own state and costs nothing to check.
        if (_currentPrice.TryGetValue(auctionId, out var price))
        {
            var required = price + auction.Increment.MinimumRaise(price);
            if (BidFrame.Amount(frame) < required)
                return RejectionReason.BelowMinimumIncrement;
        }
        else if (BidFrame.Amount(frame) < auction.OpeningPriceMinorUnits)
        {
            return RejectionReason.BelowOpeningPrice;
        }

        return RejectionReason.None;
    }
}

/// <summary>Per-bidder token bucket. Rate limiting by identity, not by IP: behind
/// carrier NAT an IP limit punishes a whole city, and bidders are Nafath-verified
/// so their identity is reliable.</summary>
public sealed class TokenBucket(int perSecond)
{
    private readonly object _gate = new();
    private double _tokens = perSecond;
    private long _lastTicks = DateTimeOffset.UtcNow.UtcTicks;

    public bool TryTake(DateTimeOffset now)
    {
        lock (_gate)
        {
            // Clamped at zero because time can move backwards: an NTP step, a
            // VM clock correction, or simply two requests whose timestamps are
            // taken out of order. Unclamped, a backwards step subtracts
            // tokens instead of adding them and locks the bidder out of their
            // own auction for no reason — the worst possible moment for a
            // refill to go the wrong way.
            var elapsed = Math.Max(0, (now.UtcTicks - _lastTicks) / (double)TimeSpan.TicksPerSecond);

            // Likewise never let the clock rewind, or the next legitimate
            // refill would be measured from the future.
            if (now.UtcTicks > _lastTicks) _lastTicks = now.UtcTicks;

            _tokens = Math.Min(perSecond, _tokens + elapsed * perSecond);
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
