using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAuction.Core;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Audit.Tests;

/// <summary>
/// The read API, against the service as it actually boots: its consumer running,
/// its own database, and a token the way Keycloak issues one.
/// </summary>
[Collection("audit")]
public class ApiTests : IAsyncLifetime
{
    private AuditDatabase _db = null!;
    private AuthenticatedFactory<Program> _factory = null!;

    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Committee = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _db = await AuditDatabase.CreateAsync();

        _factory = new AuthenticatedFactory<Program>
        {
            Settings = new Dictionary<string, string?>
            {
                // Read at startup into a local, so it has to be in place before the
                // host is built rather than registered afterwards.
                ["ConnectionStrings:Audit"] = _db.ConnectionString,
            }
        };

        // Forces the host up, so the consumer is following the stream before
        // anything is published to it.
        _ = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _db.DisposeAsync();
    }

    private IEventStream Events() => _factory.Services.GetRequiredService<IEventStream>();

    private HttpClient Auditor() =>
        _factory.CreateClient().As(Guid.NewGuid(), Roles.Auditor);

    private async Task SeedAsync()
    {
        var events = Events();

        await StaffActions.PublishAsync(events, Admin, "CreateAuctionDraft", "auction/saeed", "مخطط السعيد");
        await StaffActions.PublishAsync(events, Admin, "UpdateAuctionDetails", "auction/saeed",
            "The reserve price was changed (the figure is deliberately not recorded here).");
        await StaffActions.PublishAsync(events, Committee, "ApproveAuction", "auction/saeed",
            roles: "award-committee");
        await StaffActions.PublishAsync(events, Admin, "VerifyBankGuarantee",
            "subscription/saeed:sara", "A bank guarantee was accepted in place of the deposit.");

        await WaitForAsync(4);
    }

    private async Task WaitForAsync(int entries)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (true)
        {
            await using var db = await _db.Factory.CreateDbContextAsync(cts.Token);
            if (await db.Entries.CountAsync(cts.Token) >= entries) return;
            await Task.Delay(25, cts.Token);
        }
    }

    // --- authorization -----------------------------------------------------

    [Fact]
    public async Task The_trail_is_not_readable_without_a_token()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().Anonymous().GetAsync("/audit")).StatusCode);
    }

    [Fact]
    public async Task Nobody_who_operates_the_platform_can_read_the_trail()
    {
        // The separation that makes the trail worth keeping. An administrator who
        // could read it could at least see what has been noticed about them, and
        // the one role that opens it is held by nobody who prepares an auction,
        // confirms an award, or runs a hall.
        foreach (var role in new[]
        {
            Roles.AuctionAdmin, Roles.AwardCommittee, Roles.Operator, Roles.Bidder
        })
        {
            var client = _factory.CreateClient().As(Guid.NewGuid(), role);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/audit/0")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden, (await client.GetAsync("/audit/verify")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden, (await client.GetAsync("/audit/actions")).StatusCode);
        }
    }

    [Fact]
    public async Task There_is_no_way_to_write_to_the_trail_over_http()
    {
        // Not "a write that is forbidden": no write endpoint exists. An auditor's
        // own token gets the same answer, because the protection is the absence of
        // the route rather than a policy on it.
        var client = Auditor();

        foreach (var attempt in new[]
        {
            client.PostAsJsonAsync("/audit", new { action = "invented" }),
            client.PostAsJsonAsync("/audit/0", new { action = "invented" }),
            client.DeleteAsync("/audit/0"),
            client.PutAsJsonAsync("/audit/0", new { action = "invented" }),
        })
        {
            var response = await attempt;
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"Expected no write route, got {(int)response.StatusCode}.");
        }
    }

    // --- reading -----------------------------------------------------------

    [Fact]
    public async Task An_auditor_reads_the_trail_newest_first()
    {
        await SeedAsync();

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit");

        Assert.Equal(4, body.GetProperty("total").GetInt32());

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["VerifyBankGuarantee", "ApproveAuction", "UpdateAuctionDetails", "CreateAuctionDraft"],
            items.Select(i => i.GetProperty("action").GetString()));

        // By offset rather than by timestamp, because four services' clocks would
        // otherwise be able to put an approval before the submission it approved.
        Assert.Equal(
            new[] { 3L, 2L, 1L, 0L }, items.Select(i => i.GetProperty("offset").GetInt64()));
    }

    [Fact]
    public async Task The_reserve_price_entry_records_that_it_changed_and_not_to_what()
    {
        await SeedAsync();

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit?action=UpdateAuctionDetails");
        var entry = body.GetProperty("items").EnumerateArray().Single();

        var details = entry.GetProperty("details").GetString();
        Assert.Contains("reserve price was changed", details);

        // The guard D-23 exists for. A figure here would be السعر الاحتياطي, in a
        // row, in a different service's database, for ever.
        //
        // The assertion is on the composed summary and not on the whole payload,
        // because the payload carries an actor id and a timestamp and a regex over
        // the lot matches those. What the auction service never puts in is checked
        // on the auction service's own side, where the figure is in scope —
        // AuditTrailTests in EAuction.AuctionAdmin.Tests.
        Assert.DoesNotMatch(@"\d", details!);
    }

    [Fact]
    public async Task The_filters_narrow_by_who_what_and_which()
    {
        await SeedAsync();
        var client = Auditor();

        var byActor = await client.GetFromJsonAsync<JsonElement>($"/audit?actor={Committee}");
        Assert.Equal(1, byActor.GetProperty("total").GetInt32());
        Assert.Equal("ApproveAuction",
            byActor.GetProperty("items")[0].GetProperty("action").GetString());

        var byAction = await client.GetFromJsonAsync<JsonElement>("/audit?action=ApproveAuction");
        Assert.Equal(1, byAction.GetProperty("total").GetInt32());

        // A prefix, so "every auction" is askable by someone who has only the type.
        var byType = await client.GetFromJsonAsync<JsonElement>("/audit?subject=auction/");
        Assert.Equal(3, byType.GetProperty("total").GetInt32());

        var one = await client.GetFromJsonAsync<JsonElement>("/audit?subject=subscription/saeed:sara");
        Assert.Equal(1, one.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_roles_recorded_are_the_ones_the_token_carried()
    {
        await SeedAsync();

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit?action=ApproveAuction");
        var entry = body.GetProperty("items").EnumerateArray().Single();

        // Not looked up now. Whether the person was entitled to approve is a
        // question about what was true then.
        Assert.Equal("award-committee", entry.GetProperty("actorRoles").GetString());
    }

    [Fact]
    public async Task One_entry_is_readable_by_its_offset()
    {
        await SeedAsync();
        var client = Auditor();

        var entry = await client.GetFromJsonAsync<JsonElement>("/audit/0");
        Assert.Equal("CreateAuctionDraft", entry.GetProperty("action").GetString());
        Assert.Equal(64, entry.GetProperty("hash").GetString()!.Length);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/audit/9999")).StatusCode);
    }

    [Fact]
    public async Task The_vocabulary_is_listed_with_counts()
    {
        await SeedAsync();

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/actions");
        var actions = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("action").GetString())
            .ToList();

        Assert.Equal(4, actions.Count);
        Assert.Contains("VerifyBankGuarantee", actions);
    }

    // --- verification ------------------------------------------------------

    [Fact]
    public async Task An_untouched_trail_verifies()
    {
        await SeedAsync();

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        Assert.True(body.GetProperty("intact").GetBoolean());
        Assert.Equal(4, body.GetProperty("checkedEntries").GetInt32());
        Assert.Empty(body.GetProperty("gaps").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("brokeAt").ValueKind);
        Assert.Equal(0, body.GetProperty("missingTail").GetInt64());
    }

    [Fact]
    public async Task An_altered_entry_is_found_and_named()
    {
        await SeedAsync();

        // The trigger has to be lifted first, which is the honest version of this
        // attack: changing a row is not something the service's own credentials can
        // do, and doing it anyway leaves the chain to tell.
        await _db.AllowTamperingAsync();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            // Parameterised, not interpolated: ExecuteSqlRaw reads braces in the
            // SQL as format placeholders, and a JSON payload is nothing but braces.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE audit_entry SET "Payload" = {0} WHERE "Offset" = 1;""",
                """{"actorSubject":"00000000-0000-0000-0000-000000000000"}""");
        }

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        Assert.False(body.GetProperty("intact").GetBoolean());

        // Offset 1 is where it stops, because the payload no longer hashes to the
        // stored hash. Everything before it still verifies, which is what makes the
        // report useful rather than just alarming.
        Assert.Equal(1, body.GetProperty("brokeAt").GetInt64());
        Assert.Equal("hash", body.GetProperty("broke").GetString());
    }

    [Fact]
    public async Task Rewriting_only_the_readable_columns_is_caught_too()
    {
        // The alteration the chain alone would miss, and the reason verify checks
        // the projection as well as the hashes. Details is not hashed — it is
        // derived from the payload, so hashing it would add nothing — but it is
        // what the API returns and what an auditor reads. Changing it while leaving
        // the evidence beside it intact is the cleverest version of this attack.
        await SeedAsync();
        await _db.AllowTamperingAsync();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE audit_entry SET "Details" = 'Nothing happened.' WHERE "Offset" = 1;""");
        }

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        Assert.False(body.GetProperty("intact").GetBoolean());
        Assert.Equal(1, body.GetProperty("brokeAt").GetInt64());
        Assert.Equal("projection", body.GetProperty("broke").GetString());
    }

    [Fact]
    public async Task Relabelling_an_action_is_caught()
    {
        // The same hole from the other direction: an approval relabelled as a
        // draft would hide from every filter an auditor is likely to use.
        await SeedAsync();
        await _db.AllowTamperingAsync();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE audit_entry SET "Action" = 'AddPlot' WHERE "Offset" = 2;""");
        }

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        Assert.False(body.GetProperty("intact").GetBoolean());
        Assert.Equal(2, body.GetProperty("brokeAt").GetInt64());
        Assert.Equal("projection", body.GetProperty("broke").GetString());
    }

    [Fact]
    public async Task A_removed_entry_breaks_the_link_and_shows_as_a_gap()
    {
        await SeedAsync();
        await _db.AllowTamperingAsync();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM audit_entry WHERE "Offset" = 1;""");
        }

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        Assert.False(body.GetProperty("intact").GetBoolean());

        // Two independent signals, and both matter. The offsets have a hole in
        // them, which says something is missing; and offset 2 claims a predecessor
        // that is no longer the chain's head, which says what was removed was a
        // record rather than never written.
        var gap = body.GetProperty("gaps").EnumerateArray().Single();
        Assert.Equal(0, gap.GetProperty("after").GetInt64());
        Assert.Equal(2, gap.GetProperty("before").GetInt64());

        Assert.Equal(2, body.GetProperty("brokeAt").GetInt64());
        Assert.Equal("previous-hash", body.GetProperty("broke").GetString());
    }

    [Fact]
    public async Task A_truncated_trail_shows_up_against_the_topic_rather_than_the_chain()
    {
        await SeedAsync();
        await _db.AllowTamperingAsync();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM audit_entry WHERE "Offset" = 3;""");
        }

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        // The one tampering a hash chain cannot see by itself: lop entries off the
        // end and what is left verifies perfectly. Nothing is broken and there is
        // no gap —
        Assert.Equal(JsonValueKind.Null, body.GetProperty("brokeAt").ValueKind);
        Assert.Empty(body.GetProperty("gaps").EnumerateArray());
        Assert.True(body.GetProperty("intact").GetBoolean());

        // — and the broker still holds the record, so the two offsets disagree.
        // That is why this endpoint reports them separately instead of folding them
        // into one verdict: an auditor needs to tell "truncated" from "a second
        // behind", and only these numbers can.
        Assert.Equal(2, body.GetProperty("storedThrough").GetInt64());
        Assert.Equal(3, body.GetProperty("topicEnd").GetInt64());
        Assert.Equal(1, body.GetProperty("missingTail").GetInt64());
    }

    [Fact]
    public async Task A_bounded_verify_checks_a_page_without_rereading_the_trail()
    {
        await SeedAsync();

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify?from=2&to=3");

        // Seeded from the hash of offset 1, so a page in the middle of the chain
        // verifies on its own.
        Assert.True(body.GetProperty("intact").GetBoolean());
        Assert.Equal(2, body.GetProperty("checkedEntries").GetInt32());
        Assert.Equal(2, body.GetProperty("firstOffset").GetInt64());
    }

    [Fact]
    public async Task Verifying_an_empty_trail_is_not_a_failure()
    {
        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        Assert.True(body.GetProperty("intact").GetBoolean());
        Assert.Equal(0, body.GetProperty("checkedEntries").GetInt32());

        // Nothing on the topic either, so there is nothing missing. The shape is
        // the full one — see the test below for why that matters.
        Assert.Equal(-1, body.GetProperty("storedThrough").GetInt64());
        Assert.Equal(0, body.GetProperty("missingTail").GetInt64());
    }

    [Fact]
    public async Task A_trail_wiped_entirely_still_shows_against_the_topic()
    {
        // The state most worth shouting about, and the one the first version of
        // this endpoint reported as `intact: true` with nothing else: the table
        // emptied while the topic still holds every record. It happened because an
        // empty range returned a shorter object that left out the two numbers that
        // would have shown it.
        await SeedAsync();
        await _db.AllowTamperingAsync();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_entry;");
        }

        var body = await Auditor().GetFromJsonAsync<JsonElement>("/audit/verify");

        // Nothing left to contradict itself, so the chain has nothing to say —
        Assert.Equal(0, body.GetProperty("checkedEntries").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("brokeAt").ValueKind);

        // — and the broker says four records are missing.
        Assert.Equal(-1, body.GetProperty("storedThrough").GetInt64());
        Assert.Equal(3, body.GetProperty("topicEnd").GetInt64());
        Assert.Equal(4, body.GetProperty("missingTail").GetInt64());
    }
}
