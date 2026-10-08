namespace EAuction.Payments;

/// <summary>
/// Event names this service matches on. Declared as constants because the types
/// that produce them live in the services that own them, and the dependency points
/// the other way — the same arrangement the bid processor uses.
/// </summary>
public static class InboundEvents
{
    public const string AuctionApproved = "AuctionApproved";
    public const string BookletFeeRequested = "BookletFeeRequested";
    public const string DepositRequested = "DepositRequested";
    public const string DepositsReleasable = "DepositsReleasable";
    public const string AwardConfirmed = "AwardConfirmed";
}

// --- what this service reads -----------------------------------------------

public sealed record PaymentRequestPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public long AmountMinorUnits { get; init; }
    public string Method { get; init; } = "Payment";
}

public sealed record DepositsReleasablePayload
{
    public Guid AuctionId { get; init; }
    public Guid[]? ForfeitForBidders { get; init; }
    public Guid? AppliedToPurchaseForBidder { get; init; }

    /// <summary>A cancellation that keeps every deposit.</summary>
    public bool ForfeitAll { get; init; }

    /// <summary>A cancellation that returns the booklet fees as well.</summary>
    public bool RefundBooklets { get; init; }
}

public sealed record AwardConfirmedPayload
{
    public Guid AuctionId { get; init; }
    public Guid WinnerBidderId { get; init; }
    public long AmountMinorUnits { get; init; }
}

/// <summary>
/// Only the two fields this service needs off the public auction definition: what
/// percentage brokerage is, and nothing else.
/// </summary>
public sealed record AuctionTermsPayload
{
    public Guid AuctionId { get; init; }
    public decimal BrokerageFeePercent { get; init; }
}

// --- what this service writes ----------------------------------------------

/// <summary>
/// Money moved, on <c>payments.settlements</c>.
///
/// One event type for every outcome rather than four, because every consumer of
/// this topic cares about the same three questions — which bidder, what for, and
/// did it work — and a consumer that had to switch on four names to find out would
/// get a new case wrong the first time one was added.
/// </summary>
public sealed record PaymentSettled
{
    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }

    /// <summary>"Booklet", "Deposit" or "Brokerage".</summary>
    public required string Purpose { get; init; }

    /// <summary>"Charged", "Refused", "Refunded" or "Forfeited".</summary>
    public required string Outcome { get; init; }

    public required long AmountMinorUnits { get; init; }

    /// <summary>The gateway's reference, or empty when it refused.</summary>
    public required string Reference { get; init; }

    /// <summary>Why it was refused, in the gateway's own words. Null on success.</summary>
    public string? FailureReason { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Keyed by auction:bidder:purpose rather than by auction, because this topic
    /// is one bidder's payment history and compaction must never collapse a refund
    /// onto the charge it reverses.
    /// </summary>
    public string Key => $"{AuctionId}:{BidderId}:{Purpose}";
}

public static class PaymentOutcomes
{
    public const string Charged = "Charged";
    public const string Refused = "Refused";
    public const string Refunded = "Refunded";

    /// <summary>
    /// Kept rather than returned — the bidder defaulted. No money moves at this
    /// instant: the deposit was taken when it was paid, and forfeiting is a
    /// decision not to give it back.
    /// </summary>
    public const string Forfeited = "Forfeited";

    /// <summary>
    /// The winner's deposit, set against the price they now owe rather than
    /// refunded. Also no movement, and also not a refund — reporting them as one
    /// would overstate what was returned to bidders by the largest deposit in the
    /// auction.
    /// </summary>
    public const string AppliedToPurchase = "AppliedToPurchase";
}
