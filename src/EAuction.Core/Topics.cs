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

    /// <summary>Workflow events in both directions between admin and processor.</summary>
    public const string Lifecycle = "auctions.lifecycle";

    public const string Deposits = "auctions.deposits";

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
