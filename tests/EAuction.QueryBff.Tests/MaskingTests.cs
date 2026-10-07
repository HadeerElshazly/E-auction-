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

        Assert.Equal("مزايد #1", aliases.For(auction, Guid.NewGuid()));
        Assert.Equal("مزايد #2", aliases.For(auction, Guid.NewGuid()));
        Assert.Equal("مزايد #3", aliases.For(auction, Guid.NewGuid()));
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

        Assert.Equal("مزايد #1", aliases.For(first, sara));
        Assert.Equal("مزايد #1", aliases.For(second, khalid));

        // Sara is #1 in one auction and #2 in the other: the number says nothing
        // about who she is.
        Assert.Equal("مزايد #2", aliases.For(second, sara));
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
                // YourWinningBidId is the one exception: it is the recipient's own
                // bid id, set only on the variant sent to that one bidder, and the
                // tests below assert it never appears on anyone else's.
                .Where(p => p.Name != nameof(LivePrice.YourWinningBidId))
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
        state.SetPrice(Guid.NewGuid(), 1_100_000_00, Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, 0);

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
        state.SetPrice(id, 1_200_000_00, leader, Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(5), 2);

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

        state.SetPrice(id, 1_000_000_00, Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, 0);

        Assert.True(state.TryGet(id, out var bidOn));
        Assert.Equal(1_050_000_00, bidOn.MinimumNextBidMinorUnits);
    }
}

/// <summary>
/// The two variants of a price update. These are the only place the leader-only
/// fields can escape from, which is why there is exactly one pair of builders.
/// </summary>
public class LiveViewTests
{
    private static AuctionEntry Led(Guid leader, Guid winningBid) => new()
    {
        AuctionId = Guid.NewGuid(),
        NameAr = "أ", NameEn = "A", Channel = "Online",
        StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1),
        OpeningPriceMinorUnits = 1_000_000_00,
        MinIncrementMinorUnits = 50_000_00,
        DepositMinorUnits = 1, BookletPriceMinorUnits = 1,
        MaxExtensions = 3, TotalAreaSqm = 1m, Plots = [],
        Status = "Live",
        PriceMinorUnits = 1_200_000_00,
        LeaderBidderId = leader,
        LeaderClientBidId = winningBid
    };

    [Fact]
    public void The_others_variant_carries_no_winning_bid_id()
    {
        // The field this exists to contain. It is the leading bidder's own
        // identifier for their bid, and it must not reach anyone else.
        var leader = Guid.NewGuid();
        var winningBid = Guid.NewGuid();

        var view = LiveViews.ForOthers(Led(leader, winningBid), "#1", DateTimeOffset.UtcNow);

        Assert.Null(view.YourWinningBidId);
        Assert.False(view.LeaderIsYou);
        Assert.DoesNotContain(winningBid.ToString(), LiveViews.Serialise(view));
        Assert.DoesNotContain(leader.ToString(), LiveViews.Serialise(view));
    }

    [Fact]
    public void The_leader_variant_names_the_bid_that_won()
    {
        // So a bidder is told which of their bids took the lead, rather than
        // inferring it from a price that happens to match the amount they typed.
        var leader = Guid.NewGuid();
        var winningBid = Guid.NewGuid();

        var view = LiveViews.ForLeader(Led(leader, winningBid), "#1", DateTimeOffset.UtcNow);

        Assert.Equal(winningBid, view.YourWinningBidId);
        Assert.True(view.LeaderIsYou);

        // Still not the leader's bidder id: the alias is what identifies them.
        Assert.DoesNotContain(leader.ToString(), LiveViews.Serialise(view));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_per_caller_view_picks_the_variant_by_who_is_asking(bool callerLeads)
    {
        var leader = Guid.NewGuid();
        var entry = Led(leader, Guid.NewGuid());
        var caller = callerLeads ? leader : Guid.NewGuid();

        var view = LiveViews.For(entry, caller, "#1", DateTimeOffset.UtcNow);

        Assert.Equal(callerLeads, view.LeaderIsYou);
        Assert.Equal(callerLeads, view.YourWinningBidId is not null);
    }

    [Fact]
    public void An_anonymous_caller_never_leads()
    {
        // Guards a null-equals-null slip: the caller is null when anonymous, and
        // LeaderBidderId is null before the first bid.
        var noBidsYet = Led(Guid.NewGuid(), Guid.NewGuid()) with
        {
            LeaderBidderId = null,
            LeaderClientBidId = null,
            PriceMinorUnits = null
        };

        var view = LiveViews.For(noBidsYet, caller: null, label: null, DateTimeOffset.UtcNow);

        Assert.False(view.LeaderIsYou);
        Assert.Null(view.YourWinningBidId);

        // And with no bid, the floor to beat is the opening price.
        Assert.Equal(1_000_000_00, view.MinimumNextBidMinorUnits);
    }
}

/// <summary>
/// The administrator's choice between masking bidders and naming them (D-22), and
/// the two locks that keep a masked auction's names out of this service.
///
/// The asymmetry is the point. Masking is the default and has to survive every way
/// of getting it wrong — an unset field, a value nobody recognises, a name that
/// arrived before the auction did. Naming has to be an explicit act that the
/// administrator took, and nothing else.
/// </summary>
public class BidderVisibilityTests
{
    private static readonly Guid Auction = Guid.NewGuid();
    private static readonly Guid Sara = Guid.NewGuid();

    private static AuctionEntry Entry(string visibility, Guid? leader = null) => new()
    {
        AuctionId = Auction,
        NameAr = "أ", NameEn = "A", Channel = "Online",
        BidderVisibility = visibility,
        StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1),
        OpeningPriceMinorUnits = 1_000_000_00, MinIncrementMinorUnits = 50_000_00,
        DepositMinorUnits = 1, BookletPriceMinorUnits = 1,
        MaxExtensions = 3, TotalAreaSqm = 1m, Plots = [],
        Status = "Live",
        PriceMinorUnits = 1_200_000_00,
        LeaderBidderId = leader ?? Sara,
    };

    private static LeaderLabels Labels(out BidderNames names)
    {
        names = new BidderNames();
        return new LeaderLabels(new BidderAliases(), names);
    }

    // --- the default --------------------------------------------------------

    [Fact]
    public void A_masked_auction_shows_a_pseudonym()
    {
        var labels = Labels(out _);

        Assert.Equal("مزايد #1", labels.For(Entry("Masked")));
    }

    [Fact]
    public void An_auction_with_no_visibility_at_all_is_masked()
    {
        // An event from an older producer, or a field someone forgot. The absence of
        // an instruction is not an instruction to publish a citizen's name.
        var labels = Labels(out _);

        Assert.Equal("مزايد #1", labels.For(Entry("")));
    }

    [Fact]
    public void An_unrecognised_visibility_is_masked()
    {
        var labels = Labels(out _);

        foreach (var value in new[] { "Public", "true", "1", "Name", "Unmasked" })
            Assert.Equal("مزايد #1", labels.For(Entry(value)));
    }

    [Fact]
    public void The_recognised_value_is_matched_whatever_its_case()
    {
        // "Named" is how the admin service spells it, but the topic is read as a
        // stranger would read it and a producer may not match the casing exactly.
        var labels = Labels(out var names);

        foreach (var value in new[] { "Named", "named", "NAMED" })
        {
            var entry = Entry(value);
            Assert.True(names.Remember(entry, Sara, "سارة الحربي"));
            Assert.Equal("سارة الحربي", labels.For(entry));
        }
    }

    [Fact]
    public void A_masked_auction_refuses_to_remember_a_name_it_is_sent()
    {
        // The second lock. The participant service does not publish a name for a
        // masked auction — but if a future producer did, this service must not keep
        // it. Two services would have to fail together for a name to surface.
        var labels = Labels(out var names);
        var masked = Entry("Masked");

        Assert.False(names.Remember(masked, Sara, "سارة الحربي"));
        Assert.Equal(0, names.Count);
        Assert.Equal("مزايد #1", labels.For(masked));
    }

    // --- the administrator's choice -----------------------------------------

    [Fact]
    public void A_named_auction_shows_the_bidder_s_name()
    {
        var labels = Labels(out var names);
        var named = Entry("Named");

        Assert.True(names.Remember(named, Sara, "سارة الحربي"));

        Assert.Equal("سارة الحربي", labels.For(named));
    }

    [Fact]
    public void A_named_auction_falls_back_to_the_pseudonym_until_the_name_arrives()
    {
        // The two topics are followed independently, so a price can beat an
        // eligibility. Showing a pseudonym for a moment is harmless; throwing, or
        // showing an empty label where a name should be, is not.
        var labels = Labels(out _);

        Assert.Equal("مزايد #1", labels.For(Entry("Named")));
    }

    [Fact]
    public void A_revoked_bidder_s_name_is_forgotten()
    {
        var labels = Labels(out var names);
        var named = Entry("Named");
        names.Remember(named, Sara, "سارة الحربي");

        names.Forget(Auction, Sara);

        Assert.Equal(0, names.Count);
        Assert.Equal("مزايد #1", labels.For(named));
    }

    [Fact]
    public void A_blank_name_does_not_become_the_label()
    {
        // Otherwise a named auction whose producer sent an empty string would render
        // "المزايد الأعلى: " with nothing after it.
        var labels = Labels(out var names);
        var named = Entry("Named");

        Assert.False(names.Remember(named, Sara, "   "));
        Assert.Equal("مزايد #1", labels.For(named));
    }

    // --- what neither answer changes ----------------------------------------

    [Fact]
    public void Naming_bidders_does_not_reveal_the_leader_to_the_type_system()
    {
        // Naming is a decision about the label, not a hole in the shape: the view
        // still has exactly one slot and still carries no bidder id, so the other
        // half of D-22 — and all of D-23 — is untouched by this setting.
        var labels = Labels(out var names);
        var named = Entry("Named");
        names.Remember(named, Sara, "سارة الحربي");

        var view = LiveViews.ForOthers(named, labels.For(named), DateTimeOffset.UtcNow);
        var json = LiveViews.Serialise(view);

        // The label is asserted on the view, not on the JSON: the serialiser escapes
        // non-ASCII, so every Arabic string reaches the wire as \uXXXX. Searching the
        // JSON for Arabic therefore finds nothing whether the feature works or not —
        // the same shape of mistake as the D-23 leak check that looked for Latin
        // digits on an Arabic-Indic page.
        Assert.Equal("سارة الحربي", view.LeaderLabel);

        // Absence is what the serialised form is good for, and what matters here.
        Assert.DoesNotContain(Sara.ToString(), json);
        Assert.DoesNotContain("reserve", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_auction_with_no_leader_has_no_label_either_way()
    {
        var labels = Labels(out var names);
        var named = Entry("Named") with { LeaderBidderId = null };
        names.Remember(Entry("Named"), Sara, "سارة الحربي");

        Assert.Null(labels.For(named));
        Assert.Null(labels.For(Entry("Masked") with { LeaderBidderId = null }));
    }

    // --- what a visitor who never signed in is told -------------------------
    //
    // A citizen browsing public land listings sees the land: the plots, their
    // numbers and measurements, the description, the location, the documents. They
    // do not see who is bidding on it, in either visibility mode.

    [Fact]
    public void An_anonymous_caller_is_given_no_bidder_label_on_a_masked_auction()
    {
        var labels = Labels(out _);
        var masked = Entry("Masked", Sara);

        var view = LiveViews.For(masked, caller: null, labels.For(masked), DateTimeOffset.UtcNow);

        // Not even the pseudonym. «مزايد #2» names nobody, but it still counts
        // the distinct people in the room and announces each new arrival.
        Assert.Null(view.LeaderLabel);
        Assert.False(view.LeaderIsYou);
        Assert.Null(view.YourWinningBidId);
    }

    [Fact]
    public void An_anonymous_caller_is_given_no_bidder_name_on_a_named_auction()
    {
        var labels = Labels(out var names);
        var named = Entry("Named", Sara);
        names.Remember(named, Sara, "سارة الحربي");

        // The label the server computed is a real citizen's name — proof this test
        // is not passing because the auction happens to have no label to give.
        Assert.Equal("سارة الحربي", labels.For(named));

        var view = LiveViews.For(named, caller: null, labels.For(named), DateTimeOffset.UtcNow);

        Assert.Null(view.LeaderLabel);
        Assert.DoesNotContain(Sara.ToString(), LiveViews.Serialise(view));
    }

    [Fact]
    public void A_signed_in_bidder_still_sees_the_label_the_auction_allows()
    {
        // The rule is about being anonymous, not about hiding the leader from the
        // people bidding: D-22 still names bidders to each other on a Named auction
        // and pseudonymises them on a masked one.
        var labels = Labels(out var names);
        var named = Entry("Named", Sara);
        names.Remember(named, Sara, "سارة الحربي");

        var khalid = Guid.NewGuid();
        var view = LiveViews.For(named, khalid, labels.For(named), DateTimeOffset.UtcNow);

        Assert.Equal("سارة الحربي", view.LeaderLabel);
    }

    [Fact]
    public void The_price_and_the_clock_still_reach_an_anonymous_caller()
    {
        // The other half of the requirement: a visitor is denied the bidders, not
        // the auction. A rule enforced by returning nothing would pass the test
        // above and break the catalogue.
        var labels = Labels(out _);
        var masked = Entry("Masked", Sara) with { PriceMinorUnits = 1_200_000_00 };

        var view = LiveViews.For(masked, caller: null, labels.For(masked), DateTimeOffset.UtcNow);

        Assert.Equal(1_200_000_00, view.PriceMinorUnits);
        Assert.Equal(masked.Status, view.Status);
        Assert.Equal(masked.MinimumNextBidMinorUnits, view.MinimumNextBidMinorUnits);
    }
}
