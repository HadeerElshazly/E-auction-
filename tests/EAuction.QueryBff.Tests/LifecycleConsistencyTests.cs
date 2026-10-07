using EAuction.QueryBff;
using Xunit;

namespace EAuction.QueryBff.Tests;

/// <summary>
/// The public catalogue must say the same thing about an auction's stage as the
/// administrators' screens. It froze at «بانتظار الترسية» once the committee acted,
/// because it applied only the processor's half of the lifecycle topic.
/// </summary>
public class LifecycleConsistencyTests
{
    private static AuctionEntry Definition(Guid id) => new()
    {
        AuctionId = id,
        NameAr = "أ", NameEn = "A", Channel = "Online",
        StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1),
        OpeningPriceMinorUnits = 1_000_000_00, MinIncrementMinorUnits = 50_000_00,
        DepositMinorUnits = 100_000_00, BookletPriceMinorUnits = 1_000_00,
        MaxExtensions = 3, TotalAreaSqm = 600m, Plots = []
    };

    private static string Run(params string[] events)
    {
        var state = new CatalogueState();
        var id = Guid.NewGuid();
        state.Upsert(Definition(id));
        foreach (var e in events) state.ApplyLifecycle(id, e);
        Assert.True(state.TryGet(id, out var entry));
        return entry.Status;
    }

    [Fact]
    public void An_award_confirmed_by_the_committee_reads_as_awarded() =>
        Assert.Equal("Awarded", Run("AuctionStarted", "AuctionClosed", "CandidateOffered", "AwardConfirmed"));

    [Fact]
    public void A_settled_sale_reads_as_settled() =>
        Assert.Equal("Settled",
            Run("AuctionStarted", "AuctionClosed", "CandidateOffered", "AwardConfirmed", "AuctionSettled"));

    [Fact]
    public void A_committee_that_ends_it_unsold_is_followed() =>
        Assert.Equal("Unsold", Run("AuctionStarted", "AuctionClosed", "CandidateOffered", "AuctionUnsold"));

    [Fact]
    public void After_a_disqualification_the_processors_suggestion_does_not_move_the_stage()
    {
        // auction-admin holds the auction until the committee refers it; so must this.
        Assert.Equal("WinnerDisqualified",
            Run("AuctionStarted", "AuctionClosed", "CandidateOffered", "AwardConfirmed",
                "WinnerDisqualified", "CandidateOffered"));

        Assert.Equal("PendingAward",
            Run("AuctionStarted", "AuctionClosed", "CandidateOffered", "AwardConfirmed",
                "WinnerDisqualified", "CandidateOffered", "NextBidderReferred"));
    }

    [Fact]
    public void A_clerks_extension_does_not_move_the_stage() =>
        Assert.Equal("Live", Run("AuctionStarted", "AuctionExtendedByClerk"));

    [Fact]
    public void Lifecycle_that_replays_before_the_definition_is_applied_once_it_arrives()
    {
        var state = new CatalogueState();
        var id = Guid.NewGuid();

        foreach (var e in new[] { "AuctionStarted", "AuctionClosed", "CandidateOffered", "AwardConfirmed" })
            Assert.Null(state.ApplyLifecycle(id, e));

        state.Upsert(Definition(id));

        Assert.True(state.TryGet(id, out var entry));
        Assert.Equal("Awarded", entry.Status);
    }
}
