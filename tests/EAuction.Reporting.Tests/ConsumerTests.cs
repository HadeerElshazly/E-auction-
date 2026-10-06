using EAuction.Core;
using EAuction.Reporting.Domain;
using EAuction.Reporting.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Reporting.Tests;

/// <summary>
/// The read model, assembled from the four topics against a real Postgres.
///
/// What these are really about is replay. Every topic this service reads is
/// re-delivered from offset 0 on each restart, so the interesting question is never
/// "does one event land" but "does the hundredth delivery of it say the same thing
/// as the first".
/// </summary>
[Collection("reporting")]
public class ConsumerTests : IAsyncLifetime
{
    private ReportingDatabase _db = null!;
    private InMemoryEventStream _events = null!;

    public async Task InitializeAsync()
    {
        _db = await ReportingDatabase.CreateAsync();
        _events = new InMemoryEventStream();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private ReportingConsumer Consumer() =>
        new(_db.Factory, _events, NullLogger<ReportingConsumer>.Instance);

    /// <summary>
    /// Runs a consumer until the model says what the caller is waiting for.
    ///
    /// Started and stopped per call, which is also what a restart looks like — the
    /// replay tests below call it twice on the same database.
    /// </summary>
    private async Task DrainUntilAsync(
        Func<Persistence.ReportingDbContext, Task<bool>> satisfied, TimeSpan? within = null)
    {
        var consumer = Consumer();
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(15));

        await consumer.StartAsync(cts.Token);
        try
        {
            while (true)
            {
                await using var db = await _db.Factory.CreateDbContextAsync(cts.Token);
                if (await satisfied(db)) break;
                await Task.Delay(25, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The assertion that follows says more than a timeout does.
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    private Task DrainUntilOutcomeAsync(Guid auctionId, AuctionOutcome outcome) =>
        DrainUntilAsync(async db =>
            await db.Auctions.AnyAsync(a => a.AuctionId == auctionId && a.Outcome == outcome));

    private async Task<AuctionRecord?> AuctionAsync(Guid id)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.AuctionId == id);
    }

    [Fact]
    public async Task An_approved_auction_and_its_plots_are_recorded()
    {
        var id = Guid.NewGuid();
        await Publish.ApprovedAsync(_events, id);

        await DrainUntilAsync(async db => await db.Plots.CountAsync() == 2);

        var auction = await AuctionAsync(id);
        Assert.NotNull(auction);
        Assert.Equal("مخطط السعيد", auction!.NameAr);
        Assert.Equal("مخطط السعيد — المرحلة الأولى", auction.Phase);
        Assert.Equal(AuctionOutcome.Scheduled, auction.Outcome);
        Assert.Equal(2, auction.PlotCount);

        await using var db = await _db.Factory.CreateDbContextAsync();
        var deeds = await db.Plots.AsNoTracking().OrderBy(p => p.DeedNumber)
            .Select(p => p.DeedNumber).ToListAsync();
        Assert.Equal(["4/س/1200", "4/س/1201"], deeds);
    }

    [Fact]
    public async Task An_auction_walks_from_approved_to_settled()
    {
        var id = Guid.NewGuid();
        var winner = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.StartedAsync(_events, id);
        await Publish.ClosedAsync(_events, id, bids: 7, extensions: 2);
        await Publish.CandidateAsync(_events, id, winner, 1_200_000_00);
        await Publish.AwardedAsync(_events, id, winner, 1_200_000_00);
        await Publish.SettledAsync(_events, id, winner, 1_200_000_00);

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Settled);

        var auction = await AuctionAsync(id);
        Assert.Equal(AuctionOutcome.Settled, auction!.Outcome);
        Assert.Equal(1_200_000_00, auction.FinalPriceMinorUnits);
        Assert.Equal(winner, auction.WinnerBidderId);
        Assert.Equal(7, auction.BidCount);
        Assert.Equal(2, auction.ExtensionsUsed);
        Assert.NotNull(auction.StartedAt);
        Assert.NotNull(auction.ClosedAt);
        Assert.NotNull(auction.SettledAt);

        // The package rate, which is the only per-area figure that exists: an
        // auction sells its plots as one indivisible package (D-02).
        Assert.Equal(120_000.00m, auction.PricePerSqmMinorUnits);
    }

    [Fact]
    public async Task The_award_is_dated_by_the_committees_confirmation()
    {
        // Not by the compliance deadline, which is five business days later. The
        // event used to carry only the deadline, so every award in every report
        // would have been dated a week late — which is why ConfirmedAt was added.
        var id = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var confirmed = DateTimeOffset.UtcNow.AddDays(-3);

        await Publish.ApprovedAsync(_events, id);
        await Publish.AwardedAsync(_events, id, winner, 1_000_000_00, confirmedAt: confirmed);

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Awarded);

        var auction = await AuctionAsync(id);
        Assert.Equal(confirmed.ToUnixTimeSeconds(), auction!.AwardedAt!.Value.ToUnixTimeSeconds());
        Assert.NotEqual(
            auction.ComplianceDeadline!.Value.ToUnixTimeSeconds(),
            auction.AwardedAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task A_replay_does_not_move_a_settled_auction_back_to_live()
    {
        // The guard that makes this read model safe to rebuild. auctions.lifecycle
        // is replayed from offset 0 on every restart, so AuctionStarted arrives
        // again for an auction that settled months ago.
        var id = Guid.NewGuid();
        var winner = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.StartedAsync(_events, id);
        await Publish.ClosedAsync(_events, id);
        await Publish.AwardedAsync(_events, id, winner, 1_100_000_00);
        await Publish.SettledAsync(_events, id, winner, 1_100_000_00);

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Settled);

        // A second pass over exactly the same topics.
        await DrainUntilOutcomeAsync(id, AuctionOutcome.Settled);

        var auction = await AuctionAsync(id);
        Assert.Equal(AuctionOutcome.Settled, auction!.Outcome);
        Assert.Equal(1_100_000_00, auction.FinalPriceMinorUnits);
    }

    [Fact]
    public async Task A_replay_does_not_double_count_money()
    {
        // The reason the settlement ledger is keyed on the topic offset. Without it
        // a restart would report twice the brokerage and twice the deposits held.
        var id = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.SettlementAsync(_events, id, bidder, "Booklet", "Charged", 1_000_00);
        await Publish.SettlementAsync(_events, id, bidder, "Deposit", "Charged", 100_000_00);

        await DrainUntilAsync(async db => await db.Settlements.CountAsync() == 2);
        await DrainUntilAsync(async db => await db.Settlements.CountAsync() == 2);

        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Settlements.CountAsync());

        var held = await db.Bidders.AsNoTracking()
            .Where(x => x.AuctionId == id).SumAsync(x => x.DepositHeldMinorUnits);
        Assert.Equal(100_000_00, held);
    }

    [Fact]
    public async Task A_refund_releases_the_hold_and_a_forfeiture_does_not_read_as_one()
    {
        var id = Guid.NewGuid();
        var refunded = Guid.NewGuid();
        var defaulted = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.SettlementAsync(_events, id, refunded, "Deposit", "Charged", 100_000_00);
        await Publish.SettlementAsync(_events, id, defaulted, "Deposit", "Charged", 100_000_00);
        await Publish.SettlementAsync(_events, id, refunded, "Deposit", "Refunded", 100_000_00);
        await Publish.SettlementAsync(_events, id, defaulted, "Deposit", "Forfeited", 100_000_00);

        await DrainUntilAsync(async db => await db.Settlements.CountAsync() == 4);

        await using var db = await _db.Factory.CreateDbContextAsync();
        var rows = await db.Bidders.AsNoTracking().Where(x => x.AuctionId == id).ToListAsync();

        // Both holds are released, and the two are still distinguishable — a
        // forfeiture reported as a refund would overstate what went back to bidders.
        Assert.All(rows, r => Assert.Equal(0, r.DepositHeldMinorUnits));
        Assert.True(rows.Single(r => r.BidderId == defaulted).DepositForfeited);
        Assert.False(rows.Single(r => r.BidderId == refunded).DepositForfeited);
    }

    [Fact]
    public async Task A_disqualification_clears_the_price_until_the_cascade_lands()
    {
        // A revenue report that counted a disqualified award would report money the
        // municipality never received.
        var id = Guid.NewGuid();
        var defaulter = Guid.NewGuid();
        var second = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.ClosedAsync(_events, id);
        await Publish.AwardedAsync(_events, id, defaulter, 1_300_000_00);
        await Publish.DisqualifiedAsync(_events, id, defaulter);

        await DrainUntilAsync(async db =>
            await db.Bidders.AnyAsync(x => x.AuctionId == id && x.DisqualifiedAt != null));

        var afterDisqualification = await AuctionAsync(id);
        Assert.Null(afterDisqualification!.FinalPriceMinorUnits);
        Assert.Null(afterDisqualification.WinnerBidderId);
        Assert.Equal(AuctionOutcome.Closed, afterDisqualification.Outcome);

        // The cascade reaches the next bidder, at their own lower price.
        await Publish.AwardedAsync(_events, id, second, 1_250_000_00, step: 2);
        await Publish.SettledAsync(_events, id, second, 1_250_000_00);

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Settled);

        var settled = await AuctionAsync(id);
        Assert.Equal(1_250_000_00, settled!.FinalPriceMinorUnits);
        Assert.Equal(second, settled.WinnerBidderId);
        Assert.Equal(2, settled.CascadeStep);
    }

    [Fact]
    public async Task An_unsold_auction_has_no_price_and_no_reserve()
    {
        // The whole of what this service can say about a failure, and the cost of
        // D-45: it knows the auction did not sell, and cannot know by how much it
        // missed, because it never sees auctions.sealed.
        var id = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.ClosedAsync(_events, id, bids: 3);
        await Publish.UnsoldAsync(_events, id);

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Unsold);

        var auction = await AuctionAsync(id);
        Assert.Equal(AuctionOutcome.Unsold, auction!.Outcome);
        Assert.Null(auction.FinalPriceMinorUnits);
        Assert.Equal(3, auction.BidCount);
        Assert.NotNull(auction.UnsoldAt);
    }

    [Fact]
    public async Task An_auction_rejected_before_publication_still_gets_a_row()
    {
        // Rejection is what stops an auction reaching auctions.upcoming, so this is
        // the one lifecycle event for an auction this service has no definition of.
        // "We prepared eleven auctions and rejected two" is a report.
        var id = Guid.NewGuid();
        await Publish.RejectedAsync(_events, id, "القطعة مرهونة");

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Rejected);

        var auction = await AuctionAsync(id);
        Assert.Equal(AuctionOutcome.Rejected, auction!.Outcome);
        Assert.Equal("القطعة مرهونة", auction.RejectionReason);
        Assert.Contains("مرفوض", auction.NameAr);
    }

    [Fact]
    public async Task A_masked_auction_names_nobody()
    {
        // D-22 is enforced upstream: the participants topic carries no name at all
        // for a masked auction, so a staff report cannot name one either. That is
        // the setting working, not a missing column.
        var masked = Guid.NewGuid();
        var named = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, masked, visibility: "Masked");
        await Publish.ApprovedAsync(_events, named, visibility: "Named");
        await Publish.EligibleAsync(_events, masked, bidder, nameAr: null);
        await Publish.EligibleAsync(_events, named, bidder, nameAr: "سارة الحربي");

        await DrainUntilAsync(async db => await db.Bidders.CountAsync() == 2);

        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Null((await db.Bidders.FindAsync(masked, bidder))!.DisplayNameAr);
        Assert.Equal("سارة الحربي", (await db.Bidders.FindAsync(named, bidder))!.DisplayNameAr);
    }

    [Fact]
    public async Task A_revoked_eligibility_is_recorded_without_erasing_that_it_was_granted()
    {
        var id = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.EligibleAsync(_events, id, bidder);
        await Publish.EligibleAsync(_events, id, bidder, eligible: false);

        await DrainUntilAsync(async db =>
            await db.Bidders.AnyAsync(x => x.AuctionId == id && x.EligibilityEndedAt != null));

        await using var db = await _db.Factory.CreateDbContextAsync();
        var row = await db.Bidders.FindAsync(id, bidder);

        // Both, because "was eligible and lost it" and "never qualified" are
        // different stories about the same bidder and the funnel needs to tell them
        // apart.
        Assert.NotNull(row!.EligibleAt);
        Assert.NotNull(row.EligibilityEndedAt);
    }

    [Fact]
    public async Task A_settlement_that_arrives_before_the_eligibility_it_paid_for_still_counts()
    {
        // Four topics are followed concurrently and arrive in no order between them.
        // A bidder row with only a payment on it is a true statement about somebody
        // who paid and is not yet eligible — which is exactly a line in the funnel.
        var id = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.SettlementAsync(_events, id, bidder, "Deposit", "Charged", 100_000_00);

        await DrainUntilAsync(async db => await db.Bidders.AnyAsync(x => x.BidderId == bidder));

        await using var db = await _db.Factory.CreateDbContextAsync();
        var row = await db.Bidders.FindAsync(id, bidder);
        Assert.NotNull(row!.DepositPaidAt);
        Assert.Null(row.EligibleAt);
    }

    [Fact]
    public async Task A_refused_payment_is_recorded_with_its_reason()
    {
        var id = Guid.NewGuid();
        var bidder = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.SettlementAsync(
            _events, id, bidder, "Deposit", "Refused", 100_000_00,
            failureReason: "insufficient funds");

        await DrainUntilAsync(async db =>
            await db.Bidders.AnyAsync(x => x.BidderId == bidder && x.PaymentRefusedAt != null));

        await using var db = await _db.Factory.CreateDbContextAsync();
        var row = await db.Bidders.FindAsync(id, bidder);
        Assert.Equal("insufficient funds", row!.PaymentRefusedReason);
        Assert.Null(row.DepositPaidAt);
        Assert.Equal(0, row.DepositHeldMinorUnits);
    }

    [Fact]
    public async Task The_whole_model_rebuilds_from_the_topics_after_the_database_is_emptied()
    {
        // The property that makes this service different from every other stateful
        // one here: there is nothing in its schema that is not derivable, so losing
        // the database costs a restart and not a record.
        var id = Guid.NewGuid();
        var winner = Guid.NewGuid();

        await Publish.ApprovedAsync(_events, id);
        await Publish.ClosedAsync(_events, id, bids: 4);
        await Publish.AwardedAsync(_events, id, winner, 1_400_000_00);
        await Publish.SettledAsync(_events, id, winner, 1_400_000_00);
        await Publish.SettlementAsync(_events, id, winner, "Brokerage", "Charged", 35_000_00);

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Settled);

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE auction, plot, auction_bidder, settlement");
            Assert.Equal(0, await db.Auctions.CountAsync());
        }

        await DrainUntilOutcomeAsync(id, AuctionOutcome.Settled);

        var rebuilt = await AuctionAsync(id);
        Assert.Equal(AuctionOutcome.Settled, rebuilt!.Outcome);
        Assert.Equal(1_400_000_00, rebuilt.FinalPriceMinorUnits);
        Assert.Equal(4, rebuilt.BidCount);

        await using var after = await _db.Factory.CreateDbContextAsync();
        Assert.Equal(2, await after.Plots.CountAsync());
        Assert.Equal(1, await after.Settlements.CountAsync());
    }
}
