namespace EAuction.Core;

/// <summary>
/// Topic names, in one place so producer and consumer cannot drift.
/// </summary>
public static class Topics
{
    /// <summary>Public auction definitions. Consumed by bid-catcher and query-bff.</summary>
    public const string Upcoming = "auctions.upcoming";

    /// <summary>
    /// Reserve prices, ACL-restricted to bid-processor (D-23). Separate from
    /// <see cref="Upcoming"/> precisely because the query BFF reads that one
    /// and feeds public APIs.
    /// </summary>
    public const string Sealed = "auctions.sealed";

    /// <summary>
    /// Compacted. Who may bid in which auction, from the participant service.
    ///
    /// Carries the eligibility fact and a key epoch — never a signing secret.
    /// A secret published to a topic is readable by anything with topic access
    /// and sits in the log until compaction catches up; the catcher derives
    /// each bidder's key instead (see <see cref="BidderKeys"/>).
    /// </summary>
    public const string Participants = "auctions.participants";

    /// <summary>Workflow events in both directions between admin and processor.</summary>
    public const string Lifecycle = "auctions.lifecycle";

    public const string Deposits = "auctions.deposits";

    /// <summary>
    /// Booklet and deposit payments, from the participant service. Nothing consumes
    /// it yet — payment reconciliation and finance reporting will. It is here rather
    /// than in the router that publishes to it because a topic name that lives in one
    /// service's string constant is invisible to the provisioning tool, and an
    /// unprovisioned topic stalls that service's entire outbox.
    /// </summary>
    public const string ParticipantPayments = "participants.payments";

    /// <summary>Compacted. Current price per auction, for the catcher and the fan-out.</summary>
    public const string CurrentWinner = "auctions.current-winner";

    public const string BidsRejected = "bids.rejected";

    /// <summary>
    /// Compacted. How far the processor has published side effects for each
    /// auction, so a restart does not re-announce work consumers already saw.
    /// </summary>
    public const string Checkpoints = "processor.checkpoints";

    public const string BidTopicPrefix = "bids.";

    public static string BidTopicFor(Guid auctionId) => BidTopicPrefix + auctionId.ToString("N");
}
