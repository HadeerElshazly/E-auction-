using EAuction.AuctionAdmin.Domain;
using EAuction.Core;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

public class PreparationWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_draft_reports_every_missing_piece_at_once()
    {
        var auction = Auction.CreateDraft(Build.Admin, "", "");
        var problems = auction.Validate(Now);

        Assert.Contains(problems, p => p.Contains("Arabic name"));
        Assert.Contains(problems, p => p.Contains("English name"));
        Assert.Contains(problems, p => p.Contains("قطعة أرض واحدة على الأقل"));
        Assert.Contains(problems, p => p.Contains("كراسة الشروط"));
        Assert.Contains(problems, p => p.Contains("Start and end"));
    }

    [Fact]
    public void A_complete_draft_validates_clean()
    {
        Assert.Empty(Build.ReadyAuction(Now).Validate(Now));
    }

    [Fact]
    public void Submitting_an_incomplete_auction_is_refused_with_the_reasons()
    {
        var auction = Auction.CreateDraft(Build.Admin, "اسم", "Name");
        var ex = Assert.Throws<AuctionValidationException>(() => auction.SubmitForReview(Now));

        Assert.NotEmpty(ex.Problems);
        Assert.Equal(AuctionStatus.Draft, auction.Status);
    }

    [Fact]
    public void A_reserve_below_the_opening_price_is_refused()
    {
        // A reserve under the opening price is cleared by the first valid bid,
        // so it does nothing at all. Almost always a data entry slip.
        var auction = Build.ReadyAuction(Now);
        auction.UpdateDetails(
            "اسم", "Name", BidChannel.Online, BidderVisibility.Masked,
            Now.AddDays(7), Now.AddDays(8),
            openingPriceMinorUnits: 1_000_000_00,
            reservePriceMinorUnits: 900_000_00,
            minIncrementMinorUnits: 50_000_00, depositMinorUnits: 100_000_00,
            brokerageFeePercent: 2.5m, bookletPriceMinorUnits: 1_000_00,
            quietPeriodSeconds: 120, maxExtensions: 3);

        Assert.Contains(auction.Validate(Now), p => p.Contains("below the opening price"));
    }

    [Fact]
    public void A_start_date_in_the_past_is_refused()
    {
        var auction = Build.ReadyAuction(Now);
        auction.UpdateDetails(
            "اسم", "Name", BidChannel.Online, BidderVisibility.Masked,
            Now.AddDays(-1), Now.AddDays(8),
            1_000_000_00, 1_500_000_00, 50_000_00, 100_000_00, 2.5m, 1_000_00, 120, 3);

        Assert.Contains(auction.Validate(Now), p => p.Contains("في المستقبل"));
    }

    [Fact]
    public void Two_plots_cannot_share_a_deed_number()
    {
        var auction = Build.ReadyAuction(Now);
        Assert.Throws<AuctionValidationException>(() =>
            auction.AddPlot(new Plot(auction.Id, "SA-0001", 100m)));
    }

    [Fact]
    public void An_auction_can_hold_several_plots_sold_as_one_package()
    {
        var auction = Build.ReadyAuction(Now);
        auction.AddPlot(new Plot(auction.Id, "SA-0002", 700m));
        auction.AddPlot(new Plot(auction.Id, "SA-0003", 800m));

        Assert.Equal(3, auction.Plots.Count);
        Assert.Equal(2150.5m, auction.TotalAreaSqm);
        Assert.Empty(auction.Validate(Now));
    }

    [Fact]
    public void Approval_raises_the_public_definition_and_the_reserve_as_separate_events()
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        Assert.Equal(AuctionStatus.Approved, auction.Status);

        var approved = Assert.IsType<AuctionApproved>(
            auction.Events.Single(e => e is AuctionApproved));
        var reserve = Assert.IsType<AuctionReserveSet>(
            auction.Events.Single(e => e is AuctionReserveSet));

        Assert.Equal(1_500_000_00, reserve.ReservePriceMinorUnits);
        Assert.Equal(1_000_000_00, approved.OpeningPriceMinorUnits);
        Assert.Equal(1, approved.PlotCount);

        // They go to different topics so the ACL, not a convention, keeps the
        // reserve away from the public read path.
        Assert.NotEqual(approved.AggregateType, reserve.AggregateType);
    }

    [Fact]
    public void The_public_approval_event_has_no_field_that_could_carry_the_reserve()
    {
        var properties = typeof(AuctionApproved).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(properties, n => n.Contains("Reserve", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(AuctionApproved.OpeningPriceMinorUnits), properties);
    }

    [Fact]
    public void An_auction_cannot_be_approved_straight_from_draft()
    {
        var auction = Build.ReadyAuction(Now);
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.Approve(Now));
    }

    [Fact]
    public void Editing_is_refused_once_the_auction_is_approved()
    {
        // Bidders have relied on the published terms. Changing the dates or
        // the deposit underneath them is not an edit — it is a different auction.
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        Assert.Throws<InvalidAuctionTransitionException>(() =>
            auction.UpdateDetails("x", "x", BidChannel.Online, BidderVisibility.Masked,
                Now.AddDays(9), Now.AddDays(10),
                1, 2, 1, 1, 0m, 0, null, 3));

        Assert.Throws<InvalidAuctionTransitionException>(() =>
            auction.AddPlot(new Plot(auction.Id, "SA-9999", 10m)));

        Assert.Throws<InvalidAuctionTransitionException>(() =>
            auction.AttachBooklet(Guid.NewGuid()));
    }

    [Fact]
    public void A_rejected_auction_records_the_reason_and_becomes_editable_again()
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Reject("الصور غير واضحة");

        Assert.Equal(AuctionStatus.Rejected, auction.Status);
        Assert.Equal("الصور غير واضحة", auction.RejectionReason);
        Assert.Single(auction.Events.OfType<AuctionRejected>());

        auction.AddPlot(new Plot(auction.Id, "SA-0002", 700m));
        auction.SubmitForReview(Now);

        Assert.Equal(AuctionStatus.PendingReview, auction.Status);
        Assert.Null(auction.RejectionReason);
    }

    [Fact]
    public void Rejecting_without_a_reason_is_refused()
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        Assert.Throws<AuctionValidationException>(() => auction.Reject("  "));
    }
}
