using EAuction.QueryBff;
using Xunit;

namespace EAuction.QueryBff.Tests;

/// <summary>
/// The privacy guarantees of the public read path: D-22 (nobody learns who is
/// leading) and D-23 (nobody learns the reserve). Both are the kind of property
/// that is easy to hold today and easy to lose in a later refactor, which is what
/// makes them worth a test rather than a comment.
/// </summary>
public class MaskingTests
{
    [Fact]
    public void An_alias_is_stable_within_an_auction()
    {
        var aliases = new BidderAliases();
        var auction = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        // Otherwise the price panel would renumber the leader on every poll.
        Assert.Equal(aliases.For(auction, bidder), aliases.For(auction, bidder));
    }

    [Fact]
    public void Aliases_are_numbered_in_the_order_bidders_first_lead()
    {
        var aliases = new BidderAliases();
        var auction = Guid.NewGuid();

        Assert.Equal("#1", aliases.For(auction, Guid.NewGuid()));
        Assert.Equal("#2", aliases.For(auction, Guid.NewGuid()));
        Assert.Equal("#3", aliases.For(auction, Guid.NewGuid()));
    }

    [Fact]
    public void The_same_bidder_gets_an_unrelated_alias_in_a_different_auction()
    {
        // The point of per-auction numbering. Under one global sequence, the same
        // alias turning up in two auctions would tell a watcher the two leaders are
        // the same person — which is precisely the collusion signal the masking is
        // supposed to remove.
        var aliases = new BidderAliases();
        var sara = Guid.NewGuid();
        var khalid = Guid.NewGuid();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // Khalid leads first in the second auction, Sara leads first in the first.
        aliases.For(first, sara);
        aliases.For(second, khalid);

        Assert.Equal("#1", aliases.For(first, sara));
        Assert.Equal("#1", aliases.For(second, khalid));

        // Sara is #1 in one auction and #2 in the other: the number says nothing
        // about who she is.
        Assert.Equal("#2", aliases.For(second, sara));
    }

    [Fact]
    public void No_response_type_can_expose_the_leading_bidder_id()
    {
        // Structural, not behavioural. The read model holds the raw id because the
        // "is the leader you?" answer needs it, so the guarantee is that no shape
        // that gets serialised has anywhere to put it.
        foreach (var type in new[]
                 { typeof(AuctionSummary), typeof(AuctionDetail), typeof(LivePrice) })
        {
            var leaky = type.GetProperties()
                .Where(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?))
                .Where(p => p.Name.Contains("Bidder", StringComparison.OrdinalIgnoreCase)
                         || p.Name.Contains("Leader", StringComparison.OrdinalIgnoreCase)
                         || p.Name.Contains("Winner", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToArray();

            Assert.True(leaky.Length == 0,
                $"{type.Name} exposes a bidder identity: {string.Join(", ", leaky)}. "
                + "The public read path masks the leader (D-22) — serve an alias.");
        }
    }

    [Fact]
    public void No_response_type_can_expose_a_reserve_price()
    {
        foreach (var type in new[]
                 { typeof(AuctionSummary), typeof(AuctionDetail), typeof(LivePrice) })
        {
            var leaky = type.GetProperties()
                .Where(p => p.Name.Contains("Reserve", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToArray();

            Assert.True(leaky.Length == 0,
                $"{type.Name} exposes {string.Join(", ", leaky)}. The reserve lives on "
                + "auctions.sealed, which this service does not consume (D-23).");
        }
    }
}

/// <summary>
/// The read model's update rules. The ordering problem is real: auctions.upcoming
/// and auctions.current-winner are separate compacted topics replayed concurrently,
/// so on a cold start the price can arrive before the definition, or the definition
/// can replay again after a price is already known.
/// </summary>
public class CatalogueStateTests
{
    private static AuctionEntry Definition(Guid id, string nameEn = "A") => new()
    {
        AuctionId = id,
        NameAr = "أ", NameEn = nameEn, Channel = "Online",
        StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1),
        OpeningPriceMinorUnits = 1_000_000_00,
        MinIncrementMinorUnits = 50_000_00,
        DepositMinorUnits = 100_000_00,
        BookletPriceMinorUnits = 1_000_00,
        MaxExtensions = 3, TotalAreaSqm = 600m, Plots = []
    };

    [Fact]
    public void A_price_for_an_unknown_auction_is_dropped_not_invented()
    {
        // current-winner can replay before upcoming does. Creating a shell entry from
        // the price alone would publish an auction with no name, no plots and no
        // deposit — and the catalogue would show it.
        var state = new CatalogueState();
        state.SetPrice(Guid.NewGuid(), 1_100_000_00, Guid.NewGuid(), DateTimeOffset.UtcNow, 0);

        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void A_definition_replay_does_not_roll_back_the_live_price()
    {
        // The compacted definition topic can replay at any time. If an upsert reset
        // the price, a bidder would briefly see the opening price during an auction
        // that had already run to 1.2M, and the portal would offer to bid there.
        var state = new CatalogueState();
        var id = Guid.NewGuid();
        var leader = Guid.NewGuid();

        state.Upsert(Definition(id));
        state.SetStatus(id, "Live");
        state.SetPrice(id, 1_200_000_00, leader, DateTimeOffset.UtcNow.AddMinutes(5), 2);

        state.Upsert(Definition(id, nameEn: "A, corrected"));

        Assert.True(state.TryGet(id, out var entry));
        Assert.Equal("A, corrected", entry.NameEn);
        Assert.Equal(1_200_000_00, entry.PriceMinorUnits);
        Assert.Equal(leader, entry.LeaderBidderId);
        Assert.Equal("Live", entry.Status);
        Assert.Equal(2, entry.ExtensionsUsed);
    }

    [Fact]
    public void The_minimum_next_bid_is_the_opening_price_until_a_bid_is_judged()
    {
        // Not zero, and not the opening price plus an increment: the first valid bid
        // is AT the opening price. The portal uses this to prefill its bid box, so
        // getting it wrong means every first bid is rejected by the catcher.
        var state = new CatalogueState();
        var id = Guid.NewGuid();
        state.Upsert(Definition(id));

        Assert.True(state.TryGet(id, out var fresh));
        Assert.Null(fresh.PriceMinorUnits);
        Assert.Equal(1_000_000_00, fresh.MinimumNextBidMinorUnits);

        state.SetPrice(id, 1_000_000_00, Guid.NewGuid(), DateTimeOffset.UtcNow, 0);

        Assert.True(state.TryGet(id, out var bidOn));
        Assert.Equal(1_050_000_00, bidOn.MinimumNextBidMinorUnits);
    }
}
