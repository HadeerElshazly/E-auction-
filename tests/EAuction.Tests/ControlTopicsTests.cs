using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The provisioning tool reads <see cref="ControlTopics.All"/>. A topic that is not
/// in that list is not created, and a service that publishes to it stalls its whole
/// outbox — an ordered relay cannot skip a failing message without losing ordering,
/// so one missing topic blocks every event behind it.
///
/// This is not hypothetical: `participants.payments` lived only in the participant
/// router's own string constant, so it was never provisioned, and the eligibility
/// events queued behind it never reached the bid catcher. Nothing in the suite
/// could see that, because nothing in the suite looked at the two together.
/// </summary>
public class ControlTopicsTests
{
    [Fact]
    public void Every_topic_name_in_Topics_is_provisioned()
    {
        var declared = typeof(Topics).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            // The bid topics are per-auction and created on approval, not up front.
            .Where(f => f.Name != nameof(Topics.BidTopicPrefix))
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToArray();

        var provisioned = ControlTopics.All.Select(t => t.Name).ToHashSet();

        var missing = declared.Where(d => !provisioned.Contains(d.Value)).ToArray();

        Assert.True(missing.Length == 0,
            "These topics are declared but never provisioned: "
            + string.Join(", ", missing.Select(m => $"{m.Name} ({m.Value})"))
            + ". Add them to ControlTopics.All with the cleanup policy they need, or the "
            + "service that publishes to them will stall on an unknown topic.");
    }

    [Fact]
    public void No_topic_is_provisioned_twice_or_with_an_empty_name()
    {
        Assert.DoesNotContain(ControlTopics.All, t => string.IsNullOrWhiteSpace(t.Name));
        Assert.DoesNotContain(ControlTopics.All.GroupBy(t => t.Name), g => g.Count() > 1);
    }

    [Fact]
    public void The_lifecycle_log_is_not_compacted()
    {
        // The processor recovers by replaying AuctionStarted, AuctionClosed and
        // CandidateOffered — all keyed by auction id. Compaction keeps only the last
        // record per key, so it would erase the history recovery depends on and the
        // processor would re-announce work consumers already saw.
        var lifecycle = ControlTopics.All.Single(t => t.Name == Topics.Lifecycle);
        Assert.Equal(TopicShape.EventLog, lifecycle.Shape);
    }

    [Theory]
    [InlineData(nameof(Topics.Upcoming))]
    [InlineData(nameof(Topics.Participants))]
    [InlineData(nameof(Topics.CurrentWinner))]
    [InlineData(nameof(Topics.Checkpoints))]
    [InlineData(nameof(Topics.Sealed))]
    public void The_state_topics_are_compacted(string field)
    {
        // Each of these is rebuilt from offset 0 by at least one consumer. Without
        // compaction they age out on the retention schedule and the consumer comes up
        // warm with empty state: the catcher would reject every bid, and nothing would
        // log an error, because nothing is wrong from Kafka's point of view.
        var name = (string)typeof(Topics).GetField(field)!.GetRawConstantValue()!;
        Assert.Equal(TopicShape.CompactedState,
            ControlTopics.All.Single(t => t.Name == name).Shape);
    }
}
