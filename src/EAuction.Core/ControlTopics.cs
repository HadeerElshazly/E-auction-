namespace EAuction.Core;

/// <summary>
/// The cleanup policy each control topic must have, and why.
///
/// This is not documentation — it is the input to the provisioning tool, because
/// getting it wrong fails silently and late. A compacted topic created with the
/// default <c>delete</c> policy works perfectly until its retention expires; then
/// a restarted bid-catcher replays a topic with the eligibility rows aged out of
/// it, comes up warm with empty state, and rejects every bid in the auction.
/// Nothing logs an error, because nothing is wrong from Kafka's point of view.
/// </summary>
public enum TopicShape
{
    /// <summary>
    /// State. The latest record per key is the truth, so compaction is what makes
    /// the topic a durable table that a consumer can rebuild from offset 0.
    /// </summary>
    CompactedState,

    /// <summary>
    /// A stream of events where the history matters. These must NOT be compacted:
    /// the records share a key (the auction id), so compaction would keep only the
    /// last one and discard the rest.
    /// </summary>
    EventLog
}

public sealed record ControlTopic(string Name, TopicShape Shape, string Why);

public static class ControlTopics
{
    /// <summary>Every control-plane topic the platform needs provisioned up front.</summary>
    public static readonly IReadOnlyList<ControlTopic> All =
    [
        new(Topics.Upcoming, TopicShape.CompactedState,
            "The catcher and the processor rebuild their auction table from offset 0."),

        new(Topics.Sealed, TopicShape.CompactedState,
            "One reserve price per auction. ACL-restricted to the processor (D-23)."),

        new(Topics.Participants, TopicShape.CompactedState,
            "Eligibility per auction:bidder. If these age out the catcher rejects everyone."),

        new(Topics.CurrentWinner, TopicShape.CompactedState,
            "Latest price per auction, for the catcher's screen and the fan-out."),

        new(Topics.Checkpoints, TopicShape.CompactedState,
            "How far the processor has published per auction, so a restart does not re-announce."),

        new(Topics.Lifecycle, TopicShape.EventLog,
            "The processor replays AuctionStarted/Closed/CandidateOffered on recovery. "
            + "All of them are keyed by auction id, so compaction would erase the history "
            + "it recovers from and the processor would re-announce work already published."),

        new(Topics.Deposits, TopicShape.EventLog,
            "Payment events, keyed by auction:bidder; the sequence is the audit trail."),

        new(Topics.BidsRejected, TopicShape.EventLog,
            "Why a bid was refused, for the bidder's own feed and for dispute handling."),

        new(Topics.ParticipantPayments, TopicShape.EventLog,
            "Booklet and deposit payments, for reconciliation. Not yet consumed, but the "
            + "participant service publishes to it: without the topic its outbox cannot "
            + "drain, and eligibility never reaches the catcher.")
    ];
}
