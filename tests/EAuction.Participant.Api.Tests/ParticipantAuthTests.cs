using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAuction.Participant.Domain;
using EAuction.Participant.Persistence;
using EAuction.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EAuction.TestSupport;
using Xunit;

namespace EAuction.Participant.Api.Tests;

/// <summary>
/// Ownership, not just authentication. Being a bidder entitles you to act on
/// your own subscription and nobody else's.
/// </summary>
public class ParticipantAuthTests : IDisposable
{
    // Program is internal, so a public test class cannot take the factory as
    // a constructor parameter.
    private readonly AuthenticatedFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly Guid Khalid = Guid.NewGuid();
    private static readonly Guid AuctionId = Guid.NewGuid();

    /// <summary>
    /// National ID is uniquely indexed and the test database outlives a run, so a
    /// fixed value collides with the row the last run left behind.
    /// </summary>
    private static string FreshNationalId() =>
        "1" + Random.Shared.NextInt64(100_000_000, 999_999_999);

    private string Sub(Guid bidder, string suffix = "") =>
        $"/auctions/{AuctionId}/subscriptions/{bidder}{suffix}";

    [Fact]
    public async Task Registration_needs_a_token_because_the_identity_comes_from_it()
    {
        var response = await _factory.CreateClient().Anonymous()
            .PostAsJsonAsync("/bidders/register", new { nationalId = "1", nameAr = "a", nameEn = "b" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Registration_refuses_a_token_that_carries_no_national_id()
    {
        // A valid token is not enough. Without the Nafath claim there is no verified
        // identity to register, and the endpoint must not fall back to anything the
        // caller supplies.
        var response = await _factory.CreateClient().As(Sara, Roles.Bidder)
            .PostAsJsonAsync("/bidders/register", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Registration_ignores_a_national_id_in_the_request_body()
    {
        // The hole this closes: the endpoint used to read the body when the claim was
        // absent, so any bidder could register under anyone's national ID. It now
        // takes no body at all, and a body naming someone else changes nothing.
        var mine = FreshNationalId();
        var client = _factory.CreateClient().WithToken(
            TestJwt.FromNafath(Sara, mine, "سارة", "Sara", Roles.Bidder));

        var response = await client.PostAsJsonAsync(
            "/bidders/register", new { nationalId = "9999999999", nameAr = "x", nameEn = "y" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Sara, body.GetProperty("id").GetGuid());

        // And the identity stored is the token's, not the body's. NationalId is not on
        // the response (PDPL), so ask the endpoint that reports verification instead:
        // registering again is idempotent and must still return the same bidder.
        var again = await client.PostAsJsonAsync(
            "/bidders/register", new { nationalId = "9999999999" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var stored = _factory.Services
            .GetRequiredService<IDbContextFactory<ParticipantDbContext>>();
        await using var db = await stored.CreateDbContextAsync();
        var bidder = await db.Bidders.FindAsync(Sara);
        Assert.NotNull(bidder);
        Assert.Equal(mine, bidder!.NationalId);
        Assert.Equal("سارة", bidder.NameAr);
    }

    [Fact]
    public async Task One_national_id_cannot_become_two_bidders()
    {
        // A Keycloak account deleted and re-brokered hands the same citizen a new
        // subject. Letting that register a second bidder would put one person twice
        // in the same auction, with two deposits and two signing keys.
        var sharedId = FreshNationalId();

        var first = await _factory.CreateClient()
            .WithToken(TestJwt.FromNafath(Guid.NewGuid(), sharedId, "خالد", "Khalid", Roles.Bidder))
            .PostAsJsonAsync("/bidders/register", new { });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _factory.CreateClient()
            .WithToken(TestJwt.FromNafath(Guid.NewGuid(), sharedId, "خالد", "Khalid", Roles.Bidder))
            .PostAsJsonAsync("/bidders/register", new { });

        // A conflict the caller can act on, not the unique-index violation this used
        // to surface as a 500.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NationalIdAlreadyRegistered", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_bidder_cannot_read_another_bidder_s_signing_key()
    {
        // The most dangerous endpoint in the system: this hands over the
        // credential that signs bids. Anyone holding it could bid as that
        // person, and the signature would be indistinguishable from theirs.
        var response = await _factory.CreateClient().As(Khalid, Roles.Bidder)
            .GetAsync(Sub(Sara, "/signing-key"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Not_even_an_administrator_can_read_a_signing_key()
    {
        // Staff can verify guarantees and revoke subscriptions. They cannot
        // become a bidder — that would destroy the evidential value of a
        // signed bid, since nobody could later tell the two apart.
        var response = await _factory.CreateClient()
            .As(Guid.NewGuid(), Roles.AuctionAdmin, Roles.AwardCommittee)
            .GetAsync(Sub(Sara, "/signing-key"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_with_no_bidder_role_cannot_read_a_signing_key_even_its_own()
    {
        // The ownership check above would let this through: the subject matches. The
        // role policy is the outer lock, and it is on the endpoint itself rather than
        // inherited from a fallback configured in another file — an endpoint that
        // hands out bid-signing credentials should not be one `FallbackPolicy` edit
        // away from being open to any authenticated caller.
        var response = await _factory.CreateClient()
            .As(Sara, Roles.Operator)
            .GetAsync(Sub(Sara, "/signing-key"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_read_a_signing_key()
    {
        var response = await _factory.CreateClient().Anonymous()
            .GetAsync(Sub(Sara, "/signing-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_bidder_cannot_read_the_room_s_roster()
    {
        // It names every eligible bidder in the auction. A bidder reading it would
        // learn exactly what D-22 masks, from the endpoint next to the one that
        // masks it.
        var response = await _factory.CreateClient().As(Sara, Roles.Bidder)
            .GetAsync($"/auctions/{AuctionId}/subscriptions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_read_the_room_s_roster()
    {
        var response = await _factory.CreateClient().Anonymous()
            .GetAsync($"/auctions/{AuctionId}/subscriptions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_clerk_can_read_the_room_s_roster()
    {
        var response = await _factory.CreateClient().As(Guid.NewGuid(), Roles.Operator)
            .GetAsync($"/auctions/{AuctionId}/subscriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_roster_carries_no_national_id_and_no_payment_history()
    {
        // The clerk needs a name and a paddle number. Everything else on a
        // subscription is the bidder's own business.
        var response = await _factory.CreateClient().As(Guid.NewGuid(), Roles.Operator)
            .GetAsync($"/auctions/{AuctionId}/subscriptions");

        var body = await response.Content.ReadAsStringAsync();

        foreach (var absent in new[]
                 {
                     "nationalId", "paymentRef", "depositPaidAt", "guarantee", "keyEpoch",
                 })
            Assert.DoesNotContain(absent, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bidder_cannot_drive_another_bidder_s_subscription()
    {
        // Stepped up on purpose. Three of these five endpoints are behind the
        // second factor, so an ordinary token would be refused by the step-up
        // handler before the ownership check ran — and this test would pass on a
        // 403 that says nothing about ownership.
        var client = _factory.CreateClient().WithToken(TestJwt.SteppedUp(Khalid, Roles.Bidder));

        foreach (var (path, body) in new (string, object?)[]
        {
            (Sub(Sara, "/booklet"), null),
            (Sub(Sara, "/terms"), null),
            (Sub(Sara, "/deposit-method"), new { method = 0 }),
            (Sub(Sara, "/deposit"), null),
            (Sub(Sara, "/guarantee"), new { documentId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddYears(1) })
        })
        {
            var response = body is null
                ? await client.PostAsync(path, null)
                : await client.PostAsJsonAsync(path, body);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // And not as a step-up problem: retrying with a fresh second factor
            // would be pointless, because the subscription is not theirs.
            Assert.DoesNotContain("StepUp", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_bidder_cannot_start_a_subscription_in_someone_else_s_name()
    {
        var response = await _factory.CreateClient().As(Khalid, Roles.Bidder)
            .PostAsJsonAsync($"/auctions/{AuctionId}/subscriptions", new { bidderId = Sara });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_bidder_cannot_verify_their_own_bank_guarantee()
    {
        // Self-verification would make the whole control decorative.
        var response = await _factory.CreateClient().As(Sara, Roles.Bidder)
            .PostAsJsonAsync(Sub(Sara, "/guarantee/verify"), new { verifiedByUserId = Sara });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_bidder_cannot_revoke_a_subscription()
    {
        var response = await _factory.CreateClient().As(Sara, Roles.Bidder)
            .PostAsJsonAsync(Sub(Sara, "/revoke"), new { reason = "no" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Staff_reach_the_guarantee_and_revocation_surface()
    {
        // Not found rather than forbidden: the role passed and the
        // subscription simply does not exist.
        var client = _factory.CreateClient().As(Guid.NewGuid(), Roles.AuctionAdmin);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(Sub(Sara, "/guarantee/verify"),
                new { verifiedByUserId = Guid.NewGuid() })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(Sub(Sara, "/revoke"), new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var response = await _factory.CreateClient()
            .WithToken(TestJwt.Expired(Sara, Roles.Bidder))
            .GetAsync(Sub(Sara, "/signing-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// The grant that opens كراسة الشروط.
///
/// The booklet is Restricted in the document service: no role opens it, not an
/// administrator's. This endpoint is the only way in, so what it refuses is the
/// whole of the control.
/// </summary>
public class BookletGrantTests : IDisposable
{
    private static readonly byte[] GrantKey = DocumentGrants.NewKey();

    private readonly AuthenticatedFactory<Program> _factory = new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["Documents:GrantKeyHex"] = Convert.ToHexString(GrantKey),
        },
    };

    public void Dispose() => _factory.Dispose();

    private static readonly Guid Booklet = Guid.NewGuid();

    private IDbContextFactory<ParticipantDbContext> Db() =>
        _factory.Services.GetRequiredService<IDbContextFactory<ParticipantDbContext>>();

    /// <summary>A bidder with a subscription at the given stage, and an auction with a booklet.</summary>
    private async Task<(Guid Bidder, Guid Auction)> SeedAsync(bool bookletPaid, bool withBooklet = true)
    {
        var bidder = Guid.NewGuid();
        var auction = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = await Db().CreateDbContextAsync();

        var terms = new AuctionTerms(
            auction, now.AddMinutes(-1), now.AddHours(1),
            depositMinorUnits: 100_000_00, bookletPriceMinorUnits: 1_000_00,
            bookletDocumentId: withBooklet ? Booklet : null);

        db.AuctionTerms.Add(terms);
        db.Bidders.Add(Bidder.FromNafath(
            bidder, "1" + Random.Shared.NextInt64(100_000_000, 999_999_999),
            "سارة", "Sara", now));

        var subscription = Subscription.Start(auction, bidder);
        subscription.RequestBooklet(terms, now);
        if (bookletPaid) subscription.ConfirmBookletPayment("SIM-BOO-SEED", now);
        db.Subscriptions.Add(subscription);

        await db.SaveChangesAsync();
        return (bidder, auction);
    }

    private string Path(Guid auction, Guid bidder) =>
        $"/auctions/{auction}/subscriptions/{bidder}/booklet-grant";

    [Fact]
    public async Task A_bidder_who_paid_gets_a_grant_that_verifies()
    {
        var (bidder, auction) = await SeedAsync(bookletPaid: true);

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.For(bidder, Roles.Bidder))
            .GetAsync(Path(auction, bidder));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var granted = body.GetProperty("documentId").GetGuid();
        var grant = body.GetProperty("grant").GetString();

        Assert.Equal(Booklet, granted);

        // Verified with the key the host was given, so this asserts the grant the
        // document service will actually accept rather than that a string came back.
        Assert.True(DocumentGrants.Verify(
            GrantKey, grant, Booklet, bidder, DateTimeOffset.UtcNow));

        // And for nobody else.
        Assert.False(DocumentGrants.Verify(
            GrantKey, grant, Booklet, Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_bidder_who_has_not_paid_gets_no_grant()
    {
        // The whole point of the gate. Asking for the booklet is not paying for it:
        // the fee is requested and settles asynchronously, and this is the window
        // in which a bidder would otherwise read what they have not bought.
        var (bidder, auction) = await SeedAsync(bookletPaid: false);

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.For(bidder, Roles.Bidder))
            .GetAsync(Path(auction, bidder));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("BookletNotPurchased", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_bidder_cannot_get_a_grant_for_another()
    {
        var (bidder, auction) = await SeedAsync(bookletPaid: true);

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.For(Guid.NewGuid(), Roles.Bidder))
            .GetAsync(Path(auction, bidder));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_get_a_grant_for_a_bidder()
    {
        // Not an oversight. A grant names a subject, and an administrator who could
        // mint one for a bidder could mint one for themselves — which is exactly the
        // thing Restricted exists to prevent.
        var (bidder, auction) = await SeedAsync(bookletPaid: true);

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.For(Guid.NewGuid(), Roles.AuctionAdmin))
            .GetAsync(Path(auction, bidder));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_auction_with_no_booklet_attached_yields_no_grant()
    {
        var (bidder, auction) = await SeedAsync(bookletPaid: true, withBooklet: false);

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.For(bidder, Roles.Bidder))
            .GetAsync(Path(auction, bidder));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// The second factor, at the endpoints that move money.
///
/// These assert the HTTP contract rather than the decision — StepUpTests covers the
/// decision — because the contract is what the portal has to act on: a bare 403 and
/// a step-up challenge look identical to a browser, and only one of them is worth
/// retrying.
/// </summary>
public class StepUpEndpointTests : IDisposable
{
    private readonly AuthenticatedFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    private static string FreshNationalId() =>
        "1" + Random.Shared.NextInt64(100_000_000, 999_999_999);

    /// <summary>
    /// Seeds the auction's terms, which normally arrive from auctions.upcoming.
    ///
    /// Written straight to the table rather than published as an event and waited
    /// for: the catalogue consumer is covered elsewhere, and a test that races a
    /// background consumer to set up its fixture is a test that fails for reasons
    /// unconnected to what it is checking.
    /// </summary>
    private async Task SeedAuctionAsync(Guid auctionId)
    {
        var factory = _factory.Services
            .GetRequiredService<IDbContextFactory<ParticipantDbContext>>();

        await using var db = await factory.CreateDbContextAsync();
        if (await db.AuctionTerms.FindAsync(auctionId) is not null) return;

        db.AuctionTerms.Add(new AuctionTerms(
            auctionId,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            depositMinorUnits: 100_000_00,
            bookletPriceMinorUnits: 1_000_00));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Carries a bidder to the point where only the deposit is left.
    ///
    /// The booklet is settled straight on the aggregate rather than driven through
    /// its endpoint, because that endpoint now only *asks* the payment service for
    /// the fee: the status does not move until a settlement comes back on
    /// <c>payments.settlements</c>, and no payment service runs in this fixture.
    /// PaymentLoopTests covers that loop end to end; this file is about which token
    /// opens which door.
    /// </summary>
    private async Task<(Guid Bidder, Guid Auction)> AwaitingDepositAsync()
    {
        var bidder = Guid.NewGuid();
        var auction = Guid.NewGuid();
        await SeedAuctionAsync(auction);

        var client = _factory.CreateClient().WithToken(
            TestJwt.FromNafath(bidder, FreshNationalId(), "سارة", "Sara", Roles.Bidder));

        await client.PostAsync("/bidders/register", null);
        await client.PostAsJsonAsync($"/bidders/{bidder}/profile",
            new { phone = "+966500000001", email = "sara@example.sa" });
        await client.PostAsJsonAsync($"/auctions/{auction}/subscriptions", new { bidderId = bidder });

        var factory = _factory.Services
            .GetRequiredService<IDbContextFactory<ParticipantDbContext>>();

        await using var db = await factory.CreateDbContextAsync();
        var subscription = await db.Subscriptions
            .FirstAsync(x => x.AuctionId == auction && x.BidderId == bidder);
        var terms = await db.AuctionTerms.FirstAsync(t => t.AuctionId == auction);
        var now = DateTimeOffset.UtcNow;

        subscription.RequestBooklet(terms, now);
        subscription.ConfirmBookletPayment("SIM-BOO-SEED", now);
        subscription.AcceptTerms(now);
        subscription.ChooseDeposit(DepositMethod.Payment, terms, now);
        await db.SaveChangesAsync();

        return (bidder, auction);
    }

    [Fact]
    public async Task Registering_without_a_second_factor_is_refused()
    {
        // KYC binds a national identity to an account permanently, and every later
        // act rests on that binding. An ordinary sign-in is not enough.
        var response = await _factory.CreateClient()
            .WithToken(TestJwt.FromNafathWithoutStepUp(
                Guid.NewGuid(), FreshNationalId(), "سارة", "Sara", Roles.Bidder))
            .PostAsync("/bidders/register", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("StepUpRequired", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_challenge_says_what_to_ask_the_identity_provider_for()
    {
        // So the portal does not hard-code a level the realm can renumber beneath
        // it. Without this the portal would guess, and a realm change would turn
        // every payment into a dead end.
        var response = await _factory.CreateClient()
            .WithToken(TestJwt.FromNafathWithoutStepUp(
                Guid.NewGuid(), FreshNationalId(), "سارة", "Sara", Roles.Bidder))
            .PostAsync("/bidders/register", null);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var acr = body.GetProperty("requiredAcr").EnumerateArray()
            .Select(x => x.GetString()).ToArray();
        Assert.Contains("high", acr);
        Assert.True(body.GetProperty("maxAgeSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Paying_the_deposit_without_a_second_factor_is_refused()
    {
        var (bidder, auction) = await AwaitingDepositAsync();

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.For(bidder, Roles.Bidder))
            .PostAsync($"/auctions/{auction}/subscriptions/{bidder}/deposit", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("StepUpRequired", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_stale_second_factor_is_refused_and_says_so()
    {
        // The distinction that matters to a user: "confirm again" rather than "you
        // are not allowed". A token still inside its own lifetime, carrying a
        // genuine high acr from an hour ago.
        var (bidder, auction) = await AwaitingDepositAsync();

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.StepUpExpired(bidder, Roles.Bidder))
            .PostAsync($"/auctions/{auction}/subscriptions/{bidder}/deposit", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("StepUpStale", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Paying_the_deposit_with_a_fresh_second_factor_succeeds()
    {
        // The gate has to open, or it is just an outage.
        var (bidder, auction) = await AwaitingDepositAsync();

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.SteppedUp(bidder, Roles.Bidder))
            .PostAsync($"/auctions/{auction}/subscriptions/{bidder}/deposit", null);

        // 202, and still awaiting: the bidder is eligible when the gateway settles,
        // not when they asked. A 200 here would be the service claiming a payment
        // it has not taken.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingDeposit", body.GetProperty("status").GetString());

        // What the open gate actually produced: a request for the auction's deposit
        // amount, in the outbox, bound for the payment service. Without this the
        // test would pass on an endpoint that returned 202 and did nothing.
        var factory = _factory.Services
            .GetRequiredService<IDbContextFactory<ParticipantDbContext>>();

        await using var db = await factory.CreateDbContextAsync();
        var requested = await db.Outbox
            .Where(o => o.Type == nameof(DepositRequested)
                        && o.AggregateId == $"{auction}:{bidder}")
            .ToListAsync();

        Assert.Single(requested);
        Assert.Contains("10000000", requested[0].Payload);
    }

    [Fact]
    public async Task A_second_factor_does_not_substitute_for_the_role()
    {
        // The policies compose; they do not replace. A stepped-up token from
        // someone who is not a bidder is still not a bidder.
        var (bidder, auction) = await AwaitingDepositAsync();

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.SteppedUp(bidder, Roles.Operator))
            .PostAsync($"/auctions/{auction}/subscriptions/{bidder}/deposit", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // And it is NOT reported as a step-up problem: retrying the confirmation
        // would be pointless, and telling the user to try would be a lie.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("StepUp", body);
    }

    [Fact]
    public async Task A_second_factor_does_not_let_one_bidder_act_for_another()
    {
        // Ownership is checked before anything else. A step-up proves who you are,
        // not that you may spend someone else's deposit.
        var (bidder, auction) = await AwaitingDepositAsync();

        var response = await _factory.CreateClient()
            .WithToken(TestJwt.SteppedUp(Guid.NewGuid(), Roles.Bidder))
            .PostAsync($"/auctions/{auction}/subscriptions/{bidder}/deposit", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_clicks_that_cost_nothing_do_not_require_a_second_factor()
    {
        // Subscribing, accepting the terms and picking a deposit method commit
        // nothing irreversible, and asking for a confirmation at every click trains
        // people to approve without reading. The gate is where money moves — which
        // now includes the booklet fee, asserted below.
        var bidder = Guid.NewGuid();
        var auction = Guid.NewGuid();
        await SeedAuctionAsync(auction);

        var steppedUp = _factory.CreateClient().WithToken(
            TestJwt.FromNafath(bidder, FreshNationalId(), "سارة", "Sara", Roles.Bidder));
        await steppedUp.PostAsync("/bidders/register", null);
        await steppedUp.PostAsJsonAsync($"/bidders/{bidder}/profile",
            new { phone = "+966500000001", email = "sara@example.sa" });

        // From here on, an ordinary token.
        var ordinary = _factory.CreateClient().WithToken(TestJwt.For(bidder, Roles.Bidder));
        var sub = $"/auctions/{auction}/subscriptions/{bidder}";

        Assert.Equal(HttpStatusCode.Created,
            (await ordinary.PostAsJsonAsync(
                $"/auctions/{auction}/subscriptions", new { bidderId = bidder })).StatusCode);
        // The booklet fee is money, however little, so it is behind the gate.
        var refused = await ordinary.PostAsync($"{sub}/booklet", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("StepUpRequired", await refused.Content.ReadAsStringAsync());

        // Settled out of band, as AwaitingDepositAsync explains.
        var factory = _factory.Services
            .GetRequiredService<IDbContextFactory<ParticipantDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var subscription = await db.Subscriptions
                .FirstAsync(x => x.AuctionId == auction && x.BidderId == bidder);
            var terms = await db.AuctionTerms.FirstAsync(t => t.AuctionId == auction);
            subscription.RequestBooklet(terms, DateTimeOffset.UtcNow);
            subscription.ConfirmBookletPayment("SIM-BOO-SEED", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK,
            (await ordinary.PostAsync($"{sub}/terms", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ordinary.PostAsJsonAsync(
                $"{sub}/deposit-method", new { method = "Payment" })).StatusCode);
    }
}
