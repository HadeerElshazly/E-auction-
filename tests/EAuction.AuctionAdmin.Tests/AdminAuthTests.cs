using System.Net;
using System.Net.Http.Json;
using EAuction.Security;
using EAuction.TestSupport;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// Role separation is the point here, not just authentication.
///
/// Slide 6 shows مدير النظام and ممثل لجنة الترسية as different actors.
/// Whoever sets an auction's terms must not also be the one who decides who
/// won it, and an award is a legal act rather than an administrative one.
/// </summary>
public class AdminAuthTests : IDisposable
{
    private static readonly Guid Someone = Guid.NewGuid();

    // Held privately rather than injected as a class fixture: Program is
    // internal, so a public test class cannot take AuthenticatedFactory<Program>
    // as a constructor parameter.
    private readonly AuthenticatedFactory<Program> factory = new();

    public void Dispose() => factory.Dispose();

    private HttpClient As(params string[] roles) =>
        factory.CreateClient().As(Someone, roles);

    [Fact]
    public async Task Creating_an_auction_needs_a_token()
    {
        var response = await factory.CreateClient().Anonymous()
            .PostAsJsonAsync("/auctions", new { createdByUserId = Someone, nameAr = "م", nameEn = "A" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_bidder_cannot_touch_the_admin_surface()
    {
        var response = await As(Roles.Bidder)
            .PostAsJsonAsync("/auctions", new { createdByUserId = Someone, nameAr = "م", nameEn = "A" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_prepares_auctions()
    {
        var response = await As(Roles.AuctionAdmin)
            .PostAsJsonAsync("/auctions", new { createdByUserId = Someone, nameAr = "م", nameEn = "A" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_confirm_an_award()
    {
        // The separation that matters. Preparing an auction and awarding it
        // are different jobs held by different people, and holding one must
        // not grant the other.
        var auctionId = Guid.NewGuid();

        foreach (var path in new[]
        {
            $"/auctions/{auctionId}/award",
            $"/auctions/{auctionId}/award/disqualify",
            $"/auctions/{auctionId}/candidate",
            $"/auctions/{auctionId}/settle",
            $"/auctions/{auctionId}/unsold"
        })
        {
            var response = await As(Roles.AuctionAdmin).PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_committee_cannot_edit_the_auction_it_is_awarding()
    {
        // The other direction, and the reason it matters: a committee member
        // able to change an auction's terms could change them to fit the
        // outcome they intend.
        var auctionId = Guid.NewGuid();

        var created = await As(Roles.AwardCommittee)
            .PostAsJsonAsync("/auctions", new { createdByUserId = Someone, nameAr = "م", nameEn = "A" });
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);

        var plots = await As(Roles.AwardCommittee)
            .PostAsJsonAsync($"/auctions/{auctionId}/plots",
                new { deedNumber = "X", areaSqm = 1.0m });
        Assert.Equal(HttpStatusCode.Forbidden, plots.StatusCode);
    }

    [Fact]
    public async Task The_committee_reaches_the_award_surface()
    {
        // Not found rather than forbidden: the role and the second factor both
        // passed, and the auction simply does not exist.
        var response = await factory.CreateClient()
            .WithToken(TestJwt.SteppedUp(Someone, Roles.AwardCommittee))
            .PostAsJsonAsync($"/auctions/{Guid.NewGuid()}/award", new { committeeUserId = Someone });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Confirming_an_award_needs_a_second_factor()
    {
        // The most consequential act in the platform: it transfers a parcel of
        // state land to a named person. The committee role alone is not enough —
        // the second factor is what ties the decision to the person, which is what
        // the minutes of an award have to be able to claim.
        var response = await As(Roles.AwardCommittee)
            .PostAsJsonAsync($"/auctions/{Guid.NewGuid()}/award", new { committeeUserId = Someone });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("StepUpRequired", body);
    }

    [Fact]
    public async Task A_stale_second_factor_does_not_confirm_an_award()
    {
        var response = await factory.CreateClient()
            .WithToken(TestJwt.StepUpExpired(Someone, Roles.AwardCommittee))
            .PostAsJsonAsync($"/auctions/{Guid.NewGuid()}/award", new { committeeUserId = Someone });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("StepUpStale", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_second_factor_does_not_substitute_for_the_committee_role()
    {
        // An auction administrator with a fresh second factor is still not the
        // committee. Separation of duties survives a step-up.
        var response = await factory.CreateClient()
            .WithToken(TestJwt.SteppedUp(Someone, Roles.AuctionAdmin))
            .PostAsJsonAsync($"/auctions/{Guid.NewGuid()}/award", new { committeeUserId = Someone });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("StepUp", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_rest_of_the_award_workflow_does_not_require_a_second_factor()
    {
        // Issuing the letter, uploading the signed copy and notifying the winner
        // all carry out a decision already taken under a confirmed identity.
        // Re-confirming at every step trains people to approve without reading.
        foreach (var path in new[] { "/award/letter", "/award/signed-letter", "/award/notify" })
        {
            var response = await As(Roles.AwardCommittee)
                .PostAsJsonAsync($"/auctions/{Guid.NewGuid()}{path}",
                    new { documentId = Guid.NewGuid() });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Health_endpoints_stay_open()
    {
        var response = await factory.CreateClient().Anonymous().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
