using EAuction.BidProcessor;
using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// What happens when the processor dies and comes back.
///
/// The engine is rebuilt by replaying every bid from offset 0, which is
/// deterministic. The checkpoint exists so that replay stays silent over the
/// bids whose side effects consumers have already seen — otherwise a restart
/// would re-send an outbid notification for every bid the auction ever had.
/// </summary>
public class ProcessorRestartTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static AuctionDefinition Define(int endsInMinutes = 30) => new()
    {
        AuctionId = Guid.NewGuid(),
        StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        EndsAt = DateTimeOffset.UtcNow.AddMinutes(endsInMinutes),
        OpeningPriceMinorUnits = 1_000_000_00,
        ReservePriceMinorUnits = 1_500_000_00,
        Increment = new IncrementPolicy.Fixed(50_000_00),
        MaxExtensions = 3
    };

    private static async Task<(ProcessorHarness H, AuctionDefinition D)> Started(
        int endsInMinutes = 30)
    {
        var h = new ProcessorHarness(Grace);
        var d = Define(endsInMinutes);
        await h.PublishApprovalAsync(d);
        await h.PublishReserveAsync(d);
        await h.RecoverAsync();
        return (h, d);
    }

    [Fact]
    public async Task A_restart_republishes_nothing_for_bids_already_handled()
    {
        var (first, d) = await Started();

        var bidders = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < bidders.Length; i++)
            await first.BidAsync(d, bidders[i], 1_000_000_00 + (i + 1) * 50_000_00, DateTimeOffset.UtcNow);

        var winnersBefore = await first.CountOnAsync(Topics.CurrentWinner);
        Assert.Equal(6, winnersBefore);

        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        // The pump replays all six bids to rebuild the engine...
        var running = second.Running(d.AuctionId);
        await WaitFor(() => running.ProcessedBidCount == 6, "replay never completed");

        // ...and publishes none of them again.
        Assert.Equal(winnersBefore, await second.CountOnAsync(Topics.CurrentWinner));

        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_restart_rebuilds_the_engine_exactly()
    {
        var (first, d) = await Started();

        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();
        await first.BidAsync(d, ahmad, 1_200_000_00, DateTimeOffset.UtcNow);
        await first.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);

        var before = first.Running(d.AuctionId).Pump!.Engine;
        var priceBefore = before.CurrentPrice;
        var leaderBefore = before.ProvisionalLeader;
        var ledgerBefore = before.LedgerHead.ToArray();

        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var running = second.Running(d.AuctionId);
        await WaitFor(() => running.ProcessedBidCount == 2, "replay never completed");

        var after = running.Pump!.Engine;
        Assert.Equal(priceBefore, after.CurrentPrice);
        Assert.Equal(leaderBefore, after.ProvisionalLeader);
        Assert.True(ledgerBefore.AsSpan().SequenceEqual(after.LedgerHead));

        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_restart_does_not_resend_rejections()
    {
        var (first, d) = await Started();

        await first.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await first.BidAsync(d, Guid.NewGuid(), 900_000_00, DateTimeOffset.UtcNow);
        await first.BidAsync(d, Guid.NewGuid(), 950_000_00, DateTimeOffset.UtcNow);

        Assert.Equal(2, await first.CountOnAsync(Topics.BidsRejected));

        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var running = second.Running(d.AuctionId);
        await WaitFor(() => running.ProcessedBidCount == 3, "replay never completed");

        Assert.Equal(2, await second.CountOnAsync(Topics.BidsRejected));

        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_restart_after_a_close_does_not_close_or_offer_again()
    {
        var (first, d) = await Started();

        await first.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await first.Supervisor.TickAsync(d.EndsAt + Grace, first.Token);

        Assert.Single(await first.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Single(await first.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));

        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var running = second.Running(d.AuctionId);
        Assert.True(running.Closed);
        Assert.True(running.Announced);

        // Ticking well past the end must not reopen anything.
        await second.Supervisor.TickAsync(d.EndsAt.AddHours(1), second.Token);

        Assert.Single(await second.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Single(await second.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
        // The first run's announcement is still on the topic; what matters is
        // that the restart did not add a second one.
        Assert.Single(await second.LifecycleOfAsync<AuctionStarted>(nameof(AuctionStarted)));

        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_restart_restores_the_cascade_position()
    {
        var (first, d) = await Started();

        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();
        await first.BidAsync(d, ahmad, 1_600_000_00, DateTimeOffset.UtcNow);
        await first.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);

        await first.Supervisor.TickAsync(d.EndsAt + Grace, first.Token);
        await first.PublishDisqualificationAsync(d.AuctionId, sara);
        await first.DrainLifecycleAsync();

        Assert.Equal(2, (await first.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered))).Count);

        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var running = second.Running(d.AuctionId);
        Assert.Equal(1, running.CascadeStep);
        Assert.Contains(sara, running.Disqualified);

        // The owed offers were already published, so resume adds none.
        Assert.Equal(2, (await second.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered))).Count);

        await first.DisposeAsync();
    }

    [Fact]
    public async Task An_offer_owed_when_the_process_died_is_re_issued_on_resume()
    {
        // The window that would otherwise leave the committee waiting forever:
        // the disqualification is on the topic, but the processor died before
        // publishing the next candidate.
        var (first, d) = await Started();

        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();
        await first.BidAsync(d, ahmad, 1_600_000_00, DateTimeOffset.UtcNow);
        await first.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);
        await first.Supervisor.TickAsync(d.EndsAt + Grace, first.Token);

        // Published, but never consumed by this process.
        await first.PublishDisqualificationAsync(d.AuctionId, sara);
        await first.StopAsync();

        Assert.Single(await first.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var offers = await second.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered));

        Assert.Equal(2, offers.Count);
        Assert.Equal(ahmad, offers[1].BidderId);
        Assert.Equal(1, offers[1].CascadeStep);

        await first.DisposeAsync();
    }

    [Fact]
    public async Task Bids_arriving_after_a_restart_publish_normally()
    {
        var (first, d) = await Started();

        await first.BidAsync(d, Guid.NewGuid(), 1_200_000_00, DateTimeOffset.UtcNow);
        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var running = second.Running(d.AuctionId);
        await WaitFor(() => running.ProcessedBidCount == 1, "replay never completed");
        Assert.Equal(1, await second.CountOnAsync(Topics.CurrentWinner));

        var sara = Guid.NewGuid();
        await second.BidAsync(d, sara, 1_500_000_00, DateTimeOffset.UtcNow);

        var winners = await second.OnTopicAsync<CurrentWinner>(Topics.CurrentWinner);
        Assert.Equal(2, winners.Count);
        Assert.Equal(sara, winners[^1].LeaderBidderId);

        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_stale_checkpoint_republishes_only_what_it_missed()
    {
        // Checkpoints are written periodically, not per record, so a crash
        // loses the tail. Delivery is at-least-once either way; what matters is
        // that the republish is bounded by the commit interval, not the whole
        // auction.
        await using var first = new ProcessorHarness(
            Grace, new CheckpointPolicy { EveryRecords = 3, EveryInterval = TimeSpan.FromHours(1) });

        var d = Define();
        await first.PublishApprovalAsync(d);
        await first.PublishReserveAsync(d);
        await first.RecoverAsync();

        var bidders = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < 5; i++)
            await first.BidAsync(d, bidders[i], 1_000_000_00 + (i + 1) * 50_000_00, DateTimeOffset.UtcNow);

        Assert.Equal(5, await first.CountOnAsync(Topics.CurrentWinner));

        // Committed through offset 2; offsets 3 and 4 are past the checkpoint.
        Assert.Equal(2, first.Checkpoints.PublishedThrough(d.AuctionId));

        await first.StopAsync();

        await using var second = first.Restart(Grace);
        await second.RecoverAsync();

        var running = second.Running(d.AuctionId);
        await WaitFor(() => running.ProcessedBidCount == 5, "replay never completed");

        // Exactly the two uncommitted records come back, not all five.
        Assert.Equal(7, await second.CountOnAsync(Topics.CurrentWinner));
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        // Generous on purpose: the wait returns the moment its condition
        // holds, so a long deadline costs nothing when the machine is idle
        // and stops the suite flaking when several assemblies share it.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(message);
    }
}
