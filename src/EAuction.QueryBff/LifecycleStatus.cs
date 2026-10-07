namespace EAuction.QueryBff;

/// <summary>
/// An auction's stage, from the lifecycle topic — the same events, read the same
/// way, as auction-admin reads them, so the public catalogue and the administrators'
/// screens cannot disagree about where an auction stands.
///
/// That topic has two writers. The bid processor records what the clock and the
/// bids decide: open, closed, who the candidate is, nobody left. auction-admin
/// records what people decide: cancelled, awarded, a winner disqualified, the next
/// bidder referred, ended unsold, settled. This read model once applied only the
/// first half, and so froze at «بانتظار الترسية» the moment the committee acted —
/// an awarded auction showed as still awaiting award to every citizen.
/// </summary>
public static class LifecycleStatus
{
    /// <summary>
    /// The stage after <paramref name="eventType"/>, or null when the event does not
    /// move the stage (a clerk's extension, an event this service does not know).
    /// </summary>
    public static string? After(string current, string eventType) => eventType switch
    {
        // The processor.
        "AuctionStarted" => "Live",
        "AuctionClosed" => "Closed",
        "LadderExhausted" => "Unsold",

        // After a disqualification the processor's next candidate is only a
        // suggestion: the committee decides whether to refer it (NextBidderReferred).
        // auction-admin holds the auction where it is until then, and so does this.
        "CandidateOffered" => current == "WinnerDisqualified" ? null : "PendingAward",

        // auction-admin.
        "AwardConfirmed" => "Awarded",
        "WinnerDisqualified" => "WinnerDisqualified",
        "NextBidderReferred" => "PendingAward",
        "AuctionUnsold" => "Unsold",
        "AuctionSettled" => "Settled",

        _ => null
    };

    /// <summary>Stages after which nothing on the auction can change for a bidder.</summary>
    public static bool IsFinal(string status) => status is "Unsold" or "Settled" or "Cancelled";
}
