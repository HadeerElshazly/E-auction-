using EAuction.Core;
using EAuction.Reporting.Domain;
using EAuction.Reporting.Integration;
using EAuction.Reporting.Persistence;
using EAuction.Reporting.Reports;
using EAuction.Security;
using Microsoft.EntityFrameworkCore;

// ---------------------------------------------------------------------------
// التقارير — the reporting service (§35).
//
// A read model assembled from four control topics, and the reports a municipality
// running a land-sale programme actually asks for: what each auction did, what a
// phase raised, how many registrations turned into bids, how much of the plan is
// still unsold, whose deposits are being held, and who defaulted.
//
// Two things it deliberately does not do. It does not query any other service's
// database — the read model is built from events, so a report cannot be slowed
// down or locked out by the auction service's own load. And it does not consume
// auctions.sealed: the reserve price is the one secret in this platform, and a
// service whose output is spreadsheets is exactly where a secret stops being one
// (D-45).
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("Reporting")
    ?? "Host=localhost;Database=eauction_reporting;Username=eauction;Password=eauction";

builder.Services.AddDbContextFactory<ReportingDbContext>(o => o.UseNpgsql(connectionString));

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

builder.Services.AddSingleton<IEventStream>(
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "reporting"
        }));

builder.Services.AddSingleton<ReportingConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportingConsumer>());

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);
builder.Services.AddEAuctionCors(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseEAuctionCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok("ok")).AllowAnonymous();
app.MapGet("/health/ready", async (
    IDbContextFactory<ReportingDbContext> f, ReportingConsumer consumer, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    return consumer.Ready && await db.Database.CanConnectAsync(ct)
        ? Results.Ok("ok")
        : Results.StatusCode(503);
}).AllowAnonymous();

// --- the reports -----------------------------------------------------------
//
// Every one of them is the same shape: a filter off the query string, one call into
// ReportQueries, and a render as JSON or CSV. The `format=csv` branch is the reason
// the queries return records rather than writing responses — two renderings of one
// report, and one implementation behind them.

app.MapGet("/reports/auctions", async (
    HttpContext http, IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var rows = await ReportQueries.AuctionsAsync(db, Filter(http), ct);

    return Render(http, "auctions", rows,
    [
        "auction_id", "name_ar", "name_en", "phase", "channel", "outcome",
        "scheduled_starts_at", "started_at", "closed_at",
        "extensions_used", "bid_count", "plot_count", "total_area_sqm",
        "opening_price", "final_price", "price_per_sqm",
        "brokerage_percent", "brokerage_due",
        "cascade_step", "winner_bidder_id", "winner_name_ar", "eligible_bidders",
        "awarded_at", "settled_at", "unsold_at", "rejection_reason",
    ],
    r =>
    [
        r.AuctionId, r.NameAr, r.NameEn, r.Phase, r.Channel, r.Outcome,
        r.ScheduledStartsAt, r.StartedAt, r.ClosedAt,
        r.ExtensionsUsed, r.BidCount, r.PlotCount, r.TotalAreaSqm,
        r.OpeningPriceMinorUnits, r.FinalPriceMinorUnits, r.PricePerSqmMinorUnits,
        r.BrokerageFeePercent, r.BrokerageDueMinorUnits,
        r.CascadeStep, r.WinnerBidderId, r.WinnerNameAr, r.EligibleBidders,
        r.AwardedAt, r.SettledAt, r.UnsoldAt, r.RejectionReason,
    ]);
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/reports/auctions/{id:guid}", async (
    Guid id, IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var row = await ReportQueries.AuctionAsync(db, id, ct);
    if (row is null) return Results.NotFound();

    // The plots too, because the single-auction view is the one a committee member
    // opens to see what was actually in the package.
    var plots = await db.Plots.AsNoTracking()
        .Where(p => p.AuctionId == id)
        .OrderBy(p => p.DeedNumber)
        .Select(p => new { p.DeedNumber, p.AreaSqm, p.Latitude, p.Longitude, p.DescriptionAr })
        .ToListAsync(ct);

    return Results.Ok(new { auction = row, plots });
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/reports/revenue", async (
    HttpContext http, string? groupBy,
    IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    var grouping = groupBy?.ToLowerInvariant() switch
    {
        "phase" => ReportQueries.RevenueGrouping.Phase,
        "channel" => ReportQueries.RevenueGrouping.Channel,
        null or "" or "month" => ReportQueries.RevenueGrouping.Month,
        _ => (ReportQueries.RevenueGrouping?)null,
    };

    if (grouping is null)
        return Results.BadRequest(new
        {
            problems = new[] { $"Unknown groupBy '{groupBy}'." },
            allowed = new[] { "month", "phase", "channel" },
        });

    await using var db = await f.CreateDbContextAsync(ct);
    var rows = await ReportQueries.RevenueAsync(db, Filter(http), grouping.Value, ct);

    return Render(http, $"revenue-by-{grouping.Value.ToString().ToLowerInvariant()}", rows,
    [
        "group", "auctions_settled", "auctions_unsold",
        "sale_value", "brokerage_charged", "booklet_fees_charged",
        "deposits_forfeited", "deposits_refunded", "deposits_held", "collected",
    ],
    r =>
    [
        r.Group, r.AuctionsSettled, r.AuctionsUnsold,
        r.SaleValueMinorUnits, r.BrokerageChargedMinorUnits, r.BookletFeesChargedMinorUnits,
        r.DepositsForfeitedMinorUnits, r.DepositsRefundedMinorUnits,
        r.DepositsHeldMinorUnits, r.CollectedMinorUnits,
    ]);
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/reports/participation", async (
    HttpContext http, IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var rows = await ReportQueries.ParticipationAsync(db, Filter(http), ct);

    return Render(http, "participation", rows,
    [
        "auction_id", "name_ar", "phase", "outcome",
        "booklet_paid", "deposit_paid", "payment_refused",
        "eligible", "eligibility_ended", "bid_count", "disqualified", "winner_bidder_id",
    ],
    r =>
    [
        r.AuctionId, r.NameAr, r.Phase, r.Outcome,
        r.BookletPaid, r.DepositPaid, r.PaymentRefused,
        r.Eligible, r.EligibilityEnded, r.BidCount, r.Disqualified, r.WinnerBidderId,
    ]);
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/reports/plots", async (
    HttpContext http, bool? sold,
    IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var rows = await ReportQueries.PlotsAsync(db, Filter(http), sold, ct);

    return Render(http, "plots", rows,
    [
        "deed_number", "area_sqm", "phase", "auction_id", "auction_name_ar",
        "outcome", "sold", "package_price", "package_area_sqm", "price_per_sqm",
        "latitude", "longitude",
    ],
    r =>
    [
        r.DeedNumber, r.AreaSqm, r.Phase, r.AuctionId, r.AuctionNameAr,
        r.Outcome, r.Sold, r.PackagePriceMinorUnits, r.PackageAreaSqm, r.PricePerSqmMinorUnits,
        r.Latitude, r.Longitude,
    ]);
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/reports/deposits", async (
    HttpContext http, IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var rows = await ReportQueries.DepositsAsync(db, Filter(http), ct);

    return Render(http, "deposits-held", rows,
        ["auction_id", "name_ar", "phase", "outcome", "bidders_holding", "held"],
        r => [r.AuctionId, r.NameAr, r.Phase, r.Outcome, r.BiddersHolding, r.HeldMinorUnits]);
}).RequireAuthorization(Policies.Reporting);

app.MapGet("/reports/disqualifications", async (
    HttpContext http, IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var rows = await ReportQueries.DisqualificationsAsync(db, Filter(http), ct);

    return Render(http, "disqualifications", rows,
    [
        "auction_id", "name_ar", "phase", "bidder_id", "bidder_name_ar",
        "disqualified_at", "reason", "deposit_forfeited",
        "auction_cascade_step", "outcome", "final_price",
    ],
    r =>
    [
        r.AuctionId, r.NameAr, r.Phase, r.BidderId, r.BidderNameAr,
        r.DisqualifiedAt, r.Reason, r.DepositForfeited,
        r.AuctionCascadeStep, r.Outcome, r.FinalPriceMinorUnits,
    ]);
}).RequireAuthorization(Policies.Reporting);

// The phases, so a portal can offer the filter without knowing the programme.
app.MapGet("/reports/phases", async (
    IDbContextFactory<ReportingDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);

    var rows = await db.Auctions.AsNoTracking()
        .GroupBy(a => a.Phase)
        .Select(g => new
        {
            phase = g.Key,
            auctions = g.Count(),
            plots = g.Sum(a => a.PlotCount),
            areaSqm = g.Sum(a => a.TotalAreaSqm),
            settled = g.Count(a => a.Outcome == AuctionOutcome.Settled),
        })
        .OrderBy(x => x.phase)
        .ToListAsync(ct);

    return Results.Ok(new { items = rows });
}).RequireAuthorization(Policies.Reporting);

app.Run();

// ---------------------------------------------------------------------------

/// <summary>
/// The filter, off the query string.
///
/// Read here rather than bound as a parameter object because minimal APIs would
/// treat a record with several properties as a body, and these are all GETs.
/// </summary>
static ReportFilter Filter(HttpContext http)
{
    var q = http.Request.Query;

    return new ReportFilter
    {
        From = Date(q["from"]),
        To = Date(q["to"]),
        Phase = Text(q["phase"]),
        Channel = Text(q["channel"]),
        Outcome = Enum.TryParse<AuctionOutcome>(q["outcome"], ignoreCase: true, out var outcome)
                  && Enum.IsDefined(outcome)
            ? outcome
            : null,
        Take = int.TryParse(q["take"], out var take) ? take : 500,
        Skip = int.TryParse(q["skip"], out var skip) ? skip : 0,
    };

    static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
            | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// JSON, or a CSV with a filename somebody can find again.
///
/// One function for every report, so a report cannot be available as JSON and not
/// as a spreadsheet — which is the format the people who ask for التقارير actually
/// want.
/// </summary>
static IResult Render<T>(
    HttpContext http, string name, List<T> rows,
    string[] headers, Func<T, object?[]> cells)
{
    if (!string.Equals(http.Request.Query["format"], "csv", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new { count = rows.Count, items = rows });

    // Comma unless asked otherwise. See the note on Csv: a Windows machine set to
    // Arabic (Saudi Arabia) splits on a semicolon, and opens a comma-separated file
    // with every row in one column.
    var separator =
        string.Equals(http.Request.Query["separator"], "semicolon", StringComparison.OrdinalIgnoreCase)
            ? ';'
            : ',';

    var bytes = Csv.Write(headers, rows.Select(cells), separator);

    // Dated, because the second thing anybody asks of a downloaded report is which
    // day it was run on.
    var fileName = $"eauction-{name}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv";

    // Never sniffed. The same reasoning as the document service: this file carries
    // staff-entered text, and a browser that decided a .csv was really HTML would
    // run it on this origin.
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";

    return Results.File(bytes, Csv.ContentType, fileName);
}
