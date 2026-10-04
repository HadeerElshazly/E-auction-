using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Outbox;
using EAuction.AuctionAdmin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// Against a real PostgreSQL instance — the transactional claim is the whole
/// point of the outbox and cannot be tested without real transactions.
/// </summary>
[Collection("postgres")]
public class OutboxTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<List<OutboxMessage>> MessagesFor(Guid auctionId)
    {
        await using var db = await pg.Factory.CreateDbContextAsync();
        return await db.Outbox
            .Where(m => m.AggregateId == auctionId.ToString())
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task Approving_writes_the_state_change_and_both_events_in_one_commit()
    {
        var future = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(future);
        auction.SubmitForReview(future);
        auction.Approve(future);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            var saved = await db.Auctions.FindAsync(auction.Id);
            Assert.Equal(AuctionStatus.Approved, saved!.Status);
        }

        var messages = await MessagesFor(auction.Id);
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Type == nameof(AuctionApproved)
                                       && m.AggregateType == "auction-upcoming");
        Assert.Contains(messages, m => m.Type == nameof(AuctionReserveSet)
                                       && m.AggregateType == "auction-sealed");
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_neither_the_state_change_nor_the_event()
    {
        // The property that makes the outbox worth having: an event can never
        // exist without the state change that produced it, and vice versa.
        var future = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(future);
        auction.SubmitForReview(future);
        auction.Approve(future);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            Assert.Null(await db.Auctions.FindAsync(auction.Id));
        }

        Assert.Empty(await MessagesFor(auction.Id));
    }

    [Fact]
    public async Task The_public_outbox_payload_contains_no_reserve_price()
    {
        var future = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(future);
        auction.SubmitForReview(future);
        auction.Approve(future);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        var messages = await MessagesFor(auction.Id);
        var publicMessage = messages.Single(m => m.AggregateType == "auction-upcoming");
        var sealedMessage = messages.Single(m => m.AggregateType == "auction-sealed");

        Assert.DoesNotContain("1500000000", publicMessage.Payload);
        Assert.DoesNotContain("reserve", publicMessage.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reserve", sealedMessage.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_whole_cascade_leaves_an_ordered_event_trail()
    {
        var future = DateTimeOffset.UtcNow;
        var auction = Build.AwaitingSettlement(future, out _);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            var saved = await db.Auctions
                .Include(a => a.Awards).Include(a => a.Plots)
                .FirstAsync(a => a.Id == auction.Id);

            saved.DisqualifyWinner("لم يسدد", forfeitDeposit: true, future.AddDays(6));
            saved.OfferCandidate(Guid.NewGuid(), 1_700_000_00);
            saved.ConfirmAward(Build.Committee, future.AddDays(7), TimeSpan.FromDays(5));
            await db.SaveChangesAsync();
        }

        var types = (await MessagesFor(auction.Id)).Select(m => m.Type).ToList();

        Assert.Equal(new[]
        {
            nameof(AuctionApproved),
            nameof(AuctionReserveSet),
            nameof(AwardConfirmed),
            nameof(WinnerDisqualified),
            nameof(AwardConfirmed)
        }.OrderBy(x => x), types.OrderBy(x => x));

        Assert.Equal(2, types.Count(t => t == nameof(AwardConfirmed)));
    }

    [Fact]
    public async Task The_relay_creates_the_bid_topic_before_publishing_the_auction()
    {
        // §5: if an auction reached auctions.upcoming before its bid topic
        // existed, the catcher could accept a bid with nowhere to put it —
        // and broker auto-create is deliberately off.
        var future = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(future);
        auction.SubmitForReview(future);
        auction.Approve(future);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        var publisher = new InMemoryTopicPublisher();
        var relay = new OutboxRelay(pg.Factory, publisher, NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        var actions = publisher.Actions.ToList();
        var ensureIndex = actions.FindIndex(a => a == $"ensure-topic:bids.{auction.Id:N}");
        var publishIndex = actions.FindIndex(
            a => a == $"publish:{TopicMap.Upcoming}:{nameof(AuctionApproved)}:{auction.Id}");

        Assert.True(ensureIndex >= 0, "the bid topic was never created");
        Assert.True(publishIndex >= 0, "the auction was never published");
        Assert.True(ensureIndex < publishIndex,
            "the bid topic must be created before the auction is published");
    }

    [Fact]
    public async Task The_relay_routes_each_aggregate_type_to_its_own_topic()
    {
        var future = DateTimeOffset.UtcNow;
        var auction = Build.AwaitingSettlement(future, out _);
        auction.Settle(future.AddDays(1));

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        var publisher = new InMemoryTopicPublisher();
        var relay = new OutboxRelay(pg.Factory, publisher, NullLogger<OutboxRelay>.Instance);
        await relay.DrainOnceAsync(100, CancellationToken.None);

        var mine = publisher.Published.Where(p => p.Key == auction.Id.ToString()).ToList();

        Assert.Equal(TopicMap.Upcoming, mine.Single(p => p.EventType == nameof(AuctionApproved)).Topic);
        Assert.Equal(TopicMap.Sealed, mine.Single(p => p.EventType == nameof(AuctionReserveSet)).Topic);
        Assert.Equal(TopicMap.Lifecycle, mine.Single(p => p.EventType == nameof(AwardConfirmed)).Topic);
        Assert.Equal(TopicMap.Deposits, mine.Single(p => p.EventType == nameof(DepositsReleasable)).Topic);

        // Keyed by auctionId everywhere, so one auction's events stay ordered
        // on a single partition.
        Assert.All(mine, p => Assert.Equal(auction.Id.ToString(), p.Key));
    }

    [Fact]
    public async Task A_relayed_message_is_not_published_twice()
    {
        var future = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(future);
        auction.SubmitForReview(future);
        auction.Approve(future);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        var publisher = new InMemoryTopicPublisher();
        var relay = new OutboxRelay(pg.Factory, publisher, NullLogger<OutboxRelay>.Instance);

        await relay.DrainOnceAsync(100, CancellationToken.None);
        var afterFirst = publisher.Published.Count(p => p.Key == auction.Id.ToString());

        await relay.DrainOnceAsync(100, CancellationToken.None);
        var afterSecond = publisher.Published.Count(p => p.Key == auction.Id.ToString());

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task A_failed_publish_leaves_the_message_queued_for_retry()
    {
        // At-least-once: a lost approval is unacceptable, a duplicate is not.
        var future = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(future);
        auction.SubmitForReview(future);
        auction.Approve(future);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        var publisher = new InMemoryTopicPublisher
        {
            FailPublishFor = type => type == nameof(AuctionApproved)
        };
        var relay = new OutboxRelay(pg.Factory, publisher, NullLogger<OutboxRelay>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.DrainOnceAsync(100, CancellationToken.None));

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            var unrelayed = await db.Outbox
                .CountAsync(m => m.AggregateId == auction.Id.ToString() && m.RelayedAt == null);
            Assert.Equal(2, unrelayed);
        }

        publisher.FailPublishFor = null;
        var published = await relay.DrainOnceAsync(100, CancellationToken.None);
        Assert.Equal(2, published);
    }
}
