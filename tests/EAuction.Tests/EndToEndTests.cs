using System.Net;
using System.Net.Http.Json;
using EAuction.BidCatcher;
using EAuction.BidProcessor;
using EAuction.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// Walks the whole hot path: HTTP POST of a binary frame to the catcher, the
/// append to the bid log, the processor reading it back in offset order, and
/// the winner that falls out.
///
/// The log here is <see cref="InMemoryBidLog"/>, which mirrors Kafka's
/// per-partition ordering contract. The Kafka implementation has not been run
/// against a live broker (no Docker daemon available) — see the note on
/// <see cref="KafkaBidLog"/>.
/// </summary>
public class EndToEndTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndToEndTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static HttpContent Body(byte[] frame)
    {
        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    private (HttpClient Client, CatcherState State, IBidLog Log) Arrange()
    {
        var client = _factory.CreateClient();
        return (client,
            _factory.Services.GetRequiredService<CatcherState>(),
            _factory.Services.GetRequiredService<IBidLog>());
    }

    [Fact]
    public async Task A_bid_from_an_eligible_bidder_is_accepted_and_receipted()
    {
        var (client, state, _) = Arrange();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        var bidder = Guid.NewGuid();

        state.UpsertAuction(auction);
        state.GrantEligibility(auction.AuctionId, bidder, TestAuction.Secret);

        var frame = TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now);
        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<BidReceipt>();
        Assert.Equal(auction.AuctionId, receipt.AuctionId);
        Assert.Equal(bidder, receipt.BidderId);
        Assert.Equal(BidFrame.ClientBidId(frame), receipt.ClientBidId);
        Assert.False(string.IsNullOrWhiteSpace(receipt.Signature));
    }

    [Fact]
    public async Task A_bidder_who_has_not_paid_the_deposit_is_turned_away_at_the_edge()
    {
        var (client, state, _) = Arrange();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));

        state.UpsertAuction(auction);
        // No GrantEligibility: the bidder never completed subscription.

        var frame = TestAuction.Frame(auction.AuctionId, Guid.NewGuid(), 1_200_000_00, now);
        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(nameof(RejectionReason.NotEligible), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_auction_is_rejected_without_touching_the_log()
    {
        var (client, _, _) = Arrange();
        var frame = TestAuction.Frame(Guid.NewGuid(), Guid.NewGuid(), 100, DateTimeOffset.UtcNow);

        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(nameof(RejectionReason.UnknownAuction), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_truncated_frame_is_a_bad_request()
    {
        var (client, _, _) = Arrange();
        var response = await client.PostAsync("/bids", Body(new byte[40]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_catcher_accepts_past_the_end_time_but_inside_the_hard_ceiling()
    {
        // §6.3: the catcher must not gate on ends_at, or a bid that an
        // extension would have made valid is refused before the catcher ever
        // learns of the extension.
        var (client, state, _) = Arrange();
        var now = DateTimeOffset.UtcNow;

        var auction = TestAuction.Build(
            now.AddMinutes(-60), now.AddMinutes(-1), quiet: TimeSpan.FromMinutes(2));
        var bidder = Guid.NewGuid();

        state.UpsertAuction(auction);
        state.GrantEligibility(auction.AuctionId, bidder, TestAuction.Secret);

        // One minute past ends_at; ceiling is ends_at + 3x2min + 1min grace.
        Assert.True(now < auction.HardCeiling(TimeSpan.FromMinutes(1)));

        var frame = TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now);
        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task The_catcher_refuses_a_bid_past_the_hard_ceiling()
    {
        var (client, state, _) = Arrange();
        var now = DateTimeOffset.UtcNow;

        var auction = TestAuction.Build(
            now.AddMinutes(-180), now.AddMinutes(-120), quiet: TimeSpan.FromMinutes(2));
        var bidder = Guid.NewGuid();

        state.UpsertAuction(auction);
        state.GrantEligibility(auction.AuctionId, bidder, TestAuction.Secret);

        var frame = TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now);
        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(nameof(RejectionReason.OutsideWindow), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_forged_signature_is_rejected_even_from_an_eligible_bidder()
    {
        var (client, state, _) = Arrange();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        var bidder = Guid.NewGuid();

        state.UpsertAuction(auction);
        state.GrantEligibility(auction.AuctionId, bidder, TestAuction.Secret);

        var frame = TestAuction.Frame(
            auction.AuctionId, bidder, 1_200_000_00, now, secret: new byte[32]);
        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(nameof(RejectionReason.BadSignature), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_bids_land_in_one_order_and_produce_one_winner()
    {
        // The point of the whole design: many bidders racing, one total order,
        // a winner that any replay of the log reproduces exactly.
        var (client, state, log) = Arrange();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));

        var bidders = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
        state.UpsertAuction(auction);
        foreach (var b in bidders)
            state.GrantEligibility(auction.AuctionId, b, TestAuction.Secret);

        // Everyone fires at once, at a spread of amounts.
        var posts = bidders.Select((b, i) =>
            client.PostAsync("/bids",
                Body(TestAuction.Frame(
                    auction.AuctionId, b, 1_000_000_00 + (i + 1) * 50_000_00, now))));

        var responses = await Task.WhenAll(posts);
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.True(accepted > 0, "at least some bids should be durably recorded");

        // Replay the log through the processor.
        var verdicts = new List<BidVerdict>();
        var engine = await DrainAsync(auction, log, verdicts);

        Assert.Equal(accepted, verdicts.Count);
        Assert.NotNull(engine.ProvisionalLeader);

        // The leader is the highest accepted amount, and the ladder agrees.
        var winners = verdicts.Where(v => v.Accepted).ToList();
        Assert.Equal(winners[^1].BidderId, engine.ProvisionalLeader);
        Assert.Equal(engine.CurrentPrice, engine.Ladder[0].AmountMinorUnits);

        // Offsets are strictly increasing: one total order, no gaps in reading.
        var offsets = verdicts.Select(v => v.Offset).ToList();
        Assert.Equal(offsets.OrderBy(o => o).ToList(), offsets);
    }

    [Fact]
    public async Task Replaying_the_log_twice_gives_the_identical_winner_and_ledger()
    {
        // Determinism is what makes the result defensible: the award can be
        // recomputed from the log by anyone and must come out the same.
        var (client, state, log) = Arrange();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));

        var bidders = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();
        state.UpsertAuction(auction);
        foreach (var b in bidders)
            state.GrantEligibility(auction.AuctionId, b, TestAuction.Secret);

        await Task.WhenAll(bidders.Select((b, i) =>
            client.PostAsync("/bids",
                Body(TestAuction.Frame(
                    auction.AuctionId, b, 1_000_000_00 + (i + 1) * 60_000_00, now)))));

        var first = await DrainAsync(auction, log, new List<BidVerdict>());
        var second = await DrainAsync(auction, log, new List<BidVerdict>());

        Assert.Equal(first.ProvisionalLeader, second.ProvisionalLeader);
        Assert.Equal(first.CurrentPrice, second.CurrentPrice);
        Assert.True(first.LedgerHead.SequenceEqual(second.LedgerHead));
    }

    /// <summary>Runs the processor over everything currently in the log.</summary>
    private static async Task<AuctionEngine> DrainAsync(
        AuctionDefinition auction, IBidLog log, List<BidVerdict> verdicts)
    {
        var prices = new List<long>();
        var pump = new AuctionPump(
            auction, log,
            (v, _) => { verdicts.Add(v); return ValueTask.CompletedTask; },
            (_, p, _) => { prices.Add(p); return ValueTask.CompletedTask; });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // The pump follows the tail forever; stop once it goes quiet.
            var run = pump.RunAsync(0, cts.Token);
            var lastCount = -1;
            while (lastCount != verdicts.Count)
            {
                lastCount = verdicts.Count;
                await Task.Delay(120, CancellationToken.None);
            }
            cts.Cancel();
            await run;
        }
        catch (OperationCanceledException) { }

        return pump.Engine;
    }
}
