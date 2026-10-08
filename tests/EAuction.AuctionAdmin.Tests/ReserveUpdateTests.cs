using EAuction.AuctionAdmin.Domain;
using EAuction.Core;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// The reserve price is write-only, so an update has to be able to say "leave it".
///
/// Found by building the admin portal: no read path returns the reserve — it lives
/// on auctions.sealed and is deliberately absent from AuctionResponse (D-23) — so
/// the editor cannot prefill it. With a required field, correcting a typo in the
/// auction's name meant retyping the reserve from a piece of paper, and a wrong
/// figure would silently replace the real one on the number the whole auction turns
/// on.
/// </summary>
public class ReserveUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(3));

    private static void Edit(Auction auction, long? reserve, string nameAr = "اسم محدَّث") =>
        auction.UpdateDetails(
            nameAr: nameAr,
            nameEn: "Updated",
            channel: BidChannel.Online,
            bidderVisibility: BidderVisibility.Masked,
            startsAt: Now.AddDays(7),
            endsAt: Now.AddDays(8),
            openingPriceMinorUnits: 2_000_000_00,
            reservePriceMinorUnits: reserve,
            minIncrementMinorUnits: 50_000_00,
            depositMinorUnits: 100_000_00,
            brokerageFeePercent: 2.5m,
            bookletPriceMinorUnits: 1_000_00,
            quietPeriodSeconds: 120,
            maxExtensions: 3);

    [Fact]
    public void A_null_reserve_leaves_the_stored_one_alone()
    {
        var auction = Build.ReadyAuction(Now);
        Assert.Equal(1_500_000_00, auction.ReservePriceMinorUnits);

        Edit(auction, reserve: null);

        Assert.Equal("اسم محدَّث", auction.NameAr);
        Assert.Equal(1_500_000_00, auction.ReservePriceMinorUnits);
    }

    [Fact]
    public void A_null_reserve_does_not_become_zero()
    {
        // The failure that matters. A zero reserve passes nothing — validation
        // rejects it — but if it had been allowed through, every bid would clear the
        // reserve and the auction would award at the opening price.
        var auction = Build.ReadyAuction(Now);
        Edit(auction, reserve: null);

        Assert.NotEqual(0, auction.ReservePriceMinorUnits);
        Assert.Empty(auction.Validate(Now));
    }

    [Fact]
    public void A_supplied_reserve_still_replaces_the_stored_one()
    {
        var auction = Build.ReadyAuction(Now);
        Edit(auction, reserve: 1_750_000_00);

        Assert.Equal(1_750_000_00, auction.ReservePriceMinorUnits);
    }

    [Fact]
    public void An_approved_auction_still_publishes_the_reserve_it_kept()
    {
        // The reserve reaches the processor over auctions.sealed. An update that
        // quietly dropped it would make the auction unsellable at its real floor.
        var auction = Build.ReadyAuction(Now);
        Edit(auction, reserve: null);

        auction.SubmitForReview(Now);
        auction.Approve(Now);

        var sealedEvent = Assert.Single(auction.Events.OfType<AuctionReserveSet>());
        Assert.Equal(1_500_000_00, sealedEvent.ReservePriceMinorUnits);
    }
}
