using EAuction.Core;
using EAuction.QueryBff;
using Xunit;

namespace EAuction.QueryBff.Tests;

/// <summary>
/// «إعدادات العرض للزوار»: what a visitor who has not signed in is answered. The
/// figures a setting hides are not sent at all — a page that only hid them would
/// leave them one network tab away.
/// </summary>
public class PublicVisibilityTests
{
    private static AuctionEntry Entry(long booklet = 1_000_00) => new()
    {
        AuctionId = Guid.NewGuid(),
        NameAr = "أ", NameEn = "A", Channel = "Online",
        StartsAt = DateTimeOffset.UtcNow.AddDays(1), EndsAt = DateTimeOffset.UtcNow.AddDays(2),
        OpeningPriceMinorUnits = 1_000_000_00,
        MinIncrementMinorUnits = 50_000_00,
        DepositMinorUnits = 100_000_00,
        BookletPriceMinorUnits = booklet,
        BrokerageFeePercent = 2.5m,
        MaxExtensions = 3, TotalAreaSqm = 600m,
        Plots = [new PlotEntry(Guid.NewGuid(), "310105", 600m, "24.8", "46.6", null, null, 20m, 30m, "Residential")],
        Attachments = [new PublicDocumentEntry(Guid.NewGuid(), "مخطط")],
    };

    [Fact]
    public void By_default_a_visitor_sees_the_land_the_deposit_and_the_schedule_and_nothing_else()
    {
        var a = Entry();
        var view = AuctionDetail.From(a, PublicVisibilityPolicy.Default);

        // Always: the land and what it costs to take part.
        Assert.Single(view.Plots);
        Assert.Equal(100_000_00, view.DepositMinorUnits);
        // The schedule is open by default — the visitor's row of the requirements.
        Assert.Equal(a.StartsAt, view.StartsAt);
        // Everything else waits for the sign-in.
        Assert.Null(view.OpeningPriceMinorUnits);
        Assert.Null(view.MinIncrementMinorUnits);
        Assert.Null(view.BrokerageFeePercent);
        Assert.Null(view.BookletPriceMinorUnits);
        Assert.Null(view.MaxExtensions);
        Assert.Empty(view.Attachments);
        Assert.Contains(PublicFields.OpeningPrice, view.Hidden);
        Assert.DoesNotContain(PublicFields.Schedule, view.Hidden);
    }

    [Fact]
    public void A_free_booklet_is_always_said_to_be_free()
    {
        var view = AuctionSummary.From(Entry(booklet: 0), PublicVisibilityPolicy.Default);
        Assert.Equal(0, view.BookletPriceMinorUnits);
    }

    [Fact]
    public void An_administrator_s_choice_opens_a_group_and_closes_another()
    {
        var policy = PublicVisibilityPolicy.From(new Dictionary<string, bool>
        {
            [PublicFields.OpeningPrice] = true,
            [PublicFields.Schedule] = false,
        });
        var view = AuctionSummary.From(Entry(), policy);

        Assert.Equal(1_000_000_00, view.OpeningPriceMinorUnits);
        Assert.Null(view.StartsAt);
        Assert.Contains(PublicFields.Schedule, view.Hidden);
    }

    [Fact]
    public void A_signed_in_caller_sees_everything()
    {
        var view = AuctionDetail.From(Entry());
        Assert.NotNull(view.OpeningPriceMinorUnits);
        Assert.NotEmpty(view.Attachments);
        Assert.Empty(view.Hidden);
    }

    [Fact]
    public void An_unknown_key_is_dropped_and_a_missing_one_takes_its_default() =>
        Assert.Equal(
            PublicVisibilityPolicy.Default.Public.OrderBy(p => p.Key),
            PublicVisibilityPolicy.From(new Dictionary<string, bool> { ["reserve"] = true }).Public.OrderBy(p => p.Key));
}
