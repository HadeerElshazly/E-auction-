using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EAuction.Security;

/// <summary>
/// A second factor, required at the moments that move money or land.
///
/// Signing in proves who someone is. It does not prove that the person at the
/// keyboard right now is them: a token lives fifteen minutes, a laptop gets left
/// unlocked, a session cookie gets stolen. For reading an auction that is an
/// acceptable risk. For paying a deposit, uploading a bank guarantee, or awarding a
/// parcel of state land it is not, so those endpoints ask the identity provider to
/// confirm the person again, and refuse until it has.
///
/// In Keycloak this is step-up authentication: the client asks for a higher Level of
/// Authentication with <c>acr_values</c>, the realm's conditional flow demands a
/// second factor, and the resulting token carries that level in its <c>acr</c>
/// claim. In production the second factor is Nafath's two-digit confirmation; the
/// realm ships an OTP authenticator in the same slot so the mechanism is testable
/// before the Elm/NIC contract exists.
///
/// Two things here are easy to get wrong and are the reason this is a type rather
/// than a line in each endpoint:
///
/// **The request cannot be trusted.** <c>acr_values</c> is a *voluntary* claims
/// request in OIDC: a provider that cannot satisfy it still issues a token, just at
/// a lower level. So the service checks what the token says, never what the client
/// asked for.
///
/// **A step-up goes stale.** A confirmation from half an hour ago says nothing about
/// who is at the keyboard now, and a token minted from it stays valid for its whole
/// lifetime. So <c>auth_time</c> must be recent as well as the level sufficient —
/// this is the half that implementations usually omit, and without it a single
/// step-up at sign-in would authorise every payment for the rest of the session.
/// </summary>
public sealed record StepUpOptions
{
    /// <summary>
    /// The <c>acr</c> values that count as stepped up, as Keycloak emits them.
    ///
    /// Names rather than numbers because that is what the realm's
    /// <c>acr.loa.map</c> produces, and a name survives the map being renumbered.
    /// </summary>
    public string[] AcceptedAcr { get; init; } = ["high"];

    /// <summary>
    /// How recently the second factor must have happened.
    ///
    /// Five minutes is long enough to fill in a payment form and short enough that a
    /// walk-away does not hand someone else a deposit. It is a trade-off, not a
    /// constant of nature, and a client with a different risk appetite moves it.
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Allowance for clock skew between the identity provider and this service.
    ///
    /// Without it, a provider whose clock is a few seconds ahead produces an
    /// <c>auth_time</c> in the future, which a naive age check reads as negative and
    /// may treat as fresh — or, worse, a provider a few seconds behind makes a
    /// just-completed step-up look expired.
    /// </summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Why a step-up check failed, for the response the caller gets.</summary>
public enum StepUpFailure
{
    None = 0,

    /// <summary>The token carries no sufficient <c>acr</c>. Ask for one.</summary>
    Required,

    /// <summary>The level was reached, but too long ago. Ask again.</summary>
    Stale,
}

public sealed class StepUpRequirement : IAuthorizationRequirement
{
    /// <summary>Set by the handler so the result handler can say which it was.</summary>
    internal const string FailureItemKey = "eauction.stepup.failure";
}

public sealed class StepUpHandler(StepUpOptions options, TimeProvider clock)
    : AuthorizationHandler<StepUpRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        var outcome = Evaluate(context.User, options, clock.GetUtcNow());

        if (outcome == StepUpFailure.None)
        {
            context.Succeed(requirement);
        }
        else
        {
            // Carried on the HttpContext rather than the failure reason, because the
            // result handler needs it to tell the caller whether to ask for a
            // step-up or to ask again.
            if (context.Resource is HttpContext http)
                http.Items[StepUpRequirement.FailureItemKey] = outcome;

            context.Fail(new AuthorizationFailureReason(this, outcome.ToString()));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The decision, separated from the plumbing so it can be tested directly.
    /// </summary>
    public static StepUpFailure Evaluate(
        ClaimsPrincipal user, StepUpOptions options, DateTimeOffset now)
    {
        var acr = user.FindFirst("acr")?.Value;

        if (acr is null || !options.AcceptedAcr.Contains(acr, StringComparer.Ordinal))
            return StepUpFailure.Required;

        // No auth_time means the step-up cannot be dated, so it cannot be trusted
        // to be recent. Refused rather than waved through: Keycloak omits the claim
        // on a direct grant, which is exactly the path that cannot step up at all.
        var authTime = user.FindFirst("auth_time")?.Value;
        if (!long.TryParse(authTime, out var seconds))
            return StepUpFailure.Required;

        var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var age = now - at;

        // A negative age is the provider's clock running ahead. Allowed within the
        // skew window and refused beyond it — an auth_time far in the future would
        // otherwise never expire.
        if (age < -options.ClockSkew) return StepUpFailure.Stale;

        return age <= options.MaxAge + options.ClockSkew
            ? StepUpFailure.None
            : StepUpFailure.Stale;
    }
}

/// <summary>
/// Turns a step-up failure into a response the portal can act on.
///
/// The default for a failed policy is a bare 403, which tells a portal nothing: it
/// cannot distinguish "you are the wrong person" — where retrying is pointless —
/// from "confirm it is you and try again", where retrying is the entire remedy. So
/// a step-up failure answers with a body naming what to ask the identity provider
/// for, and every other failure keeps the default.
/// </summary>
public sealed class StepUpResultHandler(StepUpOptions options) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden
            && context.Items.TryGetValue(StepUpRequirement.FailureItemKey, out var raw)
            && raw is StepUpFailure failure
            && failure != StepUpFailure.None)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                reason = failure == StepUpFailure.Stale ? "StepUpStale" : "StepUpRequired",

                // What to put in acr_values. Sent so the portal does not hard-code a
                // level that the realm can renumber underneath it.
                requiredAcr = options.AcceptedAcr,
                maxAgeSeconds = (int)options.MaxAge.TotalSeconds,
                problems = new[]
                {
                    failure == StepUpFailure.Stale
                        ? "التحقق الثنائي انتهت صلاحيته. أعد التأكيد ثم حاول مرة أخرى."
                        : "هذا الإجراء يتطلب تأكيداً ثنائياً عبر نفاذ."
                }
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, result);
    }
}

public static class StepUpSetup
{
    /// <summary>
    /// Adds the step-up policies. Call after <c>AddEAuctionJwt</c>.
    ///
    /// Each policy composes a role with the second factor rather than replacing it:
    /// a stepped-up token from the wrong person is still the wrong person.
    /// </summary>
    public static IServiceCollection AddEAuctionStepUp(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new StepUpOptions
        {
            AcceptedAcr = configuration.GetSection("StepUp:AcceptedAcr").Get<string[]>()
                ?? ["high"],
            MaxAge = TimeSpan.FromSeconds(
                configuration.GetValue("StepUp:MaxAgeSeconds", 300)),
        };

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthorizationHandler, StepUpHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, StepUpResultHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.BidderStepUp, p =>
            {
                p.RequireRole(Roles.Bidder);
                p.Requirements.Add(new StepUpRequirement());
            })
            .AddPolicy(Policies.AwardCommitteeStepUp, p =>
            {
                p.RequireRole(Roles.AwardCommittee);
                p.Requirements.Add(new StepUpRequirement());
            });

        return services;
    }
}
