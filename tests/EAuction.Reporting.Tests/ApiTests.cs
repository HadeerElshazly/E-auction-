using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAuction.Core;
using EAuction.Reporting.Domain;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Reporting.Tests;

/// <summary>
/// The reports, against the service as it boots: its consumer running, its own
/// database, and a token the way Keycloak issues one.
///
/// The arithmetic is the point of most of these. A report that is merely reachable
/// is worth nothing; one that adds a disqualified award into revenue, or counts a
/// forfeited deposit as refunded, is worse than nothing because somebody acts on it.
/// </summary>
[Collection("reporting")]
public class ApiTests : IAsyncLifetime
{
    private ReportingDatabase _db = null!;
    private AuthenticatedFactory<Program> _factory = null!;

    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly Guid Khalid = Guid.NewGuid();

    private Guid _sold, _unsold, _cascaded;

    public async Task InitializeAsync()
    {
        _db = await ReportingDatabase.CreateAsync();

        _factory = new AuthenticatedFactory<Program>
        {
            Settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Reporting"] = _db.ConnectionString,
            }
        };

        _ = _factory.CreateClient();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _db.DisposeAsync();
    }

    private IEventStream Events() => _factory.Services.GetRequiredService<IEventStream>();

    private HttpClient Reader() => _factory.CreateClient().As(Guid.NewGuid(), Roles.Reporting);

    /// <summary>
    /// Three auctions covering the three endings a report has to tell apart: one
    /// sold, one that nobody cleared the reserve on, and one where the winner
    /// defaulted and the cascade took it at a lower price.
    ///
    /// Every event is given an explicit date on one timeline, which the first
    /// version of this seed did not — the booklet fees defaulted to "two hours ago"
    /// while the sales were dated in March, so the monthly revenue report correctly
    /// split them across two months and the assertion looked like a bug in the
    /// report. A money report groups by when each thing happened, so a test of one
    /// needs a story that actually happened in an order.
    /// </summary>
    private async Task SeedAsync()
    {
        var events = Events();

        _sold = Guid.NewGuid();
        _unsold = Guid.NewGuid();
        _cascaded = Guid.NewGuid();

        var registered = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var opened = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        var settled = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);

        // Sold outright, at 1,200,000 SAR with 2.5% brokerage.
        await Publish.ApprovedAsync(
            events, _sold, nameAr: "السعيد ١", visibility: "Named", startsAt: opened);
        await Publish.EligibleAsync(events, _sold, Sara, nameAr: "سارة الحربي");
        await Publish.EligibleAsync(events, _sold, Khalid, nameAr: "خالد العتيبي");
        await Publish.SettlementAsync(
            events, _sold, Sara, "Booklet", "Charged", 1_000_00, at: registered);
        await Publish.SettlementAsync(
            events, _sold, Khalid, "Booklet", "Charged", 1_000_00, at: registered);
        await Publish.SettlementAsync(
            events, _sold, Sara, "Deposit", "Charged", 100_000_00, at: registered);
        await Publish.SettlementAsync(
            events, _sold, Khalid, "Deposit", "Charged", 100_000_00, at: registered);
        await Publish.StartedAsync(events, _sold, at: opened);
        await Publish.ClosedAsync(events, _sold, bids: 6, at: opened.AddHours(1));
        await Publish.AwardedAsync(
            events, _sold, Sara, 1_200_000_00, confirmedAt: opened.AddDays(1));
        await Publish.SettledAsync(events, _sold, Sara, 1_200_000_00, at: settled);
        await Publish.SettlementAsync(
            events, _sold, Sara, "Brokerage", "Charged", 30_000_00, at: settled);
        await Publish.SettlementAsync(
            events, _sold, Khalid, "Deposit", "Refunded", 100_000_00, at: settled);
        await Publish.SettlementAsync(
            events, _sold, Sara, "Deposit", "AppliedToPurchase", 100_000_00, at: settled);

        // Nobody above the reserve. Its bidder's deposit is never resolved, because
        // the award never became final — which is why this is the auction that still
        // shows in the exposure report.
        await Publish.ApprovedAsync(
            events, _unsold, nameAr: "السعيد ٢",
            phase: "مخطط السعيد — المرحلة الثانية", startsAt: opened);
        await Publish.EligibleAsync(events, _unsold, Khalid);
        await Publish.SettlementAsync(
            events, _unsold, Khalid, "Deposit", "Charged", 100_000_00, at: registered);
        await Publish.ClosedAsync(events, _unsold, bids: 2, at: opened.AddHours(1));
        await Publish.UnsoldAsync(events, _unsold);

        // The winner defaulted; the cascade awarded it lower and it settled there.
        await Publish.ApprovedAsync(events, _cascaded, nameAr: "السعيد ٣", startsAt: opened);
        await Publish.EligibleAsync(events, _cascaded, Sara);
        await Publish.EligibleAsync(events, _cascaded, Khalid);
        await Publish.SettlementAsync(
            events, _cascaded, Sara, "Deposit", "Charged", 100_000_00, at: registered);
        await Publish.SettlementAsync(
            events, _cascaded, Khalid, "Deposit", "Charged", 100_000_00, at: registered);
        await Publish.ClosedAsync(events, _cascaded, bids: 9, at: opened.AddHours(1));
        await Publish.AwardedAsync(
            events, _cascaded, Sara, 1_500_000_00, confirmedAt: opened.AddDays(1));
        await Publish.DisqualifiedAsync(events, _cascaded, Sara, "لم يسدّد", forfeit: true);
        await Publish.SettlementAsync(
            events, _cascaded, Sara, "Deposit", "Forfeited", 100_000_00, at: settled);
        await Publish.AwardedAsync(
            events, _cascaded, Khalid, 1_400_000_00, step: 2, confirmedAt: settled);
        await Publish.SettledAsync(events, _cascaded, Khalid, 1_400_000_00, at: settled);

        // The cascade winner's own deposit, set against the price they now owe. The
        // first version of this seed forgot it, and the exposure report correctly
        // reported a settled auction still holding 100,000 — which is a real
        // condition worth surfacing, and was not the one being tested.
        await Publish.SettlementAsync(
            events, _cascaded, Khalid, "Deposit", "AppliedToPurchase", 100_000_00, at: settled);

        await WaitAsync();
    }

    private async Task WaitAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (true)
        {
            await using var db = await _db.Factory.CreateDbContextAsync(cts.Token);

            var ready =
                await db.Auctions.CountAsync(cts.Token) == 3
                && await db.Auctions.CountAsync(a => a.Outcome == AuctionOutcome.Settled, cts.Token) == 2
                && await db.Auctions.CountAsync(a => a.Outcome == AuctionOutcome.Unsold, cts.Token) == 1
                && await db.Settlements.CountAsync(cts.Token) == 12;

            if (ready) return;
            await Task.Delay(25, cts.Token);
        }
    }

    // --- authorization -----------------------------------------------------

    private static readonly string[] Endpoints =
    [
        "/reports/auctions", "/reports/revenue", "/reports/participation",
        "/reports/plots", "/reports/deposits", "/reports/disqualifications",
        "/reports/phases",
    ];

    [Fact]
    public async Task No_report_is_readable_without_a_token()
    {
        var client = _factory.CreateClient().Anonymous();

        foreach (var endpoint in Endpoints)
            Assert.Equal(
                HttpStatusCode.Unauthorized, (await client.GetAsync(endpoint)).StatusCode);
    }

    [Fact]
    public async Task A_bidder_and_a_clerk_cannot_read_the_reports()
    {
        // A CSV naming every winner and the price they paid is not a document a
        // bidder gets, and a clerk running a hall needs the room's roster rather
        // than the programme's revenue.
        foreach (var role in new[] { Roles.Bidder, Roles.Operator, Roles.Auditor })
        {
            var client = _factory.CreateClient().As(Guid.NewGuid(), role);

            foreach (var endpoint in Endpoints)
                Assert.Equal(
                    HttpStatusCode.Forbidden, (await client.GetAsync(endpoint)).StatusCode);
        }
    }

    [Fact]
    public async Task The_three_staff_roles_that_should_read_them_can()
    {
        // The `reporting` role exists so finance staff do not need `auction-admin`,
        // which can change a reserve price. The other two reach the reports because
        // these are management information about work they already do.
        foreach (var role in new[] { Roles.Reporting, Roles.AuctionAdmin, Roles.AwardCommittee })
        {
            var client = _factory.CreateClient().As(Guid.NewGuid(), role);

            foreach (var endpoint in Endpoints)
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(endpoint)).StatusCode);
        }
    }

    [Fact]
    public async Task There_is_no_way_to_write_through_the_reporting_api()
    {
        // A read model has no write surface. The rows come from the topics and
        // nowhere else, so a wrong figure is fixed at its source rather than
        // corrected in place — which is what keeps a report and the register in
        // agreement.
        var client = Reader();

        foreach (var attempt in new[]
        {
            client.PostAsJsonAsync("/reports/auctions", new { }),
            client.PutAsJsonAsync($"/reports/auctions/{_sold}", new { }),
            client.DeleteAsync($"/reports/auctions/{_sold}"),
        })
        {
            var response = await attempt;
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"Expected no write route, got {(int)response.StatusCode}.");
        }
    }

    // --- the outcome report ------------------------------------------------

    [Fact]
    public async Task The_outcome_report_tells_the_three_endings_apart()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/auctions");
        var items = body.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("auctionId").GetGuid());

        Assert.Equal(3, items.Count);

        Assert.Equal("Settled", items[_sold].GetProperty("outcome").GetString());
        Assert.Equal(1_200_000_00, items[_sold].GetProperty("finalPriceMinorUnits").GetInt64());

        Assert.Equal("Unsold", items[_unsold].GetProperty("outcome").GetString());
        Assert.Equal(
            JsonValueKind.Null, items[_unsold].GetProperty("finalPriceMinorUnits").ValueKind);

        // The cascade's price, not the defaulter's. The distinction the whole
        // report turns on: the municipality was paid 1,400,000, never 1,500,000.
        Assert.Equal("Settled", items[_cascaded].GetProperty("outcome").GetString());
        Assert.Equal(1_400_000_00, items[_cascaded].GetProperty("finalPriceMinorUnits").GetInt64());
        Assert.Equal(2, items[_cascaded].GetProperty("cascadeStep").GetInt32());
        Assert.Equal(Khalid, items[_cascaded].GetProperty("winnerBidderId").GetGuid());
    }

    [Fact]
    public async Task Brokerage_due_is_computed_from_the_price_that_was_actually_awarded()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/auctions");
        var items = body.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("auctionId").GetGuid());

        // 2.5% of 1,200,000 is 30,000.
        Assert.Equal(30_000_00, items[_sold].GetProperty("brokerageDueMinorUnits").GetInt64());

        // 2.5% of the cascade's 1,400,000 is 35,000 — not 37,500 on the price the
        // defaulter bid.
        Assert.Equal(35_000_00, items[_cascaded].GetProperty("brokerageDueMinorUnits").GetInt64());

        Assert.Equal(
            JsonValueKind.Null, items[_unsold].GetProperty("brokerageDueMinorUnits").ValueKind);
    }

    [Fact]
    public async Task A_named_auction_names_its_winner_and_a_masked_one_does_not()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/auctions");
        var items = body.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("auctionId").GetGuid());

        Assert.Equal("سارة الحربي", items[_sold].GetProperty("winnerNameAr").GetString());

        // D-22 holds through the report: the participants topic carried no name for
        // a masked auction, so there is none to print.
        Assert.Equal(JsonValueKind.Null, items[_cascaded].GetProperty("winnerNameAr").ValueKind);
    }

    [Fact]
    public async Task One_auction_comes_back_with_its_plots()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>($"/reports/auctions/{_sold}");

        Assert.Equal("Settled", body.GetProperty("auction").GetProperty("outcome").GetString());
        Assert.Equal(2, body.GetProperty("plots").GetArrayLength());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Reader().GetAsync($"/reports/auctions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task An_auction_rejected_before_publication_stays_inside_a_dated_report()
    {
        // The bug this closes: a rejected auction has no closing date, because
        // bidding never opened — so with its schedule left at 0001-01-01 it sorted
        // to the beginning of time and fell out of every report with a `from`
        // filter. Which defeats the one thing recording it is for.
        var rejected = Guid.NewGuid();
        await Publish.RejectedAsync(Events(), rejected, "القطعة مرهونة");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var db = await _db.Factory.CreateDbContextAsync(cts.Token);
            if (await db.Auctions.AnyAsync(a => a.AuctionId == rejected, cts.Token)) break;
            await Task.Delay(25, cts.Token);
        }

        var from = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var body = await Reader().GetFromJsonAsync<JsonElement>(
            $"/reports/auctions?from={Uri.EscapeDataString(from)}");

        var row = body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("auctionId").GetGuid() == rejected);

        Assert.Equal("Rejected", row.GetProperty("outcome").GetString());
        Assert.Equal("القطعة مرهونة", row.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task The_outcome_filter_narrows_the_report()
    {
        var client = Reader();

        var settled = await client.GetFromJsonAsync<JsonElement>("/reports/auctions?outcome=Settled");
        Assert.Equal(2, settled.GetProperty("count").GetInt32());

        var phase = await client.GetFromJsonAsync<JsonElement>(
            "/reports/auctions?phase=" + Uri.EscapeDataString("مخطط السعيد — المرحلة الثانية"));
        Assert.Equal(1, phase.GetProperty("count").GetInt32());
    }

    // --- revenue -----------------------------------------------------------

    [Fact]
    public async Task Revenue_keeps_the_sale_value_apart_from_the_cash_this_platform_took()
    {
        // The split that matters, and it is a fact about the platform rather than an
        // accounting preference: the land price does not pass through here. The
        // payment service takes the booklet fee, the deposit and brokerage, and
        // nothing else (§30). A report that added the sale value into "collected"
        // would claim the platform received 2.6 million riyals it never touched.
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/revenue?groupBy=month");
        var rows = body.GetProperty("items").EnumerateArray().ToList();

        var march = rows.Single(r => r.GetProperty("group").GetString() == "2026-03");

        // Two sales: 1,200,000 and the cascade's 1,400,000.
        Assert.Equal(2, march.GetProperty("auctionsSettled").GetInt32());
        Assert.Equal(2_600_000_00, march.GetProperty("saleValueMinorUnits").GetInt64());

        // Cash: 30,000 brokerage + 2,000 booklet fees + 100,000 forfeited.
        Assert.Equal(30_000_00, march.GetProperty("brokerageChargedMinorUnits").GetInt64());
        Assert.Equal(2_000_00, march.GetProperty("bookletFeesChargedMinorUnits").GetInt64());
        Assert.Equal(100_000_00, march.GetProperty("depositsForfeitedMinorUnits").GetInt64());
        Assert.Equal(132_000_00, march.GetProperty("collectedMinorUnits").GetInt64());

        // And the sale value is not in it.
        Assert.NotEqual(
            march.GetProperty("saleValueMinorUnits").GetInt64()
            + march.GetProperty("collectedMinorUnits").GetInt64(),
            march.GetProperty("collectedMinorUnits").GetInt64());
    }

    [Fact]
    public async Task A_refund_is_not_reported_as_income()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/revenue?groupBy=month");
        var march = body.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("group").GetString() == "2026-03");

        // Khalid's deposit on the sold auction went back to him.
        Assert.Equal(100_000_00, march.GetProperty("depositsRefundedMinorUnits").GetInt64());

        // The winner's applied deposit is neither a refund nor income: no money moved,
        // it was set against the price. Counting it as a refund would overstate what
        // went back to bidders by the largest deposit in the auction.
        Assert.Equal(100_000_00, march.GetProperty("depositsRefundedMinorUnits").GetInt64());
    }

    [Fact]
    public async Task Deposits_held_is_reported_by_phase_and_not_by_month()
    {
        // The figure has no date — it is what is held now — so dropping it into a
        // month bucket would invite somebody to read "March: 100,000 held" as money
        // taken in March. By phase it is exact.
        var client = Reader();

        var months = await client.GetFromJsonAsync<JsonElement>("/reports/revenue?groupBy=month");
        Assert.All(
            months.GetProperty("items").EnumerateArray(),
            r => Assert.Equal(0, r.GetProperty("depositsHeldMinorUnits").GetInt64()));

        var phases = await client.GetFromJsonAsync<JsonElement>("/reports/revenue?groupBy=phase");
        Assert.Equal(
            100_000_00,
            phases.GetProperty("items").EnumerateArray()
                .Sum(r => r.GetProperty("depositsHeldMinorUnits").GetInt64()));
    }

    [Fact]
    public async Task Revenue_groups_by_phase_as_well()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/revenue?groupBy=phase");
        var rows = body.GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("group").GetString()!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            2_600_000_00,
            rows["مخطط السعيد — المرحلة الأولى"].GetProperty("saleValueMinorUnits").GetInt64());
        Assert.Equal(
            1, rows["مخطط السعيد — المرحلة الثانية"].GetProperty("auctionsUnsold").GetInt32());
    }

    [Fact]
    public async Task An_unknown_grouping_is_refused_rather_than_silently_defaulted()
    {
        var response = await Reader().GetAsync("/reports/revenue?groupBy=quarter");

        // A report that quietly answered a different question than it was asked is
        // a report somebody acts on wrongly.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("month", await response.Content.ReadAsStringAsync());
    }

    // --- the other reports -------------------------------------------------

    [Fact]
    public async Task Participation_counts_the_funnel_and_the_bids_it_cannot_attribute()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/participation");
        var rows = body.GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("auctionId").GetGuid());

        var sold = rows[_sold];
        Assert.Equal(2, sold.GetProperty("bookletPaid").GetInt32());
        Assert.Equal(2, sold.GetProperty("depositPaid").GetInt32());
        Assert.Equal(2, sold.GetProperty("eligible").GetInt32());

        // The processor's total for the auction, not a count of these bidders' bids:
        // bids are binary frames on a per-auction topic nothing here consumes (D-12).
        Assert.Equal(6, sold.GetProperty("bidCount").GetInt32());

        Assert.Equal(1, rows[_cascaded].GetProperty("disqualified").GetInt32());
    }

    [Fact]
    public async Task The_plot_inventory_says_what_sold_and_at_what_package_rate()
    {
        var client = Reader();

        var all = await client.GetFromJsonAsync<JsonElement>("/reports/plots");
        Assert.Equal(6, all.GetProperty("count").GetInt32());

        var unsold = await client.GetFromJsonAsync<JsonElement>("/reports/plots?sold=false");
        Assert.Equal(2, unsold.GetProperty("count").GetInt32());

        var sold = await client.GetFromJsonAsync<JsonElement>("/reports/plots?sold=true");
        Assert.Equal(4, sold.GetProperty("count").GetInt32());

        // Every plot in a package carries the package's rate, because an auction
        // sells its plots as one indivisible lot (D-02) and nobody bid on a plot.
        var rates = sold.GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("pricePerSqmMinorUnits").GetDecimal())
            .Distinct()
            .ToList();

        Assert.Equal(2, rates.Count);
        Assert.Contains(120_000.00m, rates);
        Assert.Contains(140_000.00m, rates);
    }

    [Fact]
    public async Task Deposit_exposure_is_what_is_still_held_right_now()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/deposits");
        var rows = body.GetProperty("items").EnumerateArray().ToList();

        // Only the unsold auction still holds anything: its bidder's deposit was
        // never resolved because the award never became final. Everything on the two
        // settled auctions was refunded, forfeited or applied.
        var row = Assert.Single(rows);
        Assert.Equal(_unsold, row.GetProperty("auctionId").GetGuid());
        Assert.Equal(100_000_00, row.GetProperty("heldMinorUnits").GetInt64());
        Assert.Equal(1, row.GetProperty("biddersHolding").GetInt32());
    }

    [Fact]
    public async Task A_settled_auction_that_still_holds_a_deposit_shows_up()
    {
        // The condition the first version of the seed produced by accident, and it
        // is worth a test of its own: a deposit that was never released after the
        // award became final is money the municipality is holding with no reason
        // left to hold it (§8.3). The exposure report is where somebody notices.
        var orphan = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var stranded = Guid.NewGuid();
        var events = Events();

        // One winner throughout, and a second bidder whose deposit is never
        // resolved. The first version of this used a fresh Guid for the award and
        // another for the settlement, which is two different winners for one
        // auction — it passed, because the assertion is about the deposit, and it
        // was still a story that could not have happened.
        await Publish.ApprovedAsync(events, orphan, nameAr: "تأمين معلّق");
        await Publish.SettlementAsync(events, orphan, stranded, "Deposit", "Charged", 50_000_00);
        await Publish.AwardedAsync(events, orphan, winner, 900_000_00);
        await Publish.SettledAsync(events, orphan, winner, 900_000_00);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var db = await _db.Factory.CreateDbContextAsync(cts.Token);
            if (await db.Auctions.AnyAsync(
                    a => a.AuctionId == orphan && a.Outcome == AuctionOutcome.Settled, cts.Token))
                break;
            await Task.Delay(25, cts.Token);
        }

        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/deposits");
        var row = body.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("auctionId").GetGuid() == orphan);

        Assert.Equal("Settled", row.GetProperty("outcome").GetString());
        Assert.Equal(50_000_00, row.GetProperty("heldMinorUnits").GetInt64());
    }

    [Fact]
    public async Task Disqualifications_report_the_reason_and_the_cascade_that_followed()
    {
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/disqualifications");
        var row = body.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(_cascaded, row.GetProperty("auctionId").GetGuid());
        Assert.Equal(Sara, row.GetProperty("bidderId").GetGuid());
        Assert.Equal("لم يسدّد", row.GetProperty("reason").GetString());
        Assert.True(row.GetProperty("depositForfeited").GetBoolean());

        // Where the auction ended up after the cascade, which is the second half of
        // the story: the land still sold, for 100,000 less.
        Assert.Equal("Settled", row.GetProperty("outcome").GetString());
        Assert.Equal(1_400_000_00, row.GetProperty("finalPriceMinorUnits").GetInt64());
        Assert.Equal(2, row.GetProperty("auctionCascadeStep").GetInt32());
    }

    [Fact]
    public async Task The_phases_list_answers_how_big_the_programme_is()
    {
        // P-1: the deck says 372 plots and 165 + 162 is 327. This endpoint is what
        // settles that question from the data rather than from the slide.
        var body = await Reader().GetFromJsonAsync<JsonElement>("/reports/phases");
        var rows = body.GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("phase").GetString()!);

        Assert.Equal(4, rows["مخطط السعيد — المرحلة الأولى"].GetProperty("plots").GetInt32());
        Assert.Equal(2, rows["مخطط السعيد — المرحلة الأولى"].GetProperty("settled").GetInt32());
        Assert.Equal(2, rows["مخطط السعيد — المرحلة الثانية"].GetProperty("plots").GetInt32());
    }

    // --- csv ---------------------------------------------------------------

    [Fact]
    public async Task Every_report_downloads_as_a_csv_with_a_dated_filename()
    {
        foreach (var endpoint in Endpoints.Where(e => e != "/reports/phases"))
        {
            var response = await Reader().GetAsync($"{endpoint}?format=csv");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

            var name = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName;
            Assert.NotNull(name);
            Assert.Contains("eauction-", name);
            Assert.EndsWith(".csv", name!.Trim('"'));

            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        }
    }

    [Fact]
    public async Task The_csv_carries_the_arabic_and_the_figures_as_a_spreadsheet_wants_them()
    {
        var response = await Reader().GetAsync("/reports/auctions?format=csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.Contains("السعيد ١", text);
        Assert.Contains("مخطط السعيد — المرحلة الأولى", text);

        // Riyals with two places, not halalas: 120000000 would be the figure every
        // reader divides by a hundred in their head.
        Assert.Contains("1200000.00", text);
        Assert.DoesNotContain("120000000", text);
    }
}
