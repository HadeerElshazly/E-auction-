using System.Text.Json.Serialization;
using EAuction.Outbox;

namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// Something the rest of the system needs to know about. Raised by the
/// aggregate and turned into outbox rows inside the same transaction as the
/// state change (D-16) — so the event cannot exist without the state change,
/// and the state change cannot exist without the event.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    /// <summary>
    /// Debezium EventRouter routes on this. It is a routing detail that lives
    /// in the outbox column, so it is kept out of the payload — consumers
    /// should not see our topic-selection mechanics in the event contract.
    /// </summary>
    [JsonIgnore]
    public abstract string AggregateType { get; }

    /// <summary>The message key. Also a column, also not part of the payload.</summary>
    [JsonIgnore]
    public abstract string AggregateId { get; }
}

/// <summary>
/// Public-safe auction definition for <c>auctions.upcoming</c>.
///
/// Deliberately carries NO reserve price. The catcher and the query BFF both
/// consume this topic, and the BFF feeds public APIs — so a misconfigured BFF
/// cannot leak the reserve, because it never receives it (D-06).
/// </summary>
/// <summary>
/// A plot as the public catalogue sees it. A separate shape from the Plot entity on
/// purpose: an event that serialised the entity would carry whatever is added to it
/// later, which is how internal fields end up on a public topic by accident.
/// </summary>
public sealed record PublicPlot(
    Guid Id, string DeedNumber, decimal AreaSqm,
    string? Latitude, string? Longitude,
    string? DescriptionAr, string? DescriptionEn);

public sealed record AuctionApproved : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required string NameAr { get; init; }
    public required string NameEn { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public required long OpeningPriceMinorUnits { get; init; }
    public required long MinIncrementMinorUnits { get; init; }
    public required long DepositMinorUnits { get; init; }
    public required long BookletPriceMinorUnits { get; init; }
    public int? QuietPeriodSeconds { get; init; }
    public required int MaxExtensions { get; init; }
    public required string Channel { get; init; }

    /// <summary>
    /// "Masked" or "Named" (D-22). On the public topic because the read path has to
    /// know which label it may build, and because a bidder deciding whether to
    /// register is entitled to know whether their name will be shown.
    /// </summary>
    public required string BidderVisibility { get; init; }

    public required int PlotCount { get; init; }
    public required decimal TotalAreaSqm { get; init; }

    /// <summary>
    /// The plots themselves, not just the count. A bidder deciding whether to put
    /// down a deposit needs to know which land is in the package — deed numbers,
    /// areas and where it is. D-23 restricts the reserve price and nothing else, so
    /// this belongs on the public topic.
    /// </summary>
    public required IReadOnlyList<PublicPlot> Plots { get; init; }

    public override string AggregateType => "auction-upcoming";
    public override string AggregateId => AuctionId.ToString();
}

/// <summary>
/// The reserve price, on its own restricted topic (<c>auctions.sealed</c>)
/// read only by the bid processor.
///
/// Splitting this out of <see cref="AuctionApproved"/> is what makes "the
/// reserve never leaves the processor" enforceable by ACL rather than by
/// everyone remembering not to serialise a field.
/// </summary>
public sealed record AuctionReserveSet : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required long ReservePriceMinorUnits { get; init; }

    public override string AggregateType => "auction-sealed";
    public override string AggregateId => AuctionId.ToString();
}

public sealed record AuctionRejected : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required string Reason { get; init; }

    public override string AggregateType => "auction-lifecycle";
    public override string AggregateId => AuctionId.ToString();
}

public sealed record AwardConfirmed : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid AwardId { get; init; }
    public required Guid WinnerBidderId { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required DateTimeOffset ComplianceDeadline { get; init; }
    public required int CascadeStep { get; init; }

    public override string AggregateType => "auction-lifecycle";
    public override string AggregateId => AuctionId.ToString();
}

public sealed record WinnerDisqualified : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid AwardId { get; init; }
    public required Guid BidderId { get; init; }
    public required string Reason { get; init; }
    public required bool DepositForfeited { get; init; }

    public override string AggregateType => "auction-lifecycle";
    public override string AggregateId => AuctionId.ToString();
}

/// <summary>
/// No remaining bidder clears the reserve. Carries no reserve value: it says
/// the auction failed, not what it failed to reach (D-06).
/// </summary>
public sealed record AuctionUnsold : DomainEvent
{
    public required Guid AuctionId { get; init; }

    public override string AggregateType => "auction-lifecycle";
    public override string AggregateId => AuctionId.ToString();
}

/// <summary>
/// The award is final, so held deposits can now be resolved.
///
/// NOT emitted when bidding closes. While the cascade can still reach a losing
/// bidder, their deposit has to be held through the compliance window of
/// everyone above them — releasing at the gavel destroys the ability to
/// cascade (§8.3).
///
/// <see cref="ForfeitForBidders"/> lists the disqualified. Every other deposit
/// still held for this auction is released.
/// </summary>
public sealed record DepositsReleasable : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid[] ForfeitForBidders { get; init; }

    /// <summary>The settled winner, whose deposit is applied to the price rather than returned.</summary>
    public Guid? AppliedToPurchaseForBidder { get; init; }

    public override string AggregateType => "auction-deposits";
    public override string AggregateId => AuctionId.ToString();
}
