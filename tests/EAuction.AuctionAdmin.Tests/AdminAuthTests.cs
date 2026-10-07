using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAuction.AuctionAdmin.Persistence;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
                new { plotNumber = "X", areaSqm = 1.0m });
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

/// <summary>
/// The grant that opens an award letter.
///
/// خطاب الترسية is Restricted in the document service, which means no role opens
/// it — not an administrator's and not the committee's own. This endpoint is the
/// only way in, and it says yes to one person: whoever currently holds the award.
/// </summary>
public class AwardLetterGrantTests : IDisposable
{
    private static readonly byte[] GrantKey = DocumentGrants.NewKey();

    private readonly AuthenticatedFactory<Program> factory = new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["Documents:GrantKeyHex"] = Convert.ToHexString(GrantKey),
        },
    };

    public void Dispose() => factory.Dispose();

    private IDbContextFactory<AdminDbContext> Db() =>
        factory.Services.GetRequiredService<IDbContextFactory<AdminDbContext>>();

    /// <summary>
    /// An auction carried to a notified winner, written straight to the database.
    ///
    /// The award workflow has its own tests; driving it through nine endpoints here
    /// would make this test fail for reasons that have nothing to do with grants.
    /// </summary>
    private async Task<(Guid Auction, Guid Winner, Guid SignedLetter)> SettledAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(now);

        auction.SubmitForReview(now);
        auction.Approve(now);
        auction.MarkScheduled();
        auction.MarkLive();
        auction.MarkClosing();
        auction.MarkPendingEligibilityReview();

        var winner = Guid.NewGuid();
        auction.OfferCandidate(winner, 1_800_000_00);
        auction.ConfirmAward(Build.Committee, now, TimeSpan.FromDays(5));
        auction.GenerateAwardLetter(Guid.NewGuid());

        var signed = Guid.NewGuid();
        auction.UploadSignedAwardLetter(signed);
        auction.NotifyWinner(now);

        await using var db = await Db().CreateDbContextAsync();
        db.Auctions.Add(auction);
        await db.SaveChangesAsync();

        return (auction.Id, winner, signed);
    }

    [Fact]
    public async Task The_winner_gets_a_grant_for_the_signed_letter()
    {
        var (auction, winner, signed) = await SettledAsync();

        var response = await factory.CreateClient()
            .As(winner, Roles.Bidder)
            .GetAsync($"/auctions/{auction}/award/letter-grant");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The signed letter, not the draft: that is the instrument.
        Assert.Equal(signed, body.GetProperty("documentId").GetGuid());

        Assert.True(DocumentGrants.Verify(
            GrantKey, body.GetProperty("grant").GetString(), signed, winner,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_bidder_who_did_not_win_gets_nothing()
    {
        var (auction, _, _) = await SettledAsync();

        var response = await factory.CreateClient()
            .As(Guid.NewGuid(), Roles.Bidder)
            .GetAsync($"/auctions/{auction}/award/letter-grant");

        // 404 rather than 403: whether this auction has an award, and whose it is,
        // is not something to confirm to a bidder who is not its holder.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_committee_cannot_mint_itself_a_grant()
    {
        // Restricted means no role opens it, including the role that uploaded it.
        // A committee member who could mint a grant for themselves would make the
        // classification decorative.
        var (auction, _, _) = await SettledAsync();

        var response = await factory.CreateClient()
            .As(Guid.NewGuid(), Roles.AwardCommittee)
            .GetAsync($"/auctions/{auction}/award/letter-grant");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_auction_with_no_award_yields_nothing()
    {
        var now = DateTimeOffset.UtcNow;
        var auction = Build.ReadyAuction(now);

        await using (var db = await Db().CreateDbContextAsync())
        {
            db.Auctions.Add(auction);
            await db.SaveChangesAsync();
        }

        var response = await factory.CreateClient()
            .As(Guid.NewGuid(), Roles.Bidder)
            .GetAsync($"/auctions/{auction.Id}/award/letter-grant");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
