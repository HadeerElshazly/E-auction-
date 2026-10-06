namespace EAuction.Notifications.Integration;

/// <summary>
/// Event names this service matches on. Constants rather than shared types,
/// because the types live in the services that own them and the dependency points
/// the other way — the same arrangement the bid processor and the payment service
/// use.
/// </summary>
public static class InboundEvents
{
    public const string AuctionApproved = "AuctionApproved";
    public const string ParticipantEligibilityChanged = "ParticipantEligibilityChanged";
    public const string PaymentSettled = "PaymentSettled";
    public const string AuctionStarted = "AuctionStarted";
    public const string AuctionClosed = "AuctionClosed";
    public const string CurrentWinner = "CurrentWinner";
    public const string AwardConfirmed = "AwardConfirmed";
    public const string WinnerDisqualified = "WinnerDisqualified";
    public const string DepositsReleasable = "DepositsReleasable";
}

// This service's own view of other services' contracts — only the fields it acts
// on. A shared type would make every one of those services a build-time
// prerequisite for this one.

public sealed record AuctionApprovedPayload
{
    public Guid AuctionId { get; init; }
    public string NameAr { get; init; } = "";
}

public sealed record EligibilityPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public bool Eligible { get; init; }
}

public sealed record SettlementPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public string Purpose { get; init; } = "";
    public string Outcome { get; init; } = "";
    public long AmountMinorUnits { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset At { get; init; }
}

public sealed record AuctionIdPayload
{
    public Guid AuctionId { get; init; }
}

public sealed record CurrentWinnerPayload
{
    public Guid AuctionId { get; init; }
    public long PriceMinorUnits { get; init; }

    /// <summary>
    /// Who leads. This topic is restricted and carries the raw id; D-22 governs what
    /// public views may show, and this service shows one bidder their own position
    /// and nobody else's.
    /// </summary>
    public Guid? LeaderBidderId { get; init; }
}

public sealed record AwardConfirmedPayload
{
    public Guid AuctionId { get; init; }
    public Guid WinnerBidderId { get; init; }
    public long AmountMinorUnits { get; init; }
    public DateTimeOffset ComplianceDeadline { get; init; }
}

public sealed record WinnerDisqualifiedPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public string Reason { get; init; } = "";
    public bool DepositForfeited { get; init; }
}

public sealed record DepositsReleasablePayload
{
    public Guid AuctionId { get; init; }
    public Guid[]? ForfeitForBidders { get; init; }
    public Guid? AppliedToPurchaseForBidder { get; init; }
}
