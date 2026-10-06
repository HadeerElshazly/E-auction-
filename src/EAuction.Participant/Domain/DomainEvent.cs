using System.Text.Json.Serialization;
using EAuction.Outbox;

namespace EAuction.Participant.Domain;

public abstract record DomainEvent : IDomainEvent
{
    [JsonIgnore]
    public abstract string AggregateType { get; }

    [JsonIgnore]
    public abstract string AggregateId { get; }
}

/// <summary>
/// Who may bid in which auction — the catcher's entire admission list.
///
/// Carries a key epoch, never a signing secret. A secret published to a topic
/// is readable by anything with topic access and sits in the log until
/// compaction catches up; the catcher derives the key from a master it already
/// holds (<c>BidderKeys</c>), so reading this topic tells you who may bid, not
/// how to bid as them.
///
/// Keyed auctionId:bidderId and compacted, so the latest row is the truth and
/// revocation is just another row.
/// </summary>
public sealed record ParticipantEligibilityChanged : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }
    public required bool Eligible { get; init; }
    public required int KeyEpoch { get; init; }

    /// <summary>
    /// The bidder's name, and only when the auction names its bidders (D-22).
    ///
    /// Null on a masked auction — not blanked downstream, never put on the topic at
    /// all. A name on a compacted topic outlives the auction that justified it, and
    /// compaction has no deadline; the same reasoning that moved the reserve price
    /// onto its own topic applies to a citizen's name.
    /// </summary>
    public string? DisplayNameAr { get; init; }

    public override string AggregateType => "participant-eligibility";
    public override string AggregateId => $"{AuctionId}:{BidderId}";
}

/// <summary>
/// A deposit is owed. The payment service picks this up; until it reports
/// back, the bidder is not eligible.
/// </summary>
public sealed record DepositRequested : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required string Method { get; init; }

    public override string AggregateType => "participant-payments";
    public override string AggregateId => $"{AuctionId}:{BidderId}";
}

/// <summary>
/// The booklet fee is owed. Same topic as the deposit, same consumer: the payment
/// service does not care which of the two it is being asked to take, only how much
/// and from whom.
/// </summary>
public sealed record BookletFeeRequested : DomainEvent
{
    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }
    public required long AmountMinorUnits { get; init; }

    public override string AggregateType => "participant-payments";
    public override string AggregateId => $"{AuctionId}:{BidderId}";
}

/// <summary>
/// The purpose names as they appear on <c>payments.settlements</c>.
///
/// Duplicated here rather than referenced from the payment service, because the
/// dependency runs the other way: that service consumes this one's events and must
/// not be a build-time prerequisite for it. Two string constants are a smaller
/// price than a cycle between two deployables.
/// </summary>
public static class PaymentPurposes
{
    public const string Booklet = nameof(Booklet);
    public const string Deposit = nameof(Deposit);
    public const string Brokerage = nameof(Brokerage);
}

/// <summary>Outcomes on <c>payments.settlements</c>. Same reasoning as <see cref="PaymentPurposes"/>.</summary>
public static class PaymentOutcomeNames
{
    public const string Charged = nameof(Charged);
    public const string Refused = nameof(Refused);
}
