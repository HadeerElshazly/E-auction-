namespace EAuction.Core;

/// <summary>
/// The auction as published to <c>auctions.upcoming</c> when the admin
/// workflow approves it. This is what the bid catcher materialises in memory;
/// it never reads a database (D-12).
///
/// An auction sells 1..N plots as one indivisible package, and bidding is on
/// the package — auctionId is the bidding key (D-02).
/// </summary>
public sealed record AuctionDefinition
{
    public required Guid AuctionId { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public required long OpeningPriceMinorUnits { get; init; }

    /// <summary>
    /// السعر الاحتياطي. Secret: never leaves the processor, never appears in a
    /// public API response or report, and is not revealed even when an auction
    /// fails to meet it (D-06).
    /// </summary>
    public required long ReservePriceMinorUnits { get; init; }

    public required IncrementPolicy Increment { get; init; }

    /// <summary>Quiet period length. Null disables extension entirely (D-04).</summary>
    public TimeSpan? QuietPeriod { get; init; }

    public int MaxExtensions { get; init; } = 3;

    public BidChannel Channel { get; init; } = BidChannel.Online;

    /// <summary>
    /// Deferred blind final round (§6.4). Nullable and unused — present from
    /// the first migration so enabling it later is additive rather than a
    /// migration against live and already-awarded auctions.
    /// </summary>
    public bool? BlindRoundEnabled { get; init; }
    public TimeSpan? BlindDuration { get; init; }
    public int? MaxBlindRounds { get; init; }

    /// <summary>
    /// The window the catcher gates on — deliberately permissive (§6.3).
    ///
    /// The processor decides extensions; the catcher enforces the window. If
    /// the catcher used the live end time, a bid arriving just after the
    /// original end would be rejected before the catcher learned of the
    /// extension. Gating on the ceiling removes that race: the catcher accepts
    /// generously and the processor, which sees the fully ordered stream,
    /// remains authoritative on the exact cutoff.
    /// </summary>
    public DateTimeOffset HardCeiling(TimeSpan grace)
    {
        var ceiling = EndsAt;
        if (QuietPeriod is { } q)
            ceiling += q * MaxExtensions;

        // Blind rounds are deferred, but the formula accounts for them so the
        // ceiling grows correctly the day they are enabled.
        if (BlindRoundEnabled == true && BlindDuration is { } b && MaxBlindRounds is { } n)
            ceiling += b * n;

        return ceiling + grace;
    }
}

/// <summary>Minimum raise over the current price.</summary>
public abstract record IncrementPolicy
{
    public abstract long MinimumRaise(long currentPriceMinorUnits);

    public sealed record Fixed(long AmountMinorUnits) : IncrementPolicy
    {
        public override long MinimumRaise(long currentPriceMinorUnits) => AmountMinorUnits;
    }

    /// <summary>
    /// Bands ordered by ascending threshold; the raise for a price is the
    /// amount of the highest band whose threshold it has reached.
    /// </summary>
    public sealed record Tiered(IReadOnlyList<(long Threshold, long AmountMinorUnits)> Bands)
        : IncrementPolicy
    {
        public override long MinimumRaise(long currentPriceMinorUnits)
        {
            var raise = Bands[0].AmountMinorUnits;
            foreach (var (threshold, amount) in Bands)
            {
                if (currentPriceMinorUnits < threshold) break;
                raise = amount;
            }
            return raise;
        }
    }
}
