using EAuction.AuctionAdmin.Domain;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// السعر الاحتياطي is the floor the opening price may be lowered to. An auction that
/// ends unsold is offered again as a new draft at a lower price — never below it.
/// </summary>
public class ReofferTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static Auction Unsold()
    {
        var a = Build.ReadyAuction(Now);   // opening 2,000,000 · reserve 1,500,000
        a.SubmitForReview(Now);
        a.Approve(Now);
        a.MarkScheduled();
        a.MarkLive();
        a.MarkClosing();
        a.MarkPendingEligibilityReview();
        a.MarkUnsold();
        return a;
    }

    [Fact]
    public void An_unsold_auction_is_offered_again_as_a_new_draft_at_a_lower_price()
    {
        var old = Unsold();
        var next = old.Reoffer(Build.Admin, 1_600_000_00);

        Assert.NotEqual(old.Id, next.Id);
        Assert.Equal(AuctionStatus.Draft, next.Status);
        Assert.Equal(1_600_000_00, next.OpeningPriceMinorUnits);
        Assert.Equal(old.ReservePriceMinorUnits, next.ReservePriceMinorUnits);
        Assert.Equal(old.Plots.Count, next.Plots.Count);
        Assert.All(next.Plots, p => Assert.Equal(next.Id, p.AuctionId));
        Assert.Null(next.StartsAt);
        // The first run stays exactly as it ended.
        Assert.Equal(AuctionStatus.Unsold, old.Status);
        Assert.Equal(2_000_000_00, old.OpeningPriceMinorUnits);
    }

    [Fact]
    public void Not_below_the_reserve_and_not_at_or_above_the_old_price()
    {
        var old = Unsold();
        var below = Assert.Throws<AuctionValidationException>(() => old.Reoffer(Build.Admin, 1_400_000_00));
        Assert.Contains(below.Problems, p => p.Contains("السعر الاحتياطي"));
        Assert.Throws<AuctionValidationException>(() => old.Reoffer(Build.Admin, 2_000_000_00));

        // Exactly the reserve is allowed: it is the floor, not below it.
        Assert.Equal(1_500_000_00, old.Reoffer(Build.Admin, 1_500_000_00).OpeningPriceMinorUnits);
    }

    [Fact]
    public void Only_an_unsold_auction_is_re_offered() =>
        Assert.Throws<InvalidAuctionTransitionException>(
            () => Build.ReadyAuction(Now).Reoffer(Build.Admin, 1_600_000_00));
}
