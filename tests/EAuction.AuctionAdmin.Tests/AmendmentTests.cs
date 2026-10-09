using EAuction.AuctionAdmin.Domain;
using EAuction.Core;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// §6.5: a published auction is changed as an amendment. Bidders keep seeing what
/// was approved until the committee approves the change; the deposit and the booklet
/// price — what they paid on — never change; once the auction opens nothing does.
/// </summary>
public class AmendmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static Auction Published(bool scheduled = true)
    {
        var auction = Build.ReadyAuction(Now);
        auction.SubmitForReview(Now);
        auction.Approve(Now);
        if (scheduled) auction.MarkScheduled();
        auction.ClearEvents();
        return auction;
    }

    /// <summary>The same terms as Build.ReadyAuction, with the start moved a day later.</summary>
    private static void MoveStart(Auction auction, int days = 1, long deposit = 100_000_00, long booklet = 1_000_00,
        BidderVisibility visibility = BidderVisibility.Masked) =>
        auction.UpdateDetails(
            auction.NameAr, auction.NameEn, BidChannel.Online, visibility,
            Now.AddDays(7 + days), Now.AddDays(8 + days),
            2_000_000_00, null, 50_000_00, deposit, 2.5m, booklet, 120, 3, "Phase 1");

    [Fact]
    public void Editing_a_published_auction_opens_an_amendment_and_publishes_nothing()
    {
        var auction = Published();

        MoveStart(auction);

        Assert.Equal(AmendmentStatus.Editing, auction.Amendment);
        Assert.NotNull(auction.AmendedAt);
        // Still scheduled for everyone else, and nothing has left the service.
        Assert.Equal(AuctionStatus.Scheduled, auction.Status);
        Assert.Empty(auction.Events);
        Assert.Equal(Now.AddDays(8), auction.StartsAt);
    }

    [Fact]
    public void Saving_the_same_terms_again_is_not_an_amendment()
    {
        var auction = Published();

        MoveStart(auction, days: 0);

        Assert.Equal(AmendmentStatus.None, auction.Amendment);
    }

    [Fact]
    public void What_bidders_paid_on_cannot_change_once_published()
    {
        var auction = Published();

        var deposit = Assert.Throws<AuctionValidationException>(() => MoveStart(auction, deposit: 150_000_00));
        Assert.Contains(deposit.Problems, p => p.Contains("التأمين"));

        var booklet = Assert.Throws<AuctionValidationException>(() => MoveStart(auction, booklet: 0));
        Assert.Contains(booklet.Problems, p => p.Contains("سعر الكراسة"));

        var named = Assert.Throws<AuctionValidationException>(() => MoveStart(auction, visibility: BidderVisibility.Named));
        Assert.Contains(named.Problems, p => p.Contains("ظهور المزايدين"));

        // A refused edit leaves no half-opened amendment behind.
        Assert.Equal(AmendmentStatus.None, auction.Amendment);
        Assert.Equal(100_000_00, auction.DepositMinorUnits);
    }

    [Fact]
    public void An_amendment_is_submitted_approved_and_republished_whole()
    {
        var auction = Published();
        MoveStart(auction);
        auction.AttachBooklet(Guid.NewGuid());

        auction.SubmitForReview(Now.AddHours(1));
        Assert.Equal(AmendmentStatus.PendingReview, auction.Amendment);
        Assert.Equal(Now.AddHours(1), auction.SubmittedAt);
        Assert.Equal(AuctionStatus.Scheduled, auction.Status);

        // With the committee: nothing more is changed until it decides.
        Assert.Throws<AuctionValidationException>(() => auction.AttachCoverImage(Guid.NewGuid()));
        Assert.Throws<AuctionValidationException>(() => auction.Rename("x", "y", null));

        auction.Approve(Now.AddHours(2));

        Assert.Equal(AmendmentStatus.None, auction.Amendment);
        Assert.Null(auction.AmendedAt);
        Assert.Equal(AuctionStatus.Scheduled, auction.Status);

        // The definition again, on both topics, plus the notice that it changed.
        var approved = Assert.Single(auction.Events.OfType<AuctionApproved>());
        Assert.Equal(Now.AddDays(8), approved.StartsAt);
        Assert.Equal(auction.BookletDocumentId, approved.BookletDocumentId);
        Assert.Single(auction.Events.OfType<AuctionReserveSet>());
        var amended = Assert.Single(auction.Events.OfType<AuctionAmended>());
        Assert.Equal(Now.AddHours(2), amended.At);
        Assert.Equal("auction-lifecycle", amended.AggregateType);
    }

    [Fact]
    public void A_refused_amendment_goes_back_to_editing_with_the_reason_and_publishes_nothing()
    {
        var auction = Published();
        MoveStart(auction);
        auction.SubmitForReview(Now);

        auction.Reject("الموعد الجديد يتعارض مع مزاد آخر");

        Assert.Equal(AmendmentStatus.Editing, auction.Amendment);
        Assert.Equal("الموعد الجديد يتعارض مع مزاد آخر", auction.RejectionReason);
        Assert.Equal(AuctionStatus.Scheduled, auction.Status);
        Assert.Empty(auction.Events);

        // Corrected and resubmitted, the reason clears.
        MoveStart(auction, days: 2);
        auction.SubmitForReview(Now);
        Assert.Null(auction.RejectionReason);
        Assert.Equal(AmendmentStatus.PendingReview, auction.Amendment);
    }

    [Fact]
    public void Nothing_is_submitted_or_approved_unless_an_amendment_is_open()
    {
        var auction = Published();

        Assert.Throws<InvalidAuctionTransitionException>(() => auction.SubmitForReview(Now));
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.Approve(Now));
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.Reject("سبب"));

        MoveStart(auction);
        // Open but not yet submitted: the committee has nothing before it.
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.Approve(Now));
    }

    [Fact]
    public void Nothing_is_amendable_once_the_auction_has_opened()
    {
        var auction = Published();
        MoveStart(auction);
        auction.SubmitForReview(Now);
        auction.MarkLive();
        auction.ClearEvents();

        // The processor opened it on the published terms before the committee
        // decided: the amendment can neither be approved nor continued.
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.Approve(Now));
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.ReplacePlot(new Plot(auction.Id, "SA-0002", 700m)));
        Assert.Throws<InvalidAuctionTransitionException>(() => auction.Rename("x", "y", null));
        Assert.Equal(AmendmentStatus.PendingReview, auction.Amendment);
        Assert.Empty(auction.Events);
    }

    [Fact]
    public void A_cancellation_still_withdraws_an_auction_under_amendment()
    {
        var auction = Published();
        MoveStart(auction);

        auction.Cancel("سبب", Build.Admin, Now);

        Assert.Equal(AuctionStatus.Cancelled, auction.Status);
        Assert.Single(auction.Events.OfType<AuctionCancelled>());
    }

    [Fact]
    public void Files_and_the_plot_amend_a_published_auction_too()
    {
        var auction = Published();
        var plot = auction.Plots.Single();

        auction.ReplacePlot(new Plot(auction.Id, plot.PlotNumber, 700m));
        Assert.Equal(AmendmentStatus.Editing, auction.Amendment);

        var again = Published();
        again.AddAttachment(Guid.NewGuid(), "المخطط المعتمد", "Document");
        Assert.Equal(AmendmentStatus.Editing, again.Amendment);

        // Attaching the booklet it already has changes nothing.
        var same = Published();
        same.AttachBooklet(same.BookletDocumentId!.Value);
        Assert.Equal(AmendmentStatus.None, same.Amendment);
    }

    [Fact]
    public void Approving_a_draft_still_works_as_before_and_an_amendment_cannot_start_from_one()
    {
        var auction = Build.ReadyAuction(Now);
        MoveStart(auction);
        Assert.Equal(AmendmentStatus.None, auction.Amendment);

        auction.SubmitForReview(Now);
        Assert.Equal(Now, auction.SubmittedAt);
        auction.Approve(Now);

        Assert.Equal(AuctionStatus.Approved, auction.Status);
        Assert.Single(auction.Events.OfType<AuctionApproved>());
        Assert.Empty(auction.Events.OfType<AuctionAmended>());
    }
}
