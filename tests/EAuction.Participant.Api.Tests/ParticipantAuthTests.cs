using System.Net;
using System.Net.Http.Json;
using EAuction.Security;
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
