using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAuction.Core;
using EAuction.Outbox;
using EAuction.Participant.Domain;
using EAuction.Participant.Integration;
using EAuction.Participant.Persistence;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Participant.Api.Tests;

/// <summary>
/// The things staff do to someone else's subscription, and the record of them (D-44).
///
/// Three endpoints out of a dozen, and the line between them and the rest is the
/// point: a citizen buying a booklet is a citizen using the product, while
/// accepting a bank guarantee in place of money makes that citizen eligible to bid
/// on state land on somebody's say-so, and somebody is who this records.
/// </summary>
public class ParticipantAuditTests : IDisposable
{
    private readonly AuthenticatedFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Guarantee = Guid.NewGuid();

    private IDbContextFactory<ParticipantDbContext> Db() =>
        _factory.Services.GetRequiredService<IDbContextFactory<ParticipantDbContext>>();

    private async Task<List<StaffActionRecorded>> ActionsOnAsync(Guid auction, Guid bidder)
    {
        await using var db = await Db().CreateDbContextAsync();

        var rows = await db.Outbox.AsNoTracking()
            .Where(m => m.AggregateType == "staff-action"
                     && m.AggregateId == AuditSubject.Subscription(auction, bidder))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => m.Payload)
            .ToListAsync();

        return rows
            .Select(p => JsonSerializer.Deserialize<StaffActionRecorded>(
                p, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
    }

    /// <summary>
    /// A bidder who has filed a bank guarantee and is waiting for it to be checked.
    ///
    /// Set up on the aggregate rather than through the endpoints, for the reason
    /// the rest of this project already gives: the payment steps only ask the
    /// payment service for money, and no payment service runs here.
    /// </summary>
    private async Task<(Guid Bidder, Guid Auction)> AwaitingVerificationAsync()
    {
        var bidder = Guid.NewGuid();
        var auction = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = await Db().CreateDbContextAsync();

        var terms = new AuctionTerms(
            auction, now.AddMinutes(-1), now.AddHours(1),
            depositMinorUnits: 100_000_00, bookletPriceMinorUnits: 1_000_00);

        db.AuctionTerms.Add(terms);

        var person = Bidder.FromNafath(
            bidder, "1" + Random.Shared.NextInt64(100_000_000, 999_999_999),
            "سارة", "Sara", now);
        person.CompleteProfile("+966500000001", "sara@example.sa", now);
        db.Bidders.Add(person);

        var subscription = Subscription.Start(auction, bidder);
        subscription.RequestBooklet(terms, now);
        subscription.ConfirmBookletPayment("SIM-BOO-SEED", now);
        subscription.AcceptTerms(now);
        subscription.ChooseDeposit(DepositMethod.BankGuarantee, terms, now);
        subscription.SubmitBankGuarantee(Guarantee, now.AddMonths(6), terms);
        db.Subscriptions.Add(subscription);

        await db.SaveChangesAsync();
        return (bidder, auction);
    }

    /// <summary>
    /// The same bidder, carried through to eligible by an administrator accepting
    /// the guarantee — which is itself an audited action, so the trail already has
    /// one entry in it before the tests below add theirs.
    /// </summary>
    private async Task<(Guid Bidder, Guid Auction)> EligibleAsync()
    {
        var (bidder, auction) = await AwaitingVerificationAsync();

        var response = await _factory.CreateClient().As(Admin, Roles.AuctionAdmin)
            .PostAsJsonAsync(
                Path(auction, bidder, "/guarantee/verify"), new { verifiedByUserId = Admin });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (bidder, auction);
    }

    private string Path(Guid auction, Guid bidder, string suffix) =>
        $"/auctions/{auction}/subscriptions/{bidder}{suffix}";

    [Fact]
    public async Task Accepting_a_bank_guarantee_records_who_accepted_it()
    {
        var (bidder, auction) = await AwaitingVerificationAsync();

        var response = await _factory.CreateClient().As(Admin, Roles.AuctionAdmin)
            .PostAsJsonAsync(
                Path(auction, bidder, "/guarantee/verify"),
                new { verifiedByUserId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(await ActionsOnAsync(auction, bidder));

        Assert.Equal("VerifyBankGuarantee", entry.Action);
        Assert.Equal(AuditSubject.Subscription(auction, bidder), entry.Subject);

        // The token's subject, not the verifiedByUserId the caller supplied. If a
        // forged guarantee turns up later, this is the only field that says who
        // actually accepted it.
        Assert.Equal(Admin, entry.ActorSubject);
        Assert.Contains(Roles.AuctionAdmin, entry.ActorRoles);
    }

    [Fact]
    public async Task Revoking_an_eligibility_records_the_reason()
    {
        var (bidder, auction) = await EligibleAsync();
        var client = _factory.CreateClient().As(Admin, Roles.AuctionAdmin);

        var response = await client.PostAsJsonAsync(
            Path(auction, bidder, "/revoke"), new { reason = "الضمان البنكي مزوَّر" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var trail = await ActionsOnAsync(auction, bidder);
        Assert.Equal(["VerifyBankGuarantee", "RevokeEligibility"], trail.Select(a => a.Action));
        Assert.Equal("الضمان البنكي مزوَّر", trail[1].Details);
    }

    [Fact]
    public async Task A_bidders_own_steps_are_not_staff_actions()
    {
        // Not an oversight. One row per bidder per step would be hundreds of
        // thousands of entries for an auction of any size, and the handful that
        // matter would be unfindable among them.
        var (bidder, auction) = await EligibleAsync();

        var response = await _factory.CreateClient().As(bidder, Roles.Bidder)
            .PostAsync(Path(auction, bidder, "/rotate-key"), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The administrator's acceptance is there, and the bidder's own rotation
        // is not.
        var trail = await ActionsOnAsync(auction, bidder);
        Assert.Equal(["VerifyBankGuarantee"], trail.Select(a => a.Action));
    }

    [Fact]
    public async Task An_administrator_rotating_someone_elses_key_is_a_staff_action()
    {
        // The same endpoint, recorded or not depending on who called it. Rotating
        // a bidder's key invalidates the one they are holding, so the bids they
        // were about to place stop being accepted — a thing done to a person
        // rather than a feature they used.
        var (bidder, auction) = await EligibleAsync();

        var response = await _factory.CreateClient().As(Admin, Roles.AuctionAdmin)
            .PostAsync(Path(auction, bidder, "/rotate-key"), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var trail = await ActionsOnAsync(auction, bidder);
        Assert.Equal(["VerifyBankGuarantee", "RotateBidderKey"], trail.Select(a => a.Action));
        Assert.Equal(Admin, trail[1].ActorSubject);
    }

    [Fact]
    public async Task A_refused_staff_action_leaves_no_entry()
    {
        // The audit row shares the transaction the refusal rolls back, which is the
        // design and its cost: a probe leaves nothing here. Verifying a guarantee
        // that was never filed is refused, and nothing is written.
        var bidder = Guid.NewGuid();
        var auction = Guid.NewGuid();

        await using (var db = await Db().CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            var terms = new AuctionTerms(
                auction, now.AddMinutes(-1), now.AddHours(1),
                depositMinorUnits: 100_000_00, bookletPriceMinorUnits: 1_000_00);

            db.AuctionTerms.Add(terms);
            db.Bidders.Add(Bidder.FromNafath(
                bidder, "1" + Random.Shared.NextInt64(100_000_000, 999_999_999),
                "خالد", "Khalid", now));
            db.Subscriptions.Add(Subscription.Start(auction, bidder));
            await db.SaveChangesAsync();
        }

        var response = await _factory.CreateClient().As(Admin, Roles.AuctionAdmin)
            .PostAsJsonAsync(
                Path(auction, bidder, "/guarantee/verify"),
                new { verifiedByUserId = Admin });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ActionsOnAsync(auction, bidder));
    }

    [Fact]
    public async Task The_two_staff_lookups_left_out_are_left_out_on_purpose()
    {
        // The exclusion §34 is least sure of, pinned so it is a decision rather than
        // an omission. An administrator reading a bidder's profile sees a name, a
        // phone number and an email; the clerk's roster names every eligible bidder
        // in the auction regardless of the masking setting (§29). Neither is
        // recorded, because a portal calls both on every page render — the roster
        // repeatedly throughout a hall auction — and a trail dominated by routine
        // lookups is a trail nobody reads.
        //
        // If that call is ever reversed, this test is what fails and where the new
        // reasoning goes.
        var (bidder, auction) = await EligibleAsync();
        var admin = _factory.CreateClient().As(Admin, Roles.AuctionAdmin);

        Assert.Equal(
            HttpStatusCode.OK, (await admin.GetAsync($"/bidders/{bidder}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.GetAsync($"/auctions/{auction}/subscriptions")).StatusCode);

        // The guarantee acceptance from EligibleAsync, and nothing the two reads
        // added.
        var trail = await ActionsOnAsync(auction, bidder);
        Assert.Equal(["VerifyBankGuarantee"], trail.Select(a => a.Action));
    }

    [Fact]
    public void A_staff_action_routes_to_the_audit_topic()
    {
        // The relay stops on a row it cannot place, so a missing line here would
        // stall every eligibility event behind the first audited action.
        Assert.Equal(
            Topics.StaffActions, new ParticipantOutboxRouter().Resolve("staff-action"));
    }
}
