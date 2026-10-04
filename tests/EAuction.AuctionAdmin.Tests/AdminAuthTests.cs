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
        // Not found rather than forbidden: the role passed and the auction
        // simply does not exist.
        var response = await As(Roles.AwardCommittee)
            .PostAsJsonAsync($"/auctions/{Guid.NewGuid()}/award", new { committeeUserId = Someone });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_stay_open()
    {
        var response = await factory.CreateClient().Anonymous().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
