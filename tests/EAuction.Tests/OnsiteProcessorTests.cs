using EAuction.BidProcessor;
using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// What the processor does with a hall auction (§29): it records, and it does not
/// judge when the auction ends.
///
/// The client's instruction was that an onsite auction "doesn't go through the
/// processing service, as we already know who wins". Taken literally that would
/// also throw away the ladder, and the ladder is what the committee's cascade walks
/// down when a winner is disqualified — so the processor still builds it. What it
/// gives up is the authority it has online: no clock closes a hall auction and no
/// anti-sniping rule moves its end time, because there is an auctioneer in the room
/// doing both.
/// </summary>
public class OnsiteProcessorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static AuctionDefinition Hall(int endsInMinutes = 30) => new()
    {
        AuctionId = Guid.NewGuid(),
        StartsAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        EndsAt = DateTimeOffset.UtcNow.AddMinutes(endsInMinutes),
        OpeningPriceMinorUnits = 1_000_000_00,
        ReservePriceMinorUnits = 1_500_000_00,
        Increment = new IncrementPolicy.Fixed(50_000_00),
        QuietPeriod = TimeSpan.FromMinutes(2),
        MaxExtensions = 3,
        Channel = BidChannel.Onsite,
    };

    private static async Task<(ProcessorHarness H, AuctionDefinition D)> StartedAsync(
        AuctionDefinition? definition = null)
    {
        var h = new ProcessorHarness(Grace);
        var d = definition ?? Hall();
        await h.PublishApprovalAsync(d);
        await h.PublishReserveAsync(d);
        await h.RecoverAsync();
        return (h, d);
    }

    // --- the clock does not run the hall ------------------------------------

    [Fact]
    public async Task A_hall_auction_is_not_closed_by_the_clock()
    {
        // The one that matters. An auction closed on time while the auctioneer is
        // still calling for bids would record a winner the room never heard.
        var (h, d) = await StartedAsync();
        await using var owned = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await h.Supervisor.TickAsync(d.EndsAt.AddHours(2), h.Token);

        Assert.Empty(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Empty(await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
    }

    [Fact]
    public async Task An_online_auction_is_still_closed_by_the_clock()
    {
        // The control. Without it the test above would pass just as well if the
        // close had been broken for every channel.
        var (h, d) = await StartedAsync(Hall() with { Channel = BidChannel.Online });
        await using var owned = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await h.Supervisor.TickAsync(d.EndsAt + Grace, h.Token);

        Assert.Single(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
    }

    [Fact]
    public async Task A_bid_near_the_end_does_not_extend_a_hall_auction()
    {
        // Anti-sniping is a remedy for not having a person in charge. Moving an end
        // time the auctioneer has just announced to the room is worse than useless.
        var (h, d) = await StartedAsync(Hall(endsInMinutes: 1));
        await using var owned = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);

        var engine = h.Running(d.AuctionId).Pump!.Engine;
        Assert.Equal(0, engine.ExtensionsUsed);
        Assert.Equal(d.EndsAt, engine.EffectiveEndsAt);
    }

    // --- the clerk's two verbs ----------------------------------------------

    [Fact]
    public async Task The_clerk_moves_the_end_time()
    {
        var (h, d) = await StartedAsync();
        await using var owned = h;
        var clerk = Guid.NewGuid();

        await h.PublishClerkExtensionAsync(d.AuctionId, clerk, 300);
        await h.DrainLifecycleAsync();

        var engine = h.Running(d.AuctionId).Pump!.Engine;
        Assert.Equal(1, engine.ExtensionsUsed);
        Assert.Equal(d.EndsAt.AddSeconds(300), engine.EffectiveEndsAt);
    }

    [Fact]
    public async Task The_clerk_cannot_extend_past_the_published_cap()
    {
        // MaxExtensions is a term of the auction that bidders read before paying a
        // deposit. The auctioneer decides when to extend, not how many times the
        // terms allow.
        var (h, d) = await StartedAsync();
        await using var owned = h;
        var clerk = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            await h.PublishClerkExtensionAsync(d.AuctionId, clerk, 60);
        await h.DrainLifecycleAsync();

        var engine = h.Running(d.AuctionId).Pump!.Engine;
        Assert.Equal(d.MaxExtensions, engine.ExtensionsUsed);
        Assert.Equal(d.EndsAt.AddSeconds(60 * d.MaxExtensions), engine.EffectiveEndsAt);
    }

    [Fact]
    public async Task The_hammer_closes_it_and_offers_the_candidate()
    {
        // Everything downstream of the close is the same workflow whichever channel
        // sold the land: the committee gets a candidate, and the cascade can walk.
        var (h, d) = await StartedAsync();
        await using var owned = h;
        var winner = Guid.NewGuid();

        await h.BidAsync(d, winner, 1_800_000_00, DateTimeOffset.UtcNow);
        await h.PublishClerkCloseAsync(d.AuctionId, Guid.NewGuid());
        await h.DrainLifecycleAsync();

        Assert.Single(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        var offered = Assert.Single(await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
        Assert.Equal(winner, offered.BidderId);
        Assert.Equal(1_800_000_00, offered.AmountMinorUnits);
    }

    [Fact]
    public async Task Pressing_the_hammer_twice_closes_once()
    {
        // At-least-once delivery, and a clerk whose first click did not visibly do
        // anything will click again.
        var (h, d) = await StartedAsync();
        await using var owned = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await h.PublishClerkCloseAsync(d.AuctionId, Guid.NewGuid());
        await h.PublishClerkCloseAsync(d.AuctionId, Guid.NewGuid());
        await h.DrainLifecycleAsync();

        Assert.Single(await h.LifecycleOfAsync<AuctionClosed>(nameof(AuctionClosed)));
        Assert.Single(await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered)));
    }

    [Fact]
    public async Task A_bid_after_the_hammer_is_refused()
    {
        var (h, d) = await StartedAsync();
        await using var owned = h;

        await h.BidAsync(d, Guid.NewGuid(), 1_800_000_00, DateTimeOffset.UtcNow);
        await h.PublishClerkCloseAsync(d.AuctionId, Guid.NewGuid());
        await h.DrainLifecycleAsync();

        // The engine's own cutoff still stands: the close moved nothing about the
        // end time, so a later bid is past it.
        await h.BidAsync(d, Guid.NewGuid(), 1_900_000_00, d.EndsAt.AddMinutes(5));

        var rejected = await h.CountOnAsync(Topics.BidsRejected);
        Assert.Equal(1, rejected);
    }

    // --- the ladder survives, which is the whole point ----------------------

    [Fact]
    public async Task The_cascade_still_walks_a_hall_auction_s_ladder()
    {
        // "We already know who wins" is true until the committee disqualifies them.
        // This is why the processor keeps recording a hall auction rather than
        // stepping aside from it.
        var (h, d) = await StartedAsync();
        await using var owned = h;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await h.BidAsync(d, second, 1_600_000_00, DateTimeOffset.UtcNow);
        await h.BidAsync(d, first, 1_800_000_00, DateTimeOffset.UtcNow);

        await h.PublishClerkCloseAsync(d.AuctionId, Guid.NewGuid());
        await h.DrainLifecycleAsync();

        await h.PublishDisqualificationAsync(d.AuctionId, first);
        await h.DrainLifecycleAsync();

        var offers = await h.LifecycleOfAsync<CandidateOffered>(nameof(CandidateOffered));
        Assert.Equal(2, offers.Count);
        Assert.Equal(first, offers[0].BidderId);
        Assert.Equal(second, offers[1].BidderId);
        Assert.Equal(1_600_000_00, offers[1].AmountMinorUnits);
    }
}
