using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task A_bidder_cannot_drive_another_bidder_s_subscription()
    {
        var client = _factory.CreateClient().As(Khalid, Roles.Bidder);

        foreach (var (path, body) in new (string, object)[]
        {
            (Sub(Sara, "/booklet"), new { paymentRef = "x" }),
            (Sub(Sara, "/terms"), new { }),
            (Sub(Sara, "/deposit-method"), new { method = 0 }),
            (Sub(Sara, "/deposit"), new { paymentRef = "x" }),
            (Sub(Sara, "/guarantee"), new { documentId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddYears(1) })
        })
        {
            var response = await client.PostAsJsonAsync(path, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
