namespace EAuction.BidProcessor;

// Inbound payloads. These are the processor's own view of the auction-admin
// contract, deliberately redeclared rather than shared: a consumer owning its
// own view is what lets the producer add fields without recompiling everyone,
// and it keeps the dependency pointing the right way.

public sealed record AuctionApprovedPayload
{
    public Guid AuctionId { get; init; }
    public string NameAr { get; init; } = "";
    public string NameEn { get; init; } = "";
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public long OpeningPriceMinorUnits { get; init; }
    public long MinIncrementMinorUnits { get; init; }
    public long DepositMinorUnits { get; init; }
    public int? QuietPeriodSeconds { get; init; }
    public int MaxExtensions { get; init; }
    public string Channel { get; init; } = "Online";
}

public sealed record AuctionReserveSetPayload
{
    public Guid AuctionId { get; init; }
    public long ReservePriceMinorUnits { get; init; }
}

/// <summary>
/// Event type names produced by auction-admin. Declared as constants because
/// the producing types live in that service and are not referenced here — the
/// dependency points the other way, so these cannot come from `nameof`.
/// </summary>
public static class InboundEvents
{
    public const string AuctionApproved = "AuctionApproved";
    public const string AuctionReserveSet = "AuctionReserveSet";
    public const string WinnerDisqualified = "WinnerDisqualified";

    /// <summary>The clerk running a hall auction moved its end time (§29).</summary>
    public const string AuctionExtendedByClerk = "AuctionExtendedByClerk";

    /// <summary>The clerk brought the hammer down. The only way an onsite auction ends.</summary>
    public const string AuctionClosedByClerk = "AuctionClosedByClerk";
}

/// <summary>
/// A clerk's command from the hall. Both carry who issued it, because an onsite
/// auction's record has to say which person ended it and which person extended it —
/// in an online auction a clock did, and nobody has to be named.
/// </summary>
public sealed record ClerkCommandPayload
{
    public Guid AuctionId { get; init; }
    public Guid ClerkUserId { get; init; }

    /// <summary>Extension only. Ignored on a close.</summary>
    public int ExtendBySeconds { get; init; }
}

public sealed record WinnerDisqualifiedPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public string Reason { get; init; } = "";
}

// Outbound events, onto auctions.lifecycle.

public sealed record AuctionStarted
{
    public Guid AuctionId { get; init; }
    public DateTimeOffset At { get; init; }
}

public sealed record AuctionClosed
{
    public Guid AuctionId { get; init; }
    public DateTimeOffset At { get; init; }

    /// <summary>The end time after any quiet-period extensions.</summary>
    public DateTimeOffset EffectiveEndsAt { get; init; }

    public int ExtensionsUsed { get; init; }
    public int BidCount { get; init; }
}

/// <summary>
/// The highest remaining bidder that clears the reserve, offered to the
/// committee. Carries no reserve price — it says someone qualifies, not what
/// they had to beat (D-06).
/// </summary>
public sealed record CandidateOffered
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public long AmountMinorUnits { get; init; }
    public int CascadeStep { get; init; }
}

/// <summary>
/// Nobody is left who clears the reserve. The auction is unsold — the reason
/// is not disclosed, for the same reason the reserve is not.
/// </summary>
public sealed record LadderExhausted
{
    public Guid AuctionId { get; init; }
    public int CascadeStep { get; init; }
}

public sealed record CurrentWinner
{
    public Guid AuctionId { get; init; }
    public long PriceMinorUnits { get; init; }

    /// <summary>Masked in public views (D-22); the raw id stays on this restricted topic.</summary>
    public Guid? LeaderBidderId { get; init; }

    /// <summary>
    /// The bid that took the lead, as the bidder's own client identified it.
    ///
    /// Carried so a bidder can be told which of their bids won rather than inferring
    /// it from a price that happens to match. Masked exactly like
    /// <see cref="LeaderBidderId"/>: it is one bidder's identifier and the fan-out
    /// sends it only to them.
    /// </summary>
    public Guid? LeaderClientBidId { get; init; }

    public DateTimeOffset EffectiveEndsAt { get; init; }
    public int ExtensionsUsed { get; init; }
}

public sealed record BidRejected
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public Guid ClientBidId { get; init; }
    public string Reason { get; init; } = "";
    public long CurrentPriceMinorUnits { get; init; }
}
