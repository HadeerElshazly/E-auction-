using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Persistence;
using EAuction.Outbox;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// The endpoints behind the portal's newer actions: «حذف المسودة», «اسم المزاد
/// والمرحلة» on their own, «بانتظار الاعتماد» for the committee, an amendment to a
/// published auction (§6.5), and the number every response now carries (§6.6).
/// </summary>
public class AuctionApiTests : IDisposable
{
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Committee = Guid.NewGuid();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AuthenticatedFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient As(Guid who, params string[] roles) => _factory.CreateClient().As(who, roles);

    private IDbContextFactory<AdminDbContext> Db() =>
        _factory.Services.GetRequiredService<IDbContextFactory<AdminDbContext>>();

    private async Task<List<StaffActionRecorded>> ActionsOnAsync(Guid auctionId)
    {
        await using var db = await Db().CreateDbContextAsync();
        var rows = await db.Outbox.AsNoTracking()
            .Where(m => m.AggregateType == "staff-action" && m.AggregateId == AuditSubject.Auction(auctionId))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => m.Payload)
            .ToListAsync();
        return rows.Select(p => JsonSerializer.Deserialize<StaffActionRecorded>(p, Json)!).ToList();
    }

    private async Task<List<OutboxMessage>> EventsOnAsync(Guid auctionId)
    {
        await using var db = await Db().CreateDbContextAsync();
        return await db.Outbox.AsNoTracking()
            .Where(m => m.AggregateId == auctionId.ToString())
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync();
    }

    private static async Task<JsonElement> DraftAsync(HttpClient admin, string nameAr = "مخطط السعيد")
    {
        var response = await admin.PostAsJsonAsync(
            "/auctions", new { createdByUserId = Admin, nameAr, nameEn = "Al-Saeed" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Terms(long deposit = 100_000_00L, long booklet = 1_000_00L, int startInDays = 7) => new
    {
        nameAr = "مخطط السعيد",
        nameEn = "Al-Saeed",
        channel = "Online",
        bidderVisibility = "Masked",
        startsAt = DateTimeOffset.UtcNow.AddDays(startInDays),
        endsAt = DateTimeOffset.UtcNow.AddDays(startInDays + 1),
        openingPriceMinorUnits = 2_000_000_00L,
        reservePriceMinorUnits = (long?)1_500_000_00L,
        minIncrementMinorUnits = 50_000_00L,
        depositMinorUnits = deposit,
        brokerageFeePercent = 2.5m,
        bookletPriceMinorUnits = booklet,
        quietPeriodSeconds = 120,
        maxExtensions = 3,
        phase = (string?)null,
    };

    /// <summary>Drafted, completed and submitted — before the committee.</summary>
    private static async Task<Guid> SubmittedAsync(HttpClient admin)
    {
        var id = (await DraftAsync(admin)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/auctions/{id}", Terms())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/auctions/{id}/plots", new
        {
            plotNumber = "1200", areaSqm = 950.5m, latitude = "21.5", longitude = "39.2",
            descriptionAr = "قطعة", descriptionEn = "Plot",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/auctions/{id}/booklet", new { documentId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/auctions/{id}/submit", null)).StatusCode);
        return id;
    }

    /// <summary>Approved: on the catalogue, so an amendment is possible.</summary>
    private static async Task<Guid> PublishedAsync(HttpClient admin, HttpClient committee)
    {
        var id = await SubmittedAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await committee.PostAsync($"/auctions/{id}/approve", null)).StatusCode);
        return id;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/auctions/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement?> AwaitingAsync(HttpClient client, Guid id)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/auctions/awaiting-approval?take=200");
        foreach (var item in page.GetProperty("items").EnumerateArray())
            if (item.GetProperty("id").GetGuid() == id) return item;
        return null;
    }

    [Fact]
    public async Task Every_auction_gets_the_next_number_and_every_response_carries_it()
    {
        var admin = As(Admin, Roles.AuctionAdmin);

        var first = await DraftAsync(admin, "الأول");
        var second = await DraftAsync(admin, "الثاني");

        var n1 = first.GetProperty("number").GetInt64();
        var n2 = second.GetProperty("number").GetInt64();
        Assert.True(n1 > 0);
        // Later, not exactly next: the other test classes create drafts in the same
        // database at the same time, and the sequence is shared — which is the point.
        Assert.True(n2 > n1, $"the second draft got {n2}, not a number after {n1}");

        var fetched = await GetAsync(admin, second.GetProperty("id").GetGuid());
        Assert.Equal(n2, fetched.GetProperty("number").GetInt64());
        Assert.Equal("None", fetched.GetProperty("amendment").GetString());

        var list = await admin.GetFromJsonAsync<JsonElement>("/auctions?take=200");
        var row = list.GetProperty("items").EnumerateArray()
            .Single(a => a.GetProperty("id").GetGuid() == second.GetProperty("id").GetGuid());
        Assert.Equal(n2, row.GetProperty("number").GetInt64());
    }

    [Fact]
    public async Task A_draft_is_deleted_and_the_trail_keeps_the_deletion()
    {
        var admin = As(Admin, Roles.AuctionAdmin);
        var id = (await DraftAsync(admin)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/auctions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/auctions/{id}")).StatusCode);

        var deletion = (await ActionsOnAsync(id)).Last();
        Assert.Equal("DeleteAuctionDraft", deletion.Action);
        Assert.Equal(Admin, deletion.ActorSubject);
        Assert.Contains("مخطط السعيد", deletion.Details);
    }

    [Fact]
    public async Task A_published_auction_is_not_deleted_and_the_committee_deletes_nothing()
    {
        var admin = As(Admin, Roles.AuctionAdmin);
        var committee = As(Committee, Roles.AwardCommittee);

        var draft = (await DraftAsync(admin)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await committee.DeleteAsync($"/auctions/{draft}")).StatusCode);

        var published = await PublishedAsync(admin, committee);
        var refused = await admin.DeleteAsync($"/auctions/{published}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/auctions/{published}")).StatusCode);
    }

    [Fact]
    public async Task The_name_and_phase_are_edited_on_their_own_and_recorded_field_by_field()
    {
        var admin = As(Admin, Roles.AuctionAdmin);
        var id = (await DraftAsync(admin)).GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync($"/auctions/{id}", Terms());

        var response = await admin.PutAsJsonAsync($"/auctions/{id}/name",
            new { nameAr = "مخطط السعيد — قطعة 1200", nameEn = "Al-Saeed Plot 1200", phase = "المرحلة الأولى" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("مخطط السعيد — قطعة 1200", body.GetProperty("nameAr").GetString());
        Assert.Equal("المرحلة الأولى", body.GetProperty("phase").GetString());
        // The terms were left alone.
        Assert.Equal(2_000_000_00L, body.GetProperty("openingPriceMinorUnits").GetInt64());

        var rename = (await ActionsOnAsync(id)).Single(a => a.Action == "RenameAuction");
        Assert.Contains("الاسم", rename.Details);
        Assert.Contains("المرحلة", rename.Details);
        Assert.Contains("←", rename.Details);
    }

    [Fact]
    public async Task The_committee_sees_what_awaits_it_new_auctions_and_amendments_alike()
    {
        var admin = As(Admin, Roles.AuctionAdmin);
        var committee = As(Committee, Roles.AwardCommittee);

        var id = await SubmittedAsync(admin);

        var waiting = await AwaitingAsync(committee, id);
        Assert.NotNull(waiting);
        Assert.Equal("New", waiting.Value.GetProperty("kind").GetString());
        Assert.NotEqual(JsonValueKind.Null, waiting.Value.GetProperty("submittedAt").ValueKind);
        Assert.True(waiting.Value.GetProperty("number").GetInt64() > 0);

        Assert.Equal(HttpStatusCode.OK, (await committee.PostAsync($"/auctions/{id}/approve", null)).StatusCode);
        Assert.Null(await AwaitingAsync(committee, id));

        // A change to the published auction: an amendment, and the committee's again
        // once it is submitted.
        var rename = await admin.PutAsJsonAsync($"/auctions/{id}/name",
            new { nameAr = "مخطط السعيد — معدَّل", nameEn = "Al-Saeed (amended)", phase = (string?)null });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var amending = await GetAsync(admin, id);
        Assert.Equal("Editing", amending.GetProperty("amendment").GetString());
        Assert.Contains(amending.GetProperty("status").GetString(), new[] { "Approved", "Scheduled" });
        Assert.Null(await AwaitingAsync(committee, id));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/auctions/{id}/submit", null)).StatusCode);
        var pending = await AwaitingAsync(committee, id);
        Assert.NotNull(pending);
        Assert.Equal("Amendment", pending.Value.GetProperty("kind").GetString());
        Assert.Equal("مخطط السعيد — معدَّل", pending.Value.GetProperty("nameAr").GetString());

        // With the committee, the administrator waits.
        var refused = await admin.PutAsJsonAsync($"/auctions/{id}/name",
            new { nameAr = "x", nameEn = "y", phase = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await committee.PostAsync($"/auctions/{id}/approve", null)).StatusCode);
        Assert.Null(await AwaitingAsync(committee, id));
        var approved = await GetAsync(admin, id);
        Assert.Equal("None", approved.GetProperty("amendment").GetString());
        Assert.Contains(approved.GetProperty("status").GetString(), new[] { "Approved", "Scheduled" });

        // Republished whole, and the bidders' notice beside it.
        var events = await EventsOnAsync(id);
        Assert.Equal(2, events.Count(m => m.Type == nameof(AuctionApproved)));
        Assert.Equal(2, events.Count(m => m.Type == nameof(AuctionReserveSet)));
        var amended = Assert.Single(events, m => m.Type == nameof(AuctionAmended));
        Assert.Equal("auction-lifecycle", amended.AggregateType);
        Assert.Contains("معدَّل", events.Last(m => m.Type == nameof(AuctionApproved)).Payload);
    }

    [Fact]
    public async Task An_amendment_cannot_touch_the_deposit_or_the_booklet_price()
    {
        var admin = As(Admin, Roles.AuctionAdmin);
        var committee = As(Committee, Roles.AwardCommittee);
        var id = await PublishedAsync(admin, committee);

        var response = await admin.PutAsJsonAsync($"/auctions/{id}", Terms(deposit: 150_000_00L, booklet: 0));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("problems")
            .EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Contains(problems, p => p.Contains("التأمين"));
        Assert.Contains(problems, p => p.Contains("سعر الكراسة"));

        // A refused edit opened nothing.
        Assert.Equal("None", (await GetAsync(admin, id)).GetProperty("amendment").GetString());

        // A date is fair game, and that is an amendment.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/auctions/{id}", Terms(startInDays: 9))).StatusCode);
        Assert.Equal("Editing", (await GetAsync(admin, id)).GetProperty("amendment").GetString());
    }
}
