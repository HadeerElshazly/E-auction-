using EAuction.Core;

namespace EAuction.AuctionAdmin.Outbox;

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
    public const string Upcoming = Topics.Upcoming;
    public const string Sealed = Topics.Sealed;
    public const string Lifecycle = Topics.Lifecycle;
    public const string Deposits = Topics.Deposits;
    public const string Participants = Topics.Participants;
    public const string StaffActions = Topics.StaffActions;

    public static string BidTopicFor(Guid auctionId) => Topics.BidTopicFor(auctionId);

    public static string? Resolve(string aggregateType) => aggregateType switch
    {
        "auction-upcoming" => Upcoming,
        "auction-sealed" => Sealed,
        "auction-lifecycle" => Lifecycle,
        "auction-deposits" => Deposits,

        // The clerk assignment answers the participants topic's own question —
        // whose key signs a frame for this auction — so it goes there rather than
        // onto a topic of its own.
        "auction-clerk" => Participants,

        // Who did it, rather than what happened. The only aggregate type here that
        // is not an auction fact, and the only one three services produce — see
        // StaffActionRecorded. Routed, not skipped: the relay stops on a row it
        // cannot place, so forgetting this line would stall every auction event
        // behind the first audited action.
        "staff-action" => StaffActions,
        _ => null
    };
}
