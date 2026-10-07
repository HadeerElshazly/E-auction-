using EAuction.Reporting.Domain;
using EAuction.Reporting.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Reporting.Reports;

/// <summary>What every report can be narrowed by.</summary>
public sealed record ReportFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Phase { get; init; }
    public string? Channel { get; init; }
    public AuctionOutcome? Outcome { get; init; }

    /// <summary>Bounded here rather than trusted: a report is a table scan with a join.</summary>
    public int Take { get; init; } = 500;
    public int Skip { get; init; }
}

// --- the rows --------------------------------------------------------------

/// <summary>
/// One auction, start to finish. The report everything else is a cut of.
/// </summary>
public sealed record AuctionOutcomeRow(
    Guid AuctionId, string NameAr, string NameEn, string? Phase, string Channel,
    string Outcome,
    DateTimeOffset ScheduledStartsAt, DateTimeOffset? StartedAt, DateTimeOffset? ClosedAt,
    int ExtensionsUsed, int BidCount,
    int PlotCount, decimal TotalAreaSqm,
    long OpeningPriceMinorUnits, long? FinalPriceMinorUnits, decimal PricePerSqmMinorUnits,
    decimal BrokerageFeePercent, long? BrokerageDueMinorUnits,
    int CascadeStep, Guid? WinnerBidderId, string? WinnerNameAr,
    int EligibleBidders,
    DateTimeOffset? AwardedAt, DateTimeOffset? SettledAt, DateTimeOffset? UnsoldAt,
    string? RejectionReason);

/// <summary>
/// Money, split into what was contracted and what was actually collected.
///
/// The split is the point, and it is a fact about this platform rather than an
/// accounting preference: the land price does not pass through here. The payment
/// service takes three things and only three — the booklet fee, the deposit, and
/// brokerage (§30) — so <see cref="SaleValueMinorUnits"/> is the value of land
/// sold, which the buyer pays the municipality by other means, while the cash
/// columns are money this platform moved. A report that added them together would
/// claim the platform collected the price of the land.
/// </summary>
public sealed record RevenueRow(
    string Group,
    int AuctionsSettled, int AuctionsUnsold,
    long SaleValueMinorUnits,
    long BrokerageChargedMinorUnits,
    long BookletFeesChargedMinorUnits,
    long DepositsForfeitedMinorUnits,
    long DepositsRefundedMinorUnits,

    /// <summary>
    /// Still held by the municipality. Zero when grouping by month, because the
    /// figure has no date — see the note where it is accumulated, and
    /// <c>GET /reports/deposits</c> for the point-in-time report.
    /// </summary>
    long DepositsHeldMinorUnits)
{
    /// <summary>Cash this platform collected and keeps. Deliberately excludes the sale value.</summary>
    public long CollectedMinorUnits =>
        BrokerageChargedMinorUnits + BookletFeesChargedMinorUnits + DepositsForfeitedMinorUnits;
}

/// <summary>
/// The funnel, per auction.
///
/// <see cref="BidCount"/> is the processor's total for the auction, not a count of
/// these bidders' bids: bids are binary frames on a per-auction topic nothing here
/// consumes (D-12), so there is no "which bidders bid" column and this report does
/// not pretend otherwise.
/// </summary>
public sealed record ParticipationRow(
    Guid AuctionId, string NameAr, string? Phase, string Outcome,
    int BookletPaid, int DepositPaid, int PaymentRefused,
    int Eligible, int EligibilityEnded, int BidCount, int Disqualified,
    Guid? WinnerBidderId);

/// <summary>
/// One plot of land, and what became of it.
///
/// <see cref="PricePerSqmMinorUnits"/> is the <em>package's</em> rate, not this
/// plot's. An auction sells 1..N plots as one indivisible package keyed on
/// auctionId (D-02), so a per-plot price does not exist — nobody bid on this plot.
/// Dividing the package price by the package area is the only honest figure, and it
/// is the same number on every plot in the package.
/// </summary>
public sealed record PlotInventoryRow(
    /// <summary>
    /// The plot's own id.
    ///
    /// Here because a report row a client cannot identify is a row the client has
    /// to invent a key for — and the obvious invention, the deed number plus the
    /// auction's name, is not unique: plot numbers repeat across the auctions of a
    /// phase and two auctions can share a name. The admin portal keyed its table
    /// that way and React found the collision in a browser.
    /// </summary>
    Guid PlotId,
    string PlotNumber, decimal AreaSqm,
    decimal? StreetWidthMeters, decimal? FrontageMeters, string? Phase,
    Guid AuctionId, string AuctionNameAr, string Outcome,
    bool Sold, long? PackagePriceMinorUnits, decimal PackageAreaSqm,
    decimal PricePerSqmMinorUnits, string? Latitude, string? Longitude);

/// <summary>
/// التأمينات المحتجزة — whose money the municipality is holding, and how much.
///
/// The one report that is about now rather than about a period, which is why it
/// takes no dates. A deposit is held from the moment it settles until the award is
/// final (§8.3), so this is the platform's liability at this instant.
/// </summary>
public sealed record DepositExposureRow(
    Guid AuctionId, string NameAr, string? Phase, string Outcome,
    int BiddersHolding, long HeldMinorUnits);

/// <summary>One disqualification, and the cascade it caused.</summary>
public sealed record DisqualificationRow(
    Guid AuctionId, string NameAr, string? Phase,
    Guid BidderId, string? BidderNameAr,
    DateTimeOffset? DisqualifiedAt, string? Reason, bool DepositForfeited,
    int AuctionCascadeStep, string Outcome, long? FinalPriceMinorUnits);

// --- the queries -----------------------------------------------------------

/// <summary>
/// Every report, in one place, built as EF queries over the read model.
///
/// Returned as records rather than written straight to a response, because each one
/// is rendered twice — as JSON for a portal and as CSV for a spreadsheet — and two
/// implementations of one report is how the two come to disagree.
/// </summary>
public static class ReportQueries
{
    private static IQueryable<AuctionRecord> Scope(ReportingDbContext db, ReportFilter f)
    {
        var q = db.Auctions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(f.Phase)) q = q.Where(a => a.Phase == f.Phase);
        if (!string.IsNullOrWhiteSpace(f.Channel)) q = q.Where(a => a.Channel == f.Channel);
        if (f.Outcome is not null) q = q.Where(a => a.Outcome == f.Outcome);

        // Dated by when bidding ended; then by the rejection, for an auction that
        // never opened; then by its schedule, for one that has not opened yet.
        //
        // The middle step is not decoration. A report of "last quarter" that
        // silently dropped the auctions prepared and rejected in it answers the
        // wrong question — and a rejected auction has no closing date at all,
        // because bidding never started.
        if (f.From is not null)
            q = q.Where(a => (a.ClosedAt ?? a.RejectedAt ?? a.ScheduledStartsAt) >= f.From);
        if (f.To is not null)
            q = q.Where(a => (a.ClosedAt ?? a.RejectedAt ?? a.ScheduledStartsAt) <= f.To);

        return q;
    }

    private static int Page(ReportFilter f) => Math.Clamp(f.Take, 1, 5000);

    /// <summary>
    /// A page of the outcome report.
    ///
    /// Pages the ids and then hands them to <see cref="ForIdsAsync"/>, so the
    /// projection — every column, and the brokerage arithmetic — exists once. The
    /// first version had it twice, once here and once for the single-auction
    /// endpoint, which is two places for a new column to be forgotten.
    /// </summary>
    public static async Task<List<AuctionOutcomeRow>> AuctionsAsync(
        ReportingDbContext db, ReportFilter f, CancellationToken ct)
    {
        var ids = await Scope(db, f)
            .OrderByDescending(a => a.ClosedAt ?? a.RejectedAt ?? a.ScheduledStartsAt)
            .ThenBy(a => a.AuctionId)
            .Skip(Math.Max(0, f.Skip)).Take(Page(f))
            .Select(a => a.AuctionId)
            .ToListAsync(ct);

        var rows = await ForIdsAsync(db, ids, ct);

        // The page's order, which the second query does not preserve.
        return ids
            .Select(id => rows.First(r => r.AuctionId == id))
            .ToList();
    }

    public static async Task<AuctionOutcomeRow?> AuctionAsync(
        ReportingDbContext db, Guid auctionId, CancellationToken ct) =>
        (await ForIdsAsync(db, [auctionId], ct)).FirstOrDefault();

    /// <summary>The outcome report for a named set of auctions. The one projection.</summary>
    public static async Task<List<AuctionOutcomeRow>> ForIdsAsync(
        ReportingDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var auctions = await db.Auctions.AsNoTracking()
            .Where(a => ids.Contains(a.AuctionId))
            .ToListAsync(ct);

        // Two more queries rather than a correlated subquery per row: the set is
        // bounded and this is one round trip for the whole of it.
        var eligible = await db.Bidders.AsNoTracking()
            .Where(x => ids.Contains(x.AuctionId) && x.EligibleAt != null)
            .GroupBy(x => x.AuctionId)
            .Select(g => new { AuctionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AuctionId, x => x.Count, ct);

        var winners = await db.Bidders.AsNoTracking()
            .Where(x => ids.Contains(x.AuctionId) && x.Won)
            .Select(x => new { x.AuctionId, x.DisplayNameAr })
            .ToListAsync(ct);

        return auctions.Select(a => new AuctionOutcomeRow(
            a.AuctionId, a.NameAr, a.NameEn, a.Phase, a.Channel, a.Outcome.ToString(),
            a.ScheduledStartsAt, a.StartedAt, a.ClosedAt,
            a.ExtensionsUsed, a.BidCount, a.PlotCount, a.TotalAreaSqm,
            a.OpeningPriceMinorUnits, a.FinalPriceMinorUnits, a.PricePerSqmMinorUnits,
            a.BrokerageFeePercent,

            // مبلغ السعي — what the winner owes on top. Computed from the two
            // figures on the record rather than read from the settlement, so it is
            // right for an award that has not been charged yet, which is most of
            // them.
            a.FinalPriceMinorUnits is null
                ? null
                : (long)Math.Round(a.FinalPriceMinorUnits.Value * a.BrokerageFeePercent / 100m),

            a.CascadeStep, a.WinnerBidderId,

            // Null on a masked auction, because the topic never carried the name
            // (D-22). A staff report does not override that setting.
            winners.FirstOrDefault(w => w.AuctionId == a.AuctionId)?.DisplayNameAr,

            eligible.GetValueOrDefault(a.AuctionId),
            a.AwardedAt, a.SettledAt, a.UnsoldAt, a.RejectionReason)).ToList();
    }

    public static async Task<List<ParticipationRow>> ParticipationAsync(
        ReportingDbContext db, ReportFilter f, CancellationToken ct)
    {
        var auctions = await Scope(db, f)
            .OrderByDescending(a => a.ClosedAt ?? a.RejectedAt ?? a.ScheduledStartsAt)
            .ThenBy(a => a.AuctionId)
            .Skip(Math.Max(0, f.Skip)).Take(Page(f))
            .Select(a => new
            {
                a.AuctionId, a.NameAr, a.Phase, a.Outcome, a.BidCount, a.WinnerBidderId
            })
            .ToListAsync(ct);

        var ids = auctions.Select(a => a.AuctionId).ToList();

        var funnel = await db.Bidders.AsNoTracking()
            .Where(x => ids.Contains(x.AuctionId))
            .GroupBy(x => x.AuctionId)
            .Select(g => new
            {
                AuctionId = g.Key,
                BookletPaid = g.Count(x => x.BookletPaidAt != null),
                DepositPaid = g.Count(x => x.DepositPaidAt != null),
                Refused = g.Count(x => x.PaymentRefusedAt != null),
                Eligible = g.Count(x => x.EligibleAt != null),
                Ended = g.Count(x => x.EligibilityEndedAt != null),
                Disqualified = g.Count(x => x.DisqualifiedAt != null),
            })
            .ToDictionaryAsync(x => x.AuctionId, ct);

        return auctions.Select(a =>
        {
            var c = funnel.GetValueOrDefault(a.AuctionId);
            return new ParticipationRow(
                a.AuctionId, a.NameAr, a.Phase, a.Outcome.ToString(),
                c?.BookletPaid ?? 0, c?.DepositPaid ?? 0, c?.Refused ?? 0,
                c?.Eligible ?? 0, c?.Ended ?? 0, a.BidCount, c?.Disqualified ?? 0,
                a.WinnerBidderId);
        }).ToList();
    }

    public static async Task<List<PlotInventoryRow>> PlotsAsync(
        ReportingDbContext db, ReportFilter f, bool? sold, CancellationToken ct)
    {
        var query = from plot in db.Plots.AsNoTracking()
                    join auction in Scope(db, f) on plot.AuctionId equals auction.AuctionId
                    select new { plot, auction };

        if (sold == true) query = query.Where(x => x.auction.Outcome == AuctionOutcome.Settled);
        if (sold == false) query = query.Where(x => x.auction.Outcome != AuctionOutcome.Settled);

        var rows = await query
            .OrderBy(x => x.auction.Phase).ThenBy(x => x.plot.PlotNumber)
            .Skip(Math.Max(0, f.Skip)).Take(Page(f))
            .ToListAsync(ct);

        return rows.Select(x => new PlotInventoryRow(
            x.plot.PlotId,
            x.plot.PlotNumber, x.plot.AreaSqm,
            x.plot.StreetWidthMeters, x.plot.FrontageMeters, x.auction.Phase,
            x.auction.AuctionId, x.auction.NameAr, x.auction.Outcome.ToString(),
            x.auction.Outcome == AuctionOutcome.Settled,
            x.auction.FinalPriceMinorUnits, x.auction.TotalAreaSqm,
            x.auction.PricePerSqmMinorUnits,
            x.plot.Latitude, x.plot.Longitude)).ToList();
    }

    public static async Task<List<DepositExposureRow>> DepositsAsync(
        ReportingDbContext db, ReportFilter f, CancellationToken ct)
    {
        // Driven from the holdings rather than from the auctions, because the
        // question is "whose money is here", and an auction with nothing held is not
        // an answer to it.
        var held = await db.Bidders.AsNoTracking()
            .Where(x => x.DepositHeldMinorUnits > 0)
            .GroupBy(x => x.AuctionId)
            .Select(g => new
            {
                AuctionId = g.Key,
                Bidders = g.Count(),
                Held = g.Sum(x => x.DepositHeldMinorUnits),
            })
            .ToListAsync(ct);

        var ids = held.Select(h => h.AuctionId).ToList();

        var auctions = await Scope(db, f)
            .Where(a => ids.Contains(a.AuctionId))
            .Select(a => new { a.AuctionId, a.NameAr, a.Phase, a.Outcome })
            .ToDictionaryAsync(a => a.AuctionId, ct);

        return held
            .Where(h => auctions.ContainsKey(h.AuctionId))
            .OrderByDescending(h => h.Held)
            .Select(h => new DepositExposureRow(
                h.AuctionId, auctions[h.AuctionId].NameAr, auctions[h.AuctionId].Phase,
                auctions[h.AuctionId].Outcome.ToString(), h.Bidders, h.Held))
            .ToList();
    }

    public static async Task<List<DisqualificationRow>> DisqualificationsAsync(
        ReportingDbContext db, ReportFilter f, CancellationToken ct)
    {
        var rows = await (
            from bidder in db.Bidders.AsNoTracking()
            join auction in Scope(db, f) on bidder.AuctionId equals auction.AuctionId
            where bidder.DisqualifiedAt != null
            orderby bidder.DisqualifiedAt descending
            select new { bidder, auction })
            .Skip(Math.Max(0, f.Skip)).Take(Page(f))
            .ToListAsync(ct);

        return rows.Select(x => new DisqualificationRow(
            x.auction.AuctionId, x.auction.NameAr, x.auction.Phase,
            x.bidder.BidderId, x.bidder.DisplayNameAr,
            x.bidder.DisqualifiedAt, x.bidder.DisqualificationReason, x.bidder.DepositForfeited,
            x.auction.CascadeStep, x.auction.Outcome.ToString(),
            x.auction.FinalPriceMinorUnits)).ToList();
    }

    /// <summary>How a revenue report's rows are grouped.</summary>
    public enum RevenueGrouping { Month, Phase, Channel }

    public static async Task<List<RevenueRow>> RevenueAsync(
        ReportingDbContext db, ReportFilter f, RevenueGrouping by, CancellationToken ct)
    {
        // The sale side: contracted value of land, from the auctions themselves.
        var sales = await Scope(db, f)
            .Select(a => new
            {
                a.AuctionId, a.Phase, a.Channel, a.Outcome,
                a.FinalPriceMinorUnits, a.SettledAt,
            })
            .ToListAsync(ct);

        var ids = sales.Select(s => s.AuctionId).ToList();

        // The cash side: money this platform actually moved. Joined to the auctions
        // in scope so a phase filter means the same thing on both halves.
        var cash = await db.Settlements.AsNoTracking()
            .Where(s => ids.Contains(s.AuctionId))
            .Select(s => new { s.AuctionId, s.Purpose, s.Outcome, s.AmountMinorUnits, s.At })
            .ToListAsync(ct);

        var holdings = await db.Bidders.AsNoTracking()
            .Where(x => ids.Contains(x.AuctionId) && x.DepositHeldMinorUnits > 0)
            .Select(x => new { x.AuctionId, x.DepositHeldMinorUnits })
            .ToListAsync(ct);

        // By id, because the cash loop below asks for an auction's group once per
        // settlement. A First() over the list made this quadratic — invisible with
        // three auctions and unpleasant over a phase of several hundred.
        var byId = sales.ToDictionary(s => s.AuctionId);

        string KeyOf(Guid auctionId, DateTimeOffset? at)
        {
            var a = byId[auctionId];
            return by switch
            {
                RevenueGrouping.Phase => a.Phase ?? "(بدون مخطط)",
                RevenueGrouping.Channel => a.Channel,

                // By the date the thing happened, which differs per line: a sale is
                // dated by its settlement and a charge by when it was taken. A sale
                // settled on the 31st whose brokerage is charged on the 1st appears
                // in two months, because it happened in two months.
                _ => at is null ? "(غير مؤرّخ)" : at.Value.ToUniversalTime().ToString("yyyy-MM"),
            };
        }

        var groups = new SortedDictionary<string, RevenueAccumulator>(StringComparer.Ordinal);

        RevenueAccumulator At(string key)
        {
            if (!groups.TryGetValue(key, out var found))
                groups[key] = found = new RevenueAccumulator();
            return found;
        }

        foreach (var sale in sales)
        {
            // Dated by its settlement when grouping by month, which is when the
            // municipality sold the land. An auction still open has no such date and
            // lands in "(غير مؤرّخ)" — it has no sale value to report either.
            var bucket = At(KeyOf(sale.AuctionId, sale.SettledAt));

            if (sale.Outcome == AuctionOutcome.Settled)
            {
                bucket.AuctionsSettled++;
                bucket.SaleValue += sale.FinalPriceMinorUnits ?? 0;
            }
            else if (sale.Outcome == AuctionOutcome.Unsold)
            {
                bucket.AuctionsUnsold++;
            }
        }

        foreach (var row in cash)
        {
            var bucket = At(KeyOf(row.AuctionId, row.At));

            switch (row.Purpose, row.Outcome)
            {
                case (PaymentPurposes.Brokerage, PaymentOutcomes.Charged):
                    bucket.Brokerage += row.AmountMinorUnits; break;
                case (PaymentPurposes.Booklet, PaymentOutcomes.Charged):
                    bucket.Booklet += row.AmountMinorUnits; break;
                case (PaymentPurposes.Deposit, PaymentOutcomes.Forfeited):
                    bucket.Forfeited += row.AmountMinorUnits; break;
                case (PaymentPurposes.Deposit, PaymentOutcomes.Refunded):
                    bucket.Refunded += row.AmountMinorUnits; break;
            }
        }

        // Deposits held are reported for a phase or a channel and not for a month,
        // because the figure has no date: it is what is held *now*, and dropping it
        // into a month bucket would invite somebody to read "March: 2,000,000 held"
        // as money taken in March. GET /reports/deposits is the point-in-time
        // report, and it is the only place this figure is exact.
        if (by != RevenueGrouping.Month)
            foreach (var holding in holdings)
                At(KeyOf(holding.AuctionId, null)).Held += holding.DepositHeldMinorUnits;

        return groups
            .Select(g => new RevenueRow(
                g.Key, g.Value.AuctionsSettled, g.Value.AuctionsUnsold,
                g.Value.SaleValue, g.Value.Brokerage, g.Value.Booklet,
                g.Value.Forfeited, g.Value.Refunded, g.Value.Held))
            .ToList();
    }

    private sealed class RevenueAccumulator
    {
        public int AuctionsSettled;
        public int AuctionsUnsold;
        public long SaleValue;
        public long Brokerage;
        public long Booklet;
        public long Forfeited;
        public long Refunded;
        public long Held;
    }
}
