using EAuction.Core;
using EAuction.Outbox;

namespace EAuction.Participant.Integration;

/// <summary>
/// Where this service's outbox rows go. Each destination has its own
/// aggregate type so Debezium's EventRouter routes with no custom SMT.
/// </summary>
public sealed class ParticipantOutboxRouter : IOutboxRouter
{
    public const string Payments = Topics.ParticipantPayments;

    public string? Resolve(string aggregateType) => aggregateType switch
    {
        "participant-eligibility" => Topics.Participants,
        "participant-payments" => Payments,

        // Who verified a guarantee, who revoked an eligibility. Not a participant
        // fact, which is why its type is shared — see StaffActionRecorded.
        "staff-action" => Topics.StaffActions,
        _ => null
    };
}
