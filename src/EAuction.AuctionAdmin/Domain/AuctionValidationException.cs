namespace EAuction.AuctionAdmin.Domain;

/// <summary>Every problem at once, so an admin fixes the form in one pass.</summary>
public sealed class AuctionValidationException(IReadOnlyList<string> problems)
    : Exception("The auction is not ready: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public sealed class InvalidAuctionTransitionException(AuctionStatus from, string action)
    : Exception($"Cannot {action} an auction in status {from}.")
{
    public AuctionStatus From { get; } = from;
    public string Action { get; } = action;
}
