namespace EAuction.AuctionAdmin.Outbox;

/// <summary>
/// Writes an outbox row onto its topic. Kafka in deployment; an in-memory
/// implementation lets the relay's ordering guarantees be tested without a
/// broker.
/// </summary>
public interface ITopicPublisher
{
    /// <summary>
    /// Creates a topic if it does not exist. Must be idempotent — the relay is
    /// at-least-once and will call this again after a crash.
    /// </summary>
    Task EnsureTopicAsync(string topic, CancellationToken ct);

    Task PublishAsync(string topic, string key, string payload, string eventType, CancellationToken ct);
}

/// <summary>
/// Maps an outbox row's aggregate type to its destination topic.
///
/// Each destination has its own aggregate type rather than one "auction"
/// bucket, so Debezium's EventRouter routes correctly with no custom SMT and
/// consumers do not have to filter on a header. The ACL boundary that keeps
/// the reserve price away from the public read path is a topic boundary
/// (D-06), which only works if the reserve has a topic of its own.
/// </summary>
public static class TopicMap
{
    public const string Upcoming = "auctions.upcoming";
    public const string Sealed = "auctions.sealed";
    public const string Lifecycle = "auctions.lifecycle";
    public const string Deposits = "auctions.deposits";
    public const string BidTopicPrefix = "bids.";

    public static string BidTopicFor(Guid auctionId) => BidTopicPrefix + auctionId.ToString("N");

    public static string? Resolve(string aggregateType) => aggregateType switch
    {
        "auction-upcoming" => Upcoming,
        "auction-sealed" => Sealed,
        "auction-lifecycle" => Lifecycle,
        "auction-deposits" => Deposits,
        _ => null
    };
}
