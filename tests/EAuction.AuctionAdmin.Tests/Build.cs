using EAuction.AuctionAdmin.Domain;
using EAuction.Core;

namespace EAuction.AuctionAdmin.Tests;

internal static class Build
{
    public static readonly Guid Admin = Guid.NewGuid();
    public static readonly Guid Committee = Guid.NewGuid();

    /// <summary>An auction that passes validation — the baseline to break from.</summary>
    public static Auction ReadyAuction(
        DateTimeOffset now, int? quietSeconds = 120,
        BidderVisibility visibility = BidderVisibility.Masked)
    {
        var auction = Auction.CreateDraft(Admin, "أراضي مجمع السعيد قطعة 1", "Al-Saeed Plot 1");

        auction.UpdateDetails(
            nameAr: "أراضي مجمع السعيد قطعة 1",
            nameEn: "Al-Saeed Plot 1",
            channel: BidChannel.Online,
            bidderVisibility: visibility,
            startsAt: now.AddDays(7),
            endsAt: now.AddDays(8),
            openingPriceMinorUnits: 1_000_000_00,
            reservePriceMinorUnits: 1_500_000_00,
            minIncrementMinorUnits: 50_000_00,
            depositMinorUnits: 100_000_00,
            brokerageFeePercent: 2.5m,
            bookletPriceMinorUnits: 1_000_00,
            quietPeriodSeconds: quietSeconds,
            maxExtensions: 3,
            phase: "Phase 1");

        auction.AddPlot(new Plot(auction.Id, "SA-0001", 650.5m));
        auction.AttachBooklet(Guid.NewGuid());
        return auction;
    }

    /// <summary>Carries an auction from draft all the way to an open award.</summary>
    public static Auction AwaitingSettlement(DateTimeOffset now, out Guid winner)
    {
        var auction = ReadyAuction(now);
        auction.SubmitForReview(now);
        auction.Approve(now);
        auction.MarkScheduled();
        auction.MarkLive();
        auction.MarkClosing();
        auction.MarkPendingEligibilityReview();

        winner = Guid.NewGuid();
        auction.OfferCandidate(winner, 1_800_000_00);
        auction.ConfirmAward(Committee, now, TimeSpan.FromDays(5));
        auction.GenerateAwardLetter(Guid.NewGuid());
        auction.UploadSignedAwardLetter(Guid.NewGuid());
        auction.NotifyWinner(now);
        return auction;
    }

    /// <summary>
    /// Records the winner's full payment against a receipt — settlement refuses an
    /// award with anything still owed (الخاصية 11).
    /// </summary>
    public static void PayInFull(Auction auction, DateTimeOffset now) =>
        auction.RecordAwardPayment(
            auction.CurrentAward!.RemainingMinorUnits, now, "RCPT-0001", null, Committee, now);
}
