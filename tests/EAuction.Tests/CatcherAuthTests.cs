using System.Net;
using EAuction.BidCatcher;
using EAuction.Core;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// Until this existed every endpoint was open, and the bid path accepted a
/// frame from anyone who could form one.
/// </summary>
public class CatcherAuthTests(AuthenticatedFactory<Program> factory)
    : IClassFixture<AuthenticatedFactory<Program>>
{
    private static HttpContent Body(byte[] frame)
    {
        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    /// <summary>An auction with one eligible bidder, ready to be bid on.</summary>
    private (Guid AuctionId, Guid Bidder, byte[] Frame) Ready()
    {
        var state = factory.Services.GetRequiredService<CatcherState>();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        var bidder = Guid.NewGuid();

        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);

        return (auction.AuctionId, bidder,
            TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now));
    }

    [Fact]
    public async Task A_bid_with_no_token_is_refused()
    {
        var (_, _, frame) = Ready();
        var response = await factory.CreateClient().Anonymous().PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (_, bidder, frame) = Ready();
        var client = factory.CreateClient().WithToken(TestJwt.Expired(bidder, Roles.Bidder));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/bids", Body(frame))).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_the_wrong_key_is_refused()
    {
        var (_, bidder, frame) = Ready();
        var client = factory.CreateClient()
            .WithToken(TestJwt.SignedWithTheWrongKey(bidder, Roles.Bidder));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/bids", Body(frame))).StatusCode);
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_bidder_role_is_refused()
    {
        // Staff are authenticated too. Being known is not being entitled.
        var (_, bidder, frame) = Ready();
        var client = factory.CreateClient().As(bidder, Roles.AuctionAdmin, Roles.AwardCommittee);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/bids", Body(frame))).StatusCode);
    }

    [Fact]
    public async Task A_bidder_cannot_submit_a_frame_naming_someone_else()
    {
        // The signature alone would not catch this: an eligible bidder's own
        // key signs whatever frame they choose to build, including one that
        // spends another bidder's deposit. The token is what ties the request
        // to a person.
        var state = factory.Services.GetRequiredService<CatcherState>();
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));

        var sara = Guid.NewGuid();
        var khalid = Guid.NewGuid();
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, sara, TestAuction.Secret);
        state.GrantEligibilityWithSecret(auction.AuctionId, khalid, TestAuction.Secret);

        // Khalid's token, a frame in Sara's name.
        var frame = TestAuction.Frame(auction.AuctionId, sara, 1_200_000_00, now);
        var client = factory.CreateClient().As(khalid, Roles.Bidder);

        var response = await client.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("BidderMismatch", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_matching_bidder_is_accepted()
    {
        var (_, bidder, frame) = Ready();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);

        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsync("/bids", Body(frame))).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_stay_open_so_the_platform_can_probe_them()
    {
        var client = factory.CreateClient().Anonymous();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        // Ready may be 200 or 503 depending on warm-up, but never 401.
        var ready = await client.GetAsync("/health/ready");
        Assert.NotEqual(HttpStatusCode.Unauthorized, ready.StatusCode);
    }

    [Fact]
    public async Task Keycloak_nests_realm_roles_and_the_handler_reads_them()
    {
        // realm_access.roles is where Keycloak actually puts them, and no
        // standard handler reads it. If this regressed, every role policy
        // would silently deny and the services would look broken rather than
        // insecure — but a flat-claim test would never notice.
        var (_, bidder, frame) = Ready();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);

        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsync("/bids", Body(frame))).StatusCode);
    }
}
