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

    public TimeSpan CeilingGrace { get; init; } = TimeSpan.FromMinutes(1);
    public int MaxBidsPerSecondPerBidder { get; init; } = 20;

    public int AuctionCount => _auctions.Count;
    public int EligibilityCount => _eligibility.Count;

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

    /// <summary>Applied from <c>auctions.current-winner</c> (compacted).</summary>
    public void UpdateCurrentPrice(Guid auctionId, long price) =>
        _currentPrice[auctionId] = price;

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
    public RejectionReason Screen(ReadOnlySpan<byte> frame, DateTimeOffset now)
    {
        var auctionId = BidFrame.AuctionId(frame);
        if (!_auctions.TryGetValue(auctionId, out var auction))
            return RejectionReason.UnknownAuction;

        // Gated on the permissive ceiling, never on the live end time (§6.3).
        if (now < auction.StartsAt || now > auction.HardCeiling(CeilingGrace))
            return RejectionReason.OutsideWindow;

        var bidderId = BidFrame.BidderId(frame);
        if (!_eligibility.TryGetValue((auctionId, bidderId), out var secret))
            return RejectionReason.NotEligible;

        if (!BidFrame.VerifySignature(frame, secret))
            return RejectionReason.BadSignature;

        var bucket = _rateLimits.GetOrAdd(
            (auctionId, bidderId), _ => new TokenBucket(MaxBidsPerSecondPerBidder));
        if (!bucket.TryTake(now))
            return RejectionReason.RateLimited;

        if (_currentPrice.TryGetValue(auctionId, out var price))
        {
            var required = price + auction.Increment.MinimumRaise(price);
            if (BidFrame.Amount(frame) < required)
                return RejectionReason.BelowMinimumIncrement;
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
