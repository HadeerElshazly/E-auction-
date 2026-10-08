using EAuction.AuctionAdmin.Domain;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// «اسم المزاد والمرحلة» edited on their own, and «حذف المسودة».
/// </summary>
public class NamingAndDeletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_draft_is_renamed_without_touching_its_terms()
    {
        var auction = Build.ReadyAuction(Now);
        var opening = auction.OpeningPriceMinorUnits;

        auction.Rename(" مخطط السعيد — قطعة 2 ", "Al-Saeed Plot 2", "  ");

        Assert.Equal("مخطط السعيد — قطعة 2", auction.NameAr);
        Assert.Equal("Al-Saeed Plot 2", auction.NameEn);
        Assert.Null(auction.Phase);
        Assert.Equal(opening, auction.OpeningPriceMinorUnits);
        Assert.Equal(AmendmentStatus.None, auction.Amendment);
    }

    [Fact]
    public void Renaming_a_published_auction_is_an_amendment_and_the_same_name_is_not()
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);

        auction.Rename(auction.NameAr, auction.NameEn, auction.Phase);
        Assert.Equal(AmendmentStatus.None, auction.Amendment);

        auction.Rename(auction.NameAr, auction.NameEn, "Phase 2");
        Assert.Equal(AmendmentStatus.Editing, auction.Amendment);
        Assert.Equal("Phase 2", auction.Phase);
    }

    [Fact]
    public void A_draft_and_a_rejected_auction_can_be_deleted_and_nothing_published_can()
    {
        var draft = Build.ReadyAuction(Now);
        draft.EnsureDeletable();

        draft.SubmitForReview(Now);
        Assert.Throws<InvalidAuctionTransitionException>(draft.EnsureDeletable);

        draft.Reject("ناقص");
        draft.EnsureDeletable();

        var published = Build.ReadyAuction(Now);
        published.SubmitForReview(Now);
        published.Approve(Now);
        Assert.Throws<InvalidAuctionTransitionException>(published.EnsureDeletable);
    }
}
