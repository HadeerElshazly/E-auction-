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
