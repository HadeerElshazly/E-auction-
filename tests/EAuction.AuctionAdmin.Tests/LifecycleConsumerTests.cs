using System.Text.Json;
using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Outbox;
using EAuction.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// Admin's half of the loop with the bid processor: the workflow advances from
/// the processor's lifecycle events instead of waiting on an endpoint call.
/// </summary>
[Collection("postgres")]
public class LifecycleConsumerTests(PostgresFixture pg)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private LifecycleConsumer Consumer() =>
        new(pg.Factory, NullLogger<LifecycleConsumer>.Instance);

    private static StreamEvent Event(Guid auctionId, string eventType, object? payload = null) =>
        new(Topics.Lifecycle, auctionId.ToString(),
            JsonSerializer.Serialize(payload ?? new { auctionId }, Json), eventType, 0);

    private async Task<Guid> SeedApprovedAuction()
    {
        var now = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(now);
        auction.SubmitForReview(now);
        auction.Approve(now);

        await using var db = await pg.Factory.CreateDbContextAsync();
        db.Auctions.Add(auction);
        await db.SaveChangesAsync();
        return auction.Id;
    }

    private async Task<AuctionStatus> StatusOf(Guid id)
    {
        await using var db = await pg.Factory.CreateDbContextAsync();
        return (await db.Auctions.FindAsync(id))!.Status;
    }

    private async Task<Auction> LoadAsync(Guid id)
    {
        await using var db = await pg.Factory.CreateDbContextAsync();
        return await db.Auctions.Include(a => a.Awards).FirstAsync(a => a.Id == id);
    }

    [Fact]
    public async Task The_relay_moves_an_auction_to_scheduled_when_it_actually_publishes()
    {
        // Publication is what makes the auction visible to bidders, so only the
        // relay knows when Approved becomes Scheduled.
        var id = await SeedApprovedAuction();
        Assert.Equal(AuctionStatus.Approved, await StatusOf(id));

        var relay = new OutboxRelay(
            pg.Factory, new InMemoryTopicPublisher(), NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        Assert.Equal(AuctionStatus.Scheduled, await StatusOf(id));
    }

    [Fact]
    public async Task AuctionStarted_takes_a_scheduled_auction_live()
    {
        var id = await SeedApprovedAuction();
        var relay = new OutboxRelay(
            pg.Factory, new InMemoryTopicPublisher(), NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        await Consumer().ApplyAsync(Event(id, "AuctionStarted"), CancellationToken.None);

        Assert.Equal(AuctionStatus.Live, await StatusOf(id));
    }

    [Fact]
    public async Task AuctionClosed_moves_a_live_auction_to_eligibility_review()
    {
        var id = await SeedApprovedAuction();
        var relay = new OutboxRelay(
            pg.Factory, new InMemoryTopicPublisher(), NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        var consumer = Consumer();
        await consumer.ApplyAsync(Event(id, "AuctionStarted"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "AuctionClosed"), CancellationToken.None);

        Assert.Equal(AuctionStatus.PendingEligibilityReview, await StatusOf(id));
    }

    [Fact]
    public async Task CandidateOffered_puts_the_bidder_in_front_of_the_committee()
    {
        var id = await SeedApprovedAuction();
        var relay = new OutboxRelay(
            pg.Factory, new InMemoryTopicPublisher(), NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        var consumer = Consumer();
        await consumer.ApplyAsync(Event(id, "AuctionStarted"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "AuctionClosed"), CancellationToken.None);

        var bidder = Guid.NewGuid();
        await consumer.ApplyAsync(
            Event(id, "CandidateOffered", new
            {
                auctionId = id, bidderId = bidder,
                amountMinorUnits = 1_800_000_00L, cascadeStep = 0
            }),
            CancellationToken.None);

        var auction = await LoadAsync(id);

        Assert.Equal(AuctionStatus.PendingAward, auction.Status);
        Assert.Equal(bidder, auction.PendingCandidateBidderId);
        Assert.Equal(1_800_000_00, auction.PendingCandidateAmountMinorUnits);

        // Offering a candidate is not awarding one. The committee still decides.
        Assert.Empty(auction.Awards);
    }

    [Fact]
    public async Task LadderExhausted_marks_the_auction_unsold()
    {
        var id = await SeedApprovedAuction();
        var relay = new OutboxRelay(
            pg.Factory, new InMemoryTopicPublisher(), NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        var consumer = Consumer();
        await consumer.ApplyAsync(Event(id, "AuctionStarted"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "AuctionClosed"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "LadderExhausted"), CancellationToken.None);

        Assert.Equal(AuctionStatus.Unsold, await StatusOf(id));
    }

    [Fact]
    public async Task A_redelivered_event_is_ignored_rather_than_failing()
    {
        // At-least-once delivery means every one of these will arrive twice
        // sooner or later. The transition guard is what makes that safe.
        var id = await SeedApprovedAuction();
        var relay = new OutboxRelay(
            pg.Factory, new InMemoryTopicPublisher(), NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        var consumer = Consumer();
        await consumer.ApplyAsync(Event(id, "AuctionStarted"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "AuctionStarted"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "AuctionClosed"), CancellationToken.None);
        await consumer.ApplyAsync(Event(id, "AuctionClosed"), CancellationToken.None);

        Assert.Equal(AuctionStatus.PendingEligibilityReview, await StatusOf(id));
    }

    [Fact]
    public async Task Admin_s_own_events_coming_back_on_the_topic_are_ignored()
    {
        var id = await SeedApprovedAuction();
        var before = await StatusOf(id);

        var consumer = Consumer();
        foreach (var ownEvent in new[] { "AwardConfirmed", "WinnerDisqualified", "AuctionRejected" })
            await consumer.ApplyAsync(Event(id, ownEvent), CancellationToken.None);

        Assert.Equal(before, await StatusOf(id));
    }

    [Fact]
    public async Task An_event_for_an_unknown_auction_is_dropped_quietly()
    {
        await Consumer().ApplyAsync(
            Event(Guid.NewGuid(), "AuctionStarted"), CancellationToken.None);
    }
}
