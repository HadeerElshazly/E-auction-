namespace EAuction.Reporting.Integration;

/// <summary>
/// Event names this service matches on.
///
/// Constants rather than <c>nameof</c>, because the types that produce them live in
/// the services that own them and the dependency points the other way — the same
/// arrangement the bid processor and the payment service use.
/// </summary>
public static class InboundEvents
{
    public const string AuctionApproved = "AuctionApproved";
    public const string AuctionRejected = "AuctionRejected";
    public const string AuctionStarted = "AuctionStarted";
    public const string AuctionClosed = "AuctionClosed";
    public const string CandidateOffered = "CandidateOffered";
    public const string AwardConfirmed = "AwardConfirmed";
    public const string WinnerDisqualified = "WinnerDisqualified";
    public const string AuctionUnsold = "AuctionUnsold";
    public const string LadderExhausted = "LadderExhausted";
    public const string AuctionSettled = "AuctionSettled";
    public const string AuctionExtendedByClerk = "AuctionExtendedByClerk";
    public const string ParticipantEligibilityChanged = "ParticipantEligibilityChanged";
    public const string PaymentSettled = "PaymentSettled";

    /// <summary>
    /// Deliberately absent: <c>AuctionReserveSet</c>.
    ///
    /// This service does not consume <c>auctions.sealed</c> and must not be given
    /// its ACL (D-45). The reserve is the one figure in the platform whose secrecy
    /// is the whole point (D-06, D-23), and a reporting service is precisely where
    /// a secret stops being one: its output is spreadsheets that get emailed. The
    /// cost is stated in §35 — a report can say an auction was unsold, and cannot
    /// say by how much it missed.
    /// </summary>
    public const string NotConsumed_AuctionReserveSet = "AuctionReserveSet";
}

// --- the payloads, as this service reads them ------------------------------
//
// Each is this service's own view of another service's contract, redeclared
// rather than shared: a consumer owning its own view is what lets a producer add
// fields without recompiling everyone.

public sealed record PlotPayload
{
    public Guid Id { get; init; }
    public string DeedNumber { get; init; } = "";
    public decimal AreaSqm { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }
    public string? DescriptionAr { get; init; }
    public string? DescriptionEn { get; init; }
}

public sealed record AuctionApprovedPayload
{
    public Guid AuctionId { get; init; }
    public string NameAr { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string? Phase { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public long OpeningPriceMinorUnits { get; init; }
    public long MinIncrementMinorUnits { get; init; }
    public long DepositMinorUnits { get; init; }
    public long BookletPriceMinorUnits { get; init; }
    public decimal BrokerageFeePercent { get; init; }
    public string Channel { get; init; } = "Online";
    public string BidderVisibility { get; init; } = "Masked";
    public int PlotCount { get; init; }
    public decimal TotalAreaSqm { get; init; }
    public IReadOnlyList<PlotPayload> Plots { get; init; } = [];
}

public sealed record AuctionRejectedPayload
{
    public Guid AuctionId { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record AuctionStartedPayload
{
    public Guid AuctionId { get; init; }
    public DateTimeOffset At { get; init; }
}

public sealed record AuctionClosedPayload
{
    public Guid AuctionId { get; init; }
    public DateTimeOffset At { get; init; }
    public DateTimeOffset EffectiveEndsAt { get; init; }
    public int ExtensionsUsed { get; init; }
    public int BidCount { get; init; }
}

public sealed record CandidateOfferedPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public long AmountMinorUnits { get; init; }
    public int CascadeStep { get; init; }
}

public sealed record AwardConfirmedPayload
{
    public Guid AuctionId { get; init; }
    public Guid WinnerBidderId { get; init; }
    public long AmountMinorUnits { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; }
    public DateTimeOffset ComplianceDeadline { get; init; }
    public int CascadeStep { get; init; }
}

public sealed record WinnerDisqualifiedPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public string Reason { get; init; } = "";
    public bool DepositForfeited { get; init; }
}

public sealed record AuctionSettledPayload
{
    public Guid AuctionId { get; init; }
    public Guid WinnerBidderId { get; init; }
    public long AmountMinorUnits { get; init; }
    public DateTimeOffset At { get; init; }
}

public sealed record AuctionIdPayload
{
    public Guid AuctionId { get; init; }
}

public sealed record EligibilityPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public bool Eligible { get; init; }

    /// <summary>Null on a masked auction — never put on the topic at all (D-22).</summary>
    public string? DisplayNameAr { get; init; }
}

public sealed record PaymentSettledPayload
{
    public Guid AuctionId { get; init; }
    public Guid BidderId { get; init; }
    public string Purpose { get; init; } = "";
    public string Outcome { get; init; } = "";
    public long AmountMinorUnits { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset At { get; init; }
}
