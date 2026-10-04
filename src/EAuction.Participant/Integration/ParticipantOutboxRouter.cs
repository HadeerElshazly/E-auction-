using EAuction.Core;
using EAuction.Outbox;

namespace EAuction.Participant.Integration;

/// <summary>
/// Where this service's outbox rows go. Each destination has its own
/// aggregate type so Debezium's EventRouter routes with no custom SMT.
/// </summary>
public sealed class ParticipantOutboxRouter : IOutboxRouter
{
    public const string Payments = "participants.payments";

    public string? Resolve(string aggregateType) => aggregateType switch
    {
        "participant-eligibility" => Topics.Participants,
        "participant-payments" => Payments,
        _ => null
    };
}
