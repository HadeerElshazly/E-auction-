using System.Security.Claims;
using EAuction.Security;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The second-factor decision, which guards paying a deposit, uploading a bank
/// guarantee, registering a national identity, and awarding a parcel of land.
///
/// Every case below is a way the gate could be opened by something that should not
/// open it, or closed to something that should pass. The freshness half gets the
/// most attention because it is the half usually left out: a single step-up at
/// sign-in that authorised every payment for the rest of the session would pass a
/// test suite that only checked the acr claim.
/// </summary>
public class StepUpTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly StepUpOptions Options = new()
    {
        AcceptedAcr = ["high"],
        MaxAge = TimeSpan.FromMinutes(5),
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    private static ClaimsPrincipal Token(string? acr, DateTimeOffset? authTime)
    {
        var claims = new List<Claim>();
        if (acr is not null) claims.Add(new Claim("acr", acr));
        if (authTime is not null)
            claims.Add(new Claim("auth_time", authTime.Value.ToUnixTimeSeconds().ToString()));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static StepUpFailure Check(string? acr, DateTimeOffset? authTime) =>
        StepUpHandler.Evaluate(Token(acr, authTime), Options, Now);

    // --- the level ---------------------------------------------------------

    [Fact]
    public void A_fresh_high_acr_passes()
    {
        Assert.Equal(StepUpFailure.None, Check("high", Now.AddMinutes(-1)));
    }

    [Fact]
    public void An_ordinary_sign_in_is_refused()
    {
        // acr "low" is what Keycloak issues for a password login with no
        // acr_values — the common case, and the one this gate exists to stop.
        Assert.Equal(StepUpFailure.Required, Check("low", Now));
    }

    [Fact]
    public void A_token_with_no_acr_is_refused()
    {
        // A direct grant, or an identity provider that does not implement
        // step-up. Absence must not read as permission.
        Assert.Equal(StepUpFailure.Required, Check(null, Now));
    }

    [Fact]
    public void An_unrecognised_acr_is_refused()
    {
        // Not a substring match and not a numeric comparison: a realm renumbering
        // its map, or a provider inventing a level, must not accidentally satisfy
        // the gate.
        foreach (var acr in new[] { "HIGH", "high2", "highest", "2", "medium", "" })
            Assert.Equal(StepUpFailure.Required, Check(acr, Now));
    }

    // --- the freshness ----------------------------------------------------

    [Fact]
    public void A_high_acr_from_too_long_ago_is_stale()
    {
        // The half that is usually missing. The token is still inside its own
        // lifetime and still says "high" — but the person who confirmed is not
        // necessarily the person at the keyboard ten minutes later.
        Assert.Equal(StepUpFailure.Stale, Check("high", Now.AddMinutes(-10)));
    }

    [Fact]
    public void A_high_acr_with_no_auth_time_is_refused()
    {
        // Keycloak omits auth_time on a direct grant, which is also the path that
        // cannot step up at all. An undateable step-up cannot be shown to be
        // recent, so it is treated as absent rather than as valid.
        Assert.Equal(StepUpFailure.Required, Check("high", authTime: null));
    }

    [Fact]
    public void An_unparseable_auth_time_is_refused()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("acr", "high"), new Claim("auth_time", "yesterday")], "test"));

        Assert.Equal(StepUpFailure.Required, StepUpHandler.Evaluate(principal, Options, Now));
    }

    [Theory]
    // Inside the window.
    [InlineData(0, StepUpFailure.None)]
    [InlineData(-299, StepUpFailure.None)]
    // At the boundary, and just past it but inside the skew allowance.
    [InlineData(-300, StepUpFailure.None)]
    [InlineData(-320, StepUpFailure.None)]
    // Past the window plus the skew.
    [InlineData(-331, StepUpFailure.Stale)]
    [InlineData(-3600, StepUpFailure.Stale)]
    public void The_window_is_the_max_age_plus_the_skew(int offsetSeconds, StepUpFailure expected)
    {
        Assert.Equal(expected, Check("high", Now.AddSeconds(offsetSeconds)));
    }

    [Fact]
    public void A_slightly_future_auth_time_is_allowed()
    {
        // The identity provider's clock running a few seconds ahead. Refusing this
        // would reject a step-up that had just succeeded.
        Assert.Equal(StepUpFailure.None, Check("high", Now.AddSeconds(20)));
    }

    [Fact]
    public void A_far_future_auth_time_is_refused()
    {
        // Otherwise an auth_time far enough ahead never ages out, and a token
        // carrying one would satisfy this gate indefinitely.
        Assert.Equal(StepUpFailure.Stale, Check("high", Now.AddHours(1)));
    }

    // --- the shape of the configuration -----------------------------------

    [Fact]
    public void More_than_one_acr_can_be_accepted()
    {
        // A realm migrating between level names has to be able to accept both for
        // a while, or the migration is an outage.
        var options = Options with { AcceptedAcr = ["high", "nafath-2digit"] };

        Assert.Equal(StepUpFailure.None,
            StepUpHandler.Evaluate(Token("nafath-2digit", Now), options, Now));
        Assert.Equal(StepUpFailure.None,
            StepUpHandler.Evaluate(Token("high", Now), options, Now));
        Assert.Equal(StepUpFailure.Required,
            StepUpHandler.Evaluate(Token("low", Now), options, Now));
    }

    [Fact]
    public void An_empty_accepted_list_refuses_everything()
    {
        // Misconfiguration must fail closed. An empty list meaning "anything goes"
        // would turn a typo in a values file into an open gate.
        var options = Options with { AcceptedAcr = [] };

        Assert.Equal(StepUpFailure.Required,
            StepUpHandler.Evaluate(Token("high", Now), options, Now));
    }
}
