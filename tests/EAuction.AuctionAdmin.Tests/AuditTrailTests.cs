using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAuction.AuctionAdmin.Outbox;
using EAuction.AuctionAdmin.Persistence;
using EAuction.Core;
using EAuction.Outbox;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// The producing half of the staff audit trail (D-44).
///
/// The audit service's own tests prove the chain holds and that an alteration is
/// caught. These prove the rows get written in the first place, with the right
/// actor on them — and, for the one field where the obvious summary would be a
/// leak, that the reserve price is not in them.
/// </summary>
public class AuditTrailTests : IDisposable
{
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Committee = Guid.NewGuid();

    private readonly AuthenticatedFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    private IDbContextFactory<AdminDbContext> Db() =>
        _factory.Services.GetRequiredService<IDbContextFactory<AdminDbContext>>();

    private HttpClient As(Guid who, params string[] roles) =>
        _factory.CreateClient().As(who, roles);

    private async Task<List<StaffActionRecorded>> ActionsOnAsync(Guid auctionId)
    {
        await using var db = await Db().CreateDbContextAsync();

        var rows = await db.Outbox
            .AsNoTracking()
            .Where(m => m.AggregateType == "staff-action"
                     && m.AggregateId == AuditSubject.Auction(auctionId))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => m.Payload)
            .ToListAsync();

        return rows
            .Select(p => JsonSerializer.Deserialize<StaffActionRecorded>(
                p, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
    }

    private async Task<Guid> DraftAsync()
    {
        var response = await As(Admin, Roles.AuctionAdmin).PostAsJsonAsync(
            "/auctions", new { createdByUserId = Admin, nameAr = "مخطط السعيد", nameEn = "Al-Saeed" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private object Edit(long? reserve, string channel = "Online") => new
    {
        nameAr = "مخطط السعيد",
        nameEn = "Al-Saeed",
        channel,
        bidderVisibility = "Masked",
        startsAt = DateTimeOffset.UtcNow.AddDays(7),
        endsAt = DateTimeOffset.UtcNow.AddDays(8),
        openingPriceMinorUnits = 1_000_000_00L,
        reservePriceMinorUnits = reserve,
        minIncrementMinorUnits = 50_000_00L,
        depositMinorUnits = 100_000_00L,
        brokerageFeePercent = 2.5m,
        bookletPriceMinorUnits = 1_000_00L,
        quietPeriodSeconds = 120,
        maxExtensions = 3,
        phase = (string?)null,
    };

    [Fact]
    public async Task Creating_an_auction_records_who_created_it()
    {
        var id = await DraftAsync();

        var action = Assert.Single(await ActionsOnAsync(id));

        Assert.Equal("CreateAuctionDraft", action.Action);
        Assert.Equal(AuditSubject.Auction(id), action.Subject);

        // The token's subject, not the createdByUserId in the body. A trail that
        // took the actor from the request would say whatever the audited person
        // typed.
        Assert.Equal(Admin, action.ActorSubject);
        Assert.Contains(Roles.AuctionAdmin, action.ActorRoles);
    }

    [Fact]
    public async Task The_actor_is_the_token_and_not_the_body()
    {
        var someoneElse = Guid.NewGuid();

        var response = await As(Admin, Roles.AuctionAdmin).PostAsJsonAsync(
            "/auctions",
            new { createdByUserId = someoneElse, nameAr = "م", nameEn = "A" });

        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var action = Assert.Single(await ActionsOnAsync(id));
        Assert.Equal(Admin, action.ActorSubject);
        Assert.NotEqual(someoneElse, action.ActorSubject);
    }

    [Fact]
    public async Task Changing_the_reserve_is_recorded_without_the_figure()
    {
        // The leak this guards against, in the only place it could happen. السعر
        // الاحتياطي is kept off every topic by D-23 and leaves the service only on
        // the ACL-restricted auctions.sealed; an audit summary reading "reserve
        // changed from X to Y" would put it on a second topic, in a second
        // service's database, for ever.
        var id = await DraftAsync();

        var response = await As(Admin, Roles.AuctionAdmin)
            .PutAsJsonAsync($"/auctions/{id}", Edit(reserve: 1_750_000_00));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var update = (await ActionsOnAsync(id))
            .Single(a => a.Action == "UpdateAuctionDetails");

        Assert.Contains("reserve price was changed", update.Details);
        Assert.DoesNotContain("175000000", update.Details);
        Assert.DoesNotContain("1750000", update.Details);

        // Nor anywhere else in the row, which is what actually reaches the topic.
        await using var db = await Db().CreateDbContextAsync();
        var payloads = await db.Outbox.AsNoTracking()
            .Where(m => m.AggregateType == "staff-action")
            .Select(m => m.Payload)
            .ToListAsync();

        Assert.All(payloads, p => Assert.DoesNotContain("175000000", p));
    }

    [Fact]
    public async Task An_edit_that_leaves_the_reserve_alone_says_nothing_about_it()
    {
        var id = await DraftAsync();

        await As(Admin, Roles.AuctionAdmin).PutAsJsonAsync($"/auctions/{id}", Edit(reserve: null));

        var update = (await ActionsOnAsync(id)).Single(a => a.Action == "UpdateAuctionDetails");

        // Null means "leave it" (see ReserveUpdateTests), so an entry claiming it
        // changed would be false. The other fields the edit set are recorded, each
        // with its value before and after (الخاصية 14).
        Assert.NotNull(update.Details);
        Assert.DoesNotContain("reserve", update.Details);
        Assert.DoesNotContain("الحد الأدنى للبيع", update.Details);
        Assert.Contains("←", update.Details);
    }

    [Fact]
    public async Task The_award_workflow_leaves_a_trail_naming_the_committee_member()
    {
        var id = await DraftAsync();
        var admin = As(Admin, Roles.AuctionAdmin);
        var committee = As(Committee, Roles.AwardCommittee);

        await admin.PutAsJsonAsync($"/auctions/{id}", Edit(reserve: 1_500_000_00));
        await admin.PostAsJsonAsync($"/auctions/{id}/plots", new
        {
            deedNumber = "4/س/1200", areaSqm = 950.5m,
            latitude = "21.5", longitude = "39.2",
            descriptionAr = "قطعة", descriptionEn = "Plot",
        });
        // Required by Validate, so without it the submission is refused and there
        // is nothing to approve.
        await admin.PostAsJsonAsync($"/auctions/{id}/booklet", new { documentId = Guid.NewGuid() });

        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsync($"/auctions/{id}/submit", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await committee.PostAsync($"/auctions/{id}/approve", null)).StatusCode);

        var trail = await ActionsOnAsync(id);

        Assert.Equal(
            ["CreateAuctionDraft", "UpdateAuctionDetails", "AddPlot", "AttachBooklet",
             "SubmitAuctionForReview", "ApproveAuction"],
            trail.Select(a => a.Action));

        // The separation slide 6 draws, visible in the record: the person who set
        // the terms is not the person who approved them.
        var approval = trail.Single(a => a.Action == "ApproveAuction");
        Assert.Equal(Committee, approval.ActorSubject);
        Assert.Equal(Roles.AwardCommittee, approval.ActorRoles);

        Assert.All(
            trail.Where(a => a.Action != "ApproveAuction"),
            a => Assert.Equal(Admin, a.ActorSubject));
    }

    [Fact]
    public async Task A_rejected_change_leaves_no_entry()
    {
        // The cost of putting the audit row in the same transaction as the change,
        // stated as a test rather than left to be discovered. Approving a draft
        // that was never submitted is refused, and nothing is recorded — the row
        // and the rollback are the same transaction, which is the design (D-44).
        var id = await DraftAsync();

        var response = await As(Committee, Roles.AwardCommittee)
            .PostAsync($"/auctions/{id}/approve", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var trail = await ActionsOnAsync(id);
        Assert.DoesNotContain("ApproveAuction", trail.Select(a => a.Action));
    }

    [Fact]
    public async Task Collecting_a_halls_signing_key_is_recorded()
    {
        // The only read in this service that is audited, and the reason is the
        // value it hands over: whoever holds it can sign a bid for any eligible
        // bidder in that auction (§29).
        var clerk = Guid.NewGuid();
        var id = await DraftAsync();

        // Onsite, because only a hall auction has a clerk on the floor.
        var admin = As(Admin, Roles.AuctionAdmin);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/auctions/{id}", Edit(1_500_000_00, "Onsite"))).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(
                $"/auctions/{id}/clerk", new { clerkUserId = clerk })).StatusCode);

        var response = await As(clerk, Roles.Operator).GetAsync($"/auctions/{id}/clerk-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var trail = await ActionsOnAsync(id);

        var assignment = trail.Single(a => a.Action == "AssignClerk");
        Assert.Contains(clerk.ToString(), assignment.Details);

        var collection = trail.Single(a => a.Action == "ReadClerkSigningKey");
        Assert.Equal(clerk, collection.ActorSubject);
        Assert.Equal(Roles.Operator, collection.ActorRoles);
    }

    [Fact]
    public async Task A_clerk_who_may_not_have_the_key_leaves_no_entry()
    {
        // 403 before the row, so a refused attempt does not look like a collection.
        var id = await DraftAsync();
        var admin = As(Admin, Roles.AuctionAdmin);
        await admin.PutAsJsonAsync($"/auctions/{id}", Edit(1_500_000_00, "Onsite"));
        await admin.PutAsJsonAsync($"/auctions/{id}/clerk", new { clerkUserId = Guid.NewGuid() });

        var response = await As(Guid.NewGuid(), Roles.Operator).GetAsync($"/auctions/{id}/clerk-key");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var trail = await ActionsOnAsync(id);
        Assert.DoesNotContain("ReadClerkSigningKey", trail.Select(a => a.Action));
    }

    [Fact]
    public void A_staff_action_routes_to_the_audit_topic()
    {
        // Routed rather than skipped. The relay stops on a row it cannot place —
        // deliberately, so an unroutable event cannot be overtaken by a later one
        // for the same aggregate — so a missing line here would stall every auction
        // event behind the first audited action.
        Assert.Equal(Topics.StaffActions, TopicMap.Resolve("staff-action"));
    }
}
