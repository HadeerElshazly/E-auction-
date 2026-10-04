using System.Text.Json;
using EAuction.BidProcessor;
using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

public class ProcessorWiringTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static AuctionDefinition Define(
        DateTimeOffset start, DateTimeOffset end, TimeSpan? quiet = null) => new()
    {
        AuctionId = Guid.NewGuid(),
        StartsAt = start,
        EndsAt = end,
        OpeningPriceMinorUnits = 1_000_000_00,
        ReservePriceMinorUnits = 1_500_000_00,
        Increment = new IncrementPolicy.Fixed(50_000_00),
        QuietPeriod = quiet,
        MaxExtensions = 3
    };

    [Fact]
    public async Task An_auction_runs_only_once_both_halves_of_its_definition_arrive()
    {
        // The public definition and the reserve travel on separate topics
        // because the reserve is ACL-restricted (D-23), so either can land
        // first and neither alone is enough to run the auction.
        await using var h = new ProcessorHarness();
        var d = Define(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));

        await h.PublishApprovalAsync(d);
        await h.RecoverAsync();

        Assert.False(h.Registry.TryGet(d.AuctionId, out _));
        Assert.Contains(d.AuctionId, h.Registry.AwaitingReserve);

        await h.PublishReserveAsync(d);
        await h.RecoverAsync();

        Assert.True(h.Registry.TryGet(d.AuctionId, out var ready));
        Assert.Equal(1_500_000_00, ready.ReservePriceMinorUnits);
        Assert.Empty(h.Registry.AwaitingReserve);
        Assert.NotNull(h.Running(d.AuctionId));
    }

    [Fact]
    public async Task The_reserve_arriving_first_works_the_same_way()
    {
        await using var h = new ProcessorHarness();
        var d = Define(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));

        await h.PublishReserveAsync(d);
        await h.RecoverAsync();

        Assert.False(h.Registry.TryGet(d.AuctionId, out _));
        Assert.Contains(d.AuctionId, h.Registry.AwaitingDefinition);

        await h.PublishApprovalAsync(d);
        await h.RecoverAsync();

        Assert.True(h.Registry.TryGet(d.AuctionId, out _));
    }

    private static async Task<(ProcessorHarness H, AuctionDefinition D)> RunningAuction(
        TimeSpan? quiet = null, int endsInMinutes = 30)
    {
        var h = new ProcessorHarness(Grace);
        var d = Define(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(endsInMinutes),
            quiet);

        await h.PublishApprovalAsync(d);
        await h.PublishReserveAsync(d);
        await h.RecoverAsync();
        return (h, d);
    }

    [Fact]
    public async Task An_accepted_bid_publishes_the_new_current_winner()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        var sara = Guid.NewGuid();
        await h.BidAsync(d, sara, 1_200_000_00, DateTimeOffset.UtcNow);

        var winners = await h.OnTopicAsync<CurrentWinner>(Topics.CurrentWinner);
        var latest = Assert.Single(winners);

        Assert.Equal(d.AuctionId, latest.AuctionId);
        Assert.Equal(1_200_000_00, latest.PriceMinorUnits);
        Assert.Equal(sara, latest.LeaderBidderId);
    }

    [Fact]
    public async Task A_rejected_bid_publishes_a_rejection_with_its_reason()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        await h.BidAsync(d, Guid.NewGuid(), 900_000_00, DateTimeOffset.UtcNow);

        var rejections = await h.OnTopicAsync<BidRejected>(Topics.BidsRejected);
        var rejection = Assert.Single(rejections);

        Assert.Equal(nameof(RejectionReason.BelowMinimumIncrement), rejection.Reason);
        Assert.Empty(await h.OnTopicAsync<CurrentWinner>(Topics.CurrentWinner));
    }

    [Fact]
    public async Task An_auction_does_not_close_before_its_end_time()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        await h.Supervisor.TickAsync(DateTimeOffset.UtcNow, h.Token);

        Assert.False(h.Running(d.AuctionId).Closed);
        Assert.Empty(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
    }

    [Fact]
    public async Task Closing_offers_the_highest_bidder_that_clears_the_reserve()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        var khalid = Guid.NewGuid();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        await h.BidAsync(d, khalid, 1_100_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, ahmad, 1_550_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);

        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        var closed = Assert.Single(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Equal(3, closed.BidCount);

        var offered = Assert.Single(
            await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
        Assert.Equal(sara, offered.BidderId);
        Assert.Equal(1_800_000_00, offered.AmountMinorUnits);
        Assert.Equal(0, offered.CascadeStep);
    }

    [Fact]
    public async Task Closing_below_the_reserve_reports_the_ladder_exhausted()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_100_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, Guid.NewGuid(), 1_200_000_00, DateTimeOffset.UtcNow);

        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        Assert.Empty(await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
        Assert.Single(await h.LifecycleOfAsync<LadderExhausted>(nameof(LadderExhausted)));
    }

    [Fact]
    public async Task The_reserve_price_never_appears_on_the_lifecycle_topic()
    {
        // The committee is told someone qualifies, not what they had to beat.
        var (h, d) = await RunningAuction();
        await using var _ = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        foreach (var (_, payload) in await h.LifecycleAsync())
        {
            Assert.DoesNotContain("reserve", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("150000000", payload);
        }
    }

    [Fact]
    public async Task A_quiet_period_bid_pushes_the_close_out()
    {
        // The engine moves the end time, and the supervisor must re-read it
        // rather than hold a deadline it computed once.
        var (h, d) = await RunningAuction(quiet: TimeSpan.FromMinutes(2), endsInMinutes: 1);
        await using var _ = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, d.EndsAt.AddSeconds(-30));

        var running = h.Running(d.AuctionId);
        Assert.Equal(1, running.Pump!.Engine.ExtensionsUsed);
        Assert.Equal(d.EndsAt.AddMinutes(2), running.Pump.Engine.EffectiveEndsAt);

        // The original deadline has passed; the extended one has not.
        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);
        Assert.False(running.Closed);

        await h.Supervisor.TickAsync(d.EndsAt.AddMinutes(2) + Grace, h.Token);
        Assert.True(running.Closed);

        var closed = Assert.Single(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Equal(1, closed.ExtensionsUsed);
    }

    [Fact]
    public async Task Closing_happens_once_however_many_ticks_follow()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);

        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);
        await h.Supervisor.TickAsync(d.EndsAt + Grace + TimeSpan.FromMinutes(1), h.Token);
        await h.Supervisor.TickAsync(d.EndsAt + Grace + TimeSpan.FromMinutes(2), h.Token);

        Assert.Single(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Single(await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
    }

    [Fact]
    public async Task A_disqualification_from_admin_offers_the_next_qualifying_bidder()
    {
        // This is the processor's half of the cascade: admin withdraws the
        // award, the processor walks its ladder and names the next candidate.
        var (h, d) = await RunningAuction();
        await using var _ = h;

        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        await h.BidAsync(d, ahmad, 1_600_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);
        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        var first = Assert.Single(
            await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
        Assert.Equal(sara, first.BidderId);

        await h.PublishDisqualificationAsync(d.AuctionId, sara);
        await h.DrainLifecycleAsync();

        var offers = await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered));
        Assert.Equal(2, offers.Count);
        Assert.Equal(ahmad, offers[1].BidderId);
        Assert.Equal(1_600_000_00, offers[1].AmountMinorUnits);
        Assert.Equal(1, offers[1].CascadeStep);
    }

    [Fact]
    public async Task The_cascade_stops_when_the_next_bidder_is_below_the_reserve()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        var khalid = Guid.NewGuid();
        var sara = Guid.NewGuid();

        await h.BidAsync(d, khalid, 1_100_000_00, DateTimeOffset.UtcNow);  // under reserve
        await h.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);
        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        await h.PublishDisqualificationAsync(d.AuctionId, sara);
        await h.DrainLifecycleAsync();

        Assert.Single(await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
        Assert.Single(await h.LifecycleOfAsync<LadderExhausted>(nameof(LadderExhausted)));
    }

    [Fact]
    public async Task A_redelivered_disqualification_does_not_skip_a_bidder()
    {
        // Delivery is at-least-once. Advancing the cascade twice on one
        // disqualification would silently pass over a qualifying bidder.
        var (h, d) = await RunningAuction();
        await using var _ = h;

        var khalid = Guid.NewGuid();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        await h.BidAsync(d, khalid, 1_550_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, ahmad, 1_650_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, sara, 1_800_000_00, DateTimeOffset.UtcNow);
        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        await h.Supervisor.OnWinnerDisqualifiedAsync(
            new WinnerDisqualifiedPayload { AuctionId = d.AuctionId, BidderId = sara }, h.Token);
        await h.Supervisor.OnWinnerDisqualifiedAsync(
            new WinnerDisqualifiedPayload { AuctionId = d.AuctionId, BidderId = sara }, h.Token);

        var offers = await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered));

        Assert.Equal(2, offers.Count);
        Assert.Equal(ahmad, offers[1].BidderId);
        Assert.Equal(1, h.Running(d.AuctionId).CascadeStep);
    }

    [Fact]
    public async Task A_started_auction_is_announced_once()
    {
        var (h, d) = await RunningAuction();
        await using var _ = h;

        await h.Supervisor.TickAsync(DateTimeOffset.UtcNow, h.Token);
        await h.Supervisor.TickAsync(DateTimeOffset.UtcNow.AddSeconds(1), h.Token);

        Assert.Single(await h.LifecycleOfAsync<AuctionStarted>(nameof(AuctionStarted)));
    }
}

/// <summary>
/// The processor declares its own view of auction-admin's events rather than
/// sharing the types. That is deliberate — but it only works if the two shapes
/// actually line up, which these tests check against the real producer types.
/// </summary>
public class ContractDriftTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_processor_reads_every_field_of_a_real_AuctionApproved()
    {
        var produced = new AuctionAdmin.Domain.AuctionApproved
        {
            AuctionId = Guid.NewGuid(),
            NameAr = "أراضي مجمع السعيد",
            NameEn = "Al-Saeed",
            StartsAt = DateTimeOffset.UtcNow.AddDays(7),
            EndsAt = DateTimeOffset.UtcNow.AddDays(8),
            OpeningPriceMinorUnits = 1_000_000_00,
            MinIncrementMinorUnits = 50_000_00,
            DepositMinorUnits = 100_000_00,
            BookletPriceMinorUnits = 1_000_00,
            QuietPeriodSeconds = 120,
            MaxExtensions = 3,
            Channel = "Online",
            PlotCount = 2,
            TotalAreaSqm = 1350.5m
        };

        var consumed = JsonSerializer.Deserialize<AuctionApprovedPayload>(
            JsonSerializer.Serialize(produced, Json), Json)!;

        Assert.Equal(produced.AuctionId, consumed.AuctionId);
        Assert.Equal(produced.StartsAt, consumed.StartsAt);
        Assert.Equal(produced.EndsAt, consumed.EndsAt);
        Assert.Equal(produced.OpeningPriceMinorUnits, consumed.OpeningPriceMinorUnits);
        Assert.Equal(produced.MinIncrementMinorUnits, consumed.MinIncrementMinorUnits);
        Assert.Equal(produced.QuietPeriodSeconds, consumed.QuietPeriodSeconds);
        Assert.Equal(produced.MaxExtensions, consumed.MaxExtensions);
        Assert.Equal(produced.Channel, consumed.Channel);
    }

    [Fact]
    public void The_processor_reads_a_real_AuctionReserveSet()
    {
        var produced = new AuctionAdmin.Domain.AuctionReserveSet
        {
            AuctionId = Guid.NewGuid(),
            ReservePriceMinorUnits = 1_500_000_00
        };

        var consumed = JsonSerializer.Deserialize<AuctionReserveSetPayload>(
            JsonSerializer.Serialize(produced, Json), Json)!;

        Assert.Equal(produced.AuctionId, consumed.AuctionId);
        Assert.Equal(1_500_000_00, consumed.ReservePriceMinorUnits);
    }

    [Fact]
    public void The_processor_reads_a_real_WinnerDisqualified()
    {
        var produced = new AuctionAdmin.Domain.WinnerDisqualified
        {
            AuctionId = Guid.NewGuid(),
            AwardId = Guid.NewGuid(),
            BidderId = Guid.NewGuid(),
            Reason = "لم يسدد خلال المدة",
            DepositForfeited = true
        };

        var consumed = JsonSerializer.Deserialize<WinnerDisqualifiedPayload>(
            JsonSerializer.Serialize(produced, Json), Json)!;

        Assert.Equal(produced.AuctionId, consumed.AuctionId);
        Assert.Equal(produced.BidderId, consumed.BidderId);
        Assert.Equal(produced.Reason, consumed.Reason);
    }
}
