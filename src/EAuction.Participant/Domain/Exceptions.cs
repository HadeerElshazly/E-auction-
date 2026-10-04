namespace EAuction.Participant.Domain;

/// <summary>Every problem at once, so a bidder fixes the form in one pass.</summary>
public sealed class ParticipantValidationException(IReadOnlyList<string> problems)
    : Exception("Not accepted: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public sealed class InvalidSubscriptionTransitionException(SubscriptionStatus from, string action)
    : Exception($"Cannot {action} a subscription in status {from}.")
{
    public SubscriptionStatus From { get; } = from;
}
