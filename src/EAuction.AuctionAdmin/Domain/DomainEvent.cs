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

    /// <summary>
    /// نسبة السعي — the brokerage percentage, charged to the winner on the price
    /// they won at.
    ///
    /// On the public topic because a bidder deciding what to bid needs to know what
    /// the sale will cost them on top, and because the payment service computes the
    /// fee from it. Not a reserve price: D-23 restricts what the auction is worth to
    /// the municipality, not the published terms of sale.
    /// </summary>
    public required decimal BrokerageFeePercent { get; init; }

    public int? QuietPeriodSeconds { get; init; }
    public required int MaxExtensions { get; init; }
    public required string Channel { get; init; }

    /// <summary>
    /// "Masked" or "Named" (D-22). On the public topic because the read path has to
    /// know which label it may build, and because a bidder deciding whether to
    /// register is entitled to know whether their name will be shown.
    /// </summary>
    public required string BidderVisibility { get; init; }

    /// <summary>
    /// كراسة الشروط, in the document service.
    ///
    /// On the public topic because the services that decide who may read it need
    /// to know which document it is: the participant service mints a grant for a
    /// bidder who has paid, and it learns the id here. The id alone discloses
    /// nothing — the document is Restricted and opens only to a grant.
    /// </summary>
    public Guid? BookletDocumentId { get; init; }

    /// <summary>The cover image, which is Public: it is on the catalogue an anonymous citizen reads.</summary>
    public Guid? CoverImageDocumentId { get; init; }

    /// <summary>
    /// المخطط — which plan and phase this land belongs to, e.g. "مخطط السعيد — المرحلة الأولى".
    ///
    /// Added for the reporting service (§35), which groups almost everything by it:
    /// a municipality asks what a *phase* raised and how much of it is left, not
    /// what one auction did. Public information — it says where the land is, which
    /// the deed numbers and coordinates on this same event already say — and it
    /// answers P-1, the open question of whether the plan holds 327 plots or 372.
    /// </summary>
    public string? Phase { get; init; }

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

    /// <summary>
    /// When the committee confirmed it.
    ///
    /// Added for reporting (§35), which had been deriving the award date from
    /// <see cref="ComplianceDeadline"/> — a figure that is the deadline minus a
    /// window the consumer does not know. The committee's confirmation is a legal
    /// act with a date, and the date belongs on the event that announces it.
    /// </summary>
    public required DateTimeOffset ConfirmedAt { get; init; }

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
/// The sale completed: البيع تم. On <c>auctions.lifecycle</c>.
///
/// Nothing published this before, which was a gap rather than a decision. Settling
/// an auction already emitted <see cref="DepositsReleasable"/> with the winner in
/// <c>AppliedToPurchaseForBidder</c>, so a consumer could infer the settlement from
/// a deposit event — and the reporting service did, until it was clear that
/// "the sale completed" is a lifecycle fact and inferring it from money moving is
/// how a report comes to disagree with the register.
/// </summary>
public sealed record AuctionSettled : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid WinnerBidderId { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required DateTimeOffset At { get; init; }

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


/// <summary>
/// A clerk moving their own auction, on <c>auctions.lifecycle</c> (§29).
///
/// Two records rather than one with a verb field, because the outbox names an
/// event after its type and the processor matches on that name. A single type
/// would have had to smuggle the verb past the serialiser, which is a lot of
/// cleverness to avoid writing a second record.
/// </summary>
public sealed record AuctionExtendedByClerk : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid ClerkUserId { get; init; }
    public required int ExtendBySeconds { get; init; }

    public override string AggregateType => "auction-lifecycle";
    public override string AggregateId => AuctionId.ToString();
}

/// <summary>The hammer. The only thing that ends a hall auction.</summary>
public sealed record AuctionClosedByClerk : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid ClerkUserId { get; init; }

    public override string AggregateType => "auction-lifecycle";
    public override string AggregateId => AuctionId.ToString();
}

/// <summary>
/// Who may enter bids from the floor of a hall auction, on
/// <c>auctions.participants</c> (§29).
///
/// It rides the participants topic rather than a new one because it answers that
/// topic's question — whose key signs a frame for this auction — and the catcher
/// rebuilds clerks and bidders from the same replay. Like eligibility it carries an
/// epoch and never a key: the catcher derives one from a master it already holds,
/// so reading this topic says who is on the floor, not how to sign as them.
/// </summary>
public sealed record AuctionClerkAssigned : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid ClerkUserId { get; init; }
    public required bool Assigned { get; init; }
    public required int KeyEpoch { get; init; }

    public override string AggregateType => "auction-clerk";
    public override string AggregateId => $"{AuctionId}:{ClerkUserId}";
}
