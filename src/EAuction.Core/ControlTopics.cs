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

/// <summary>
/// One control topic, and everything the provisioning tool needs to create it.
///
/// <paramref name="Partitions"/> is null for almost all of them, meaning "whatever
/// the tool was told": partition count is a throughput decision and belongs to the
/// environment. It is set only where the number is part of the design rather than a
/// tuning choice — <see cref="Topics.StaffActions"/> is hash-chained and a chain
/// needs a total order, which more than one partition does not give.
/// </summary>
public sealed record ControlTopic(string Name, TopicShape Shape, string Why, int? Partitions = null);

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

        new(Topics.Settlements, TopicShape.EventLog,
            "What the payment service did with each bidder's money. The payment "
            + "service replays it on start to know what it has already charged, so "
            + "losing it means charging every bidder a second time."),

        new(Topics.StaffActions, TopicShape.EventLog,
            "Every consequential staff action, hash-chained by the audit service. "
            + "One partition, because a chain needs a total order; compaction would "
            + "erase the history that is the entire point.",
            Partitions: 1),

        new(Topics.ParticipantPayments, TopicShape.EventLog,
            "Requests for the booklet fee and the deposit. The payment service consumes "
            + "it; without the topic the participant outbox cannot drain, and eligibility "
            + "never reaches the catcher.")
    ];
}
