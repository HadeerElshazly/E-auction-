using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EAuction.Security;

/// <summary>
/// Cross-origin access for the two portals.
///
/// Defaults to **no origins**, so a service that is never called from a browser
/// needs no configuration and allows nothing. There is no wildcard path: the bid
/// catcher and the participant service accept bearer tokens, and a wildcard origin
/// on a token-authenticated API lets any page on the internet spend a signed-in
/// bidder's session.
/// </summary>
public static class CorsSetup
{
    public const string PolicyName = "eauction-portals";

    public static IServiceCollection AddEAuctionCors(
        this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? (configuration["Cors:AllowedOrigins"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (origins.Length == 0)
            {
                // Nothing allowed. Browsers then block the call, which is the right
                // outcome for a misconfigured deployment: a failed portal is visible,
                // a silently wide-open API is not.
                return;
            }

            policy.WithOrigins(origins)
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")

                // Enumerated rather than AllowAnyHeader: Authorization and
                // Content-Type are what the portals send, and the preflight is the
                // last place to notice something else trying.
                .WithHeaders("Authorization", "Content-Type")

                // Location for the bid receipt; Content-Disposition so a download
                // fetched with a token (booklets, reports) keeps its real file name
                // instead of falling back to one without an extension.
                .WithExposedHeaders("Location", "Content-Disposition")

                // Cache the preflight. Without it a browser sends an OPTIONS before
                // every single bid, which doubles the request count on the hot path.
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10));

            // Deliberately no AllowCredentials: the portals authenticate with a
            // bearer token in a header, not a cookie. Allowing credentials would
            // make the API reachable by a cross-site request carrying the user's
            // cookies, which is the CSRF shape bearer tokens exist to avoid.
        }));
    }

    public static IApplicationBuilder UseEAuctionCors(this IApplicationBuilder app) =>
        app.UseCors(PolicyName);
}
