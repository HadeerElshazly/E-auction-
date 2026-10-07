using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace EAuction.Security;

public sealed record JwtOptions
{
    /// <summary>Keycloak realm URL, e.g. https://id.example.sa/realms/eauction.</summary>
    public string? Authority { get; init; }

    /// <summary>
    /// A further <c>iss</c> to accept, where it differs from <see cref="Authority"/>.
    ///
    /// In Compose the services reach Keycloak as keycloak:8080 but the browser logs in
    /// at localhost:8080, and Keycloak stamps the host it was called on. Additive
    /// rather than a replacement: both forms are accepted, because tokens for the same
    /// realm legitimately arrive carrying either. Leave it unset and only
    /// <see cref="Authority"/> is accepted.
    /// </summary>
    public string? Issuer { get; init; }

    public string Audience { get; init; } = "eauction";
    public bool RequireHttpsMetadata { get; init; } = true;
}

public static class JwtSetup
{
    /// <summary>
    /// Validates tokens offline against the issuer's cached signing keys.
    ///
    /// The keys are fetched once and refreshed on a schedule, so no request —
    /// least of all a bid — ever waits on the identity provider (D-19). An
    /// introspection call per bid would put Keycloak on the hot path and make
    /// its availability the auction's availability.
    /// </summary>
    public static IServiceCollection AddEAuctionJwt(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = new JwtOptions
        {
            Authority = configuration["Jwt:Authority"],
            Issuer = configuration["Jwt:Issuer"],
            Audience = configuration["Jwt:Audience"] ?? "eauction",
            RequireHttpsMetadata = configuration.GetValue("Jwt:RequireHttpsMetadata", true)
        };

        if (string.IsNullOrWhiteSpace(options.Authority) && environment.IsProduction())
            throw new InvalidOperationException(
                "Jwt:Authority is required. Without it no token can be validated and every "
                + "endpoint would be open.");

        services.AddSingleton(options);
        services.AddSingleton<ValidatedTokenCache>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    // Both forms, not one, and not `??`.
                    //
                    // Not one, because in Compose the services reach Keycloak as
                    // keycloak:8080 while the browser logs in at localhost:8080, and
                    // Keycloak stamps `iss` with the host it was called on. Tokens
                    // legitimately arrive carrying either, so a single ValidIssuer
                    // rejects whichever one it happens not to be — browser tokens if
                    // it names the internal host, service-to-service tokens if it
                    // names the external one.
                    //
                    // Not `??`, because configuration returns "" for a key that is
                    // present and empty: `Jwt__Issuer:` with nothing after it in YAML,
                    // or a Helm value that resolved to nothing. Null-coalescing lets
                    // that empty string win, and Microsoft.IdentityModel then refuses
                    // every token in the service with IDX10204 — an error that names
                    // neither the setting nor the service that was misconfigured.
                    ValidIssuers = new[] { options.Issuer, options.Authority }
                        .Where(i => !string.IsNullOrWhiteSpace(i))
                        .Distinct()
                        .ToArray(),
                    ValidAudience = options.Audience,
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role,
                    // Tokens are short-lived; a generous skew would widen the
                    // window in which a revoked session still works.
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                jwt.Events = new JwtBearerEvents
                {
                    // An RS256 signature costs far more than the rest of a bid
                    // request put together, and a bidder sends many bids under
                    // one token. Verifying it once per token instead of once
                    // per request is the difference between meeting the
                    // latency budget and missing it by a wide margin.
                    OnMessageReceived = context =>
                    {
                        var token = ReadBearer(context.Request.Headers.Authorization);
                        if (token is null) return Task.CompletedTask;

                        var cache = context.HttpContext.RequestServices
                            .GetRequiredService<ValidatedTokenCache>();

                        if (cache.TryGet(token, DateTimeOffset.UtcNow, out var principal)
                            && principal is not null)
                        {
                            context.Principal = principal;
                            context.Success();
                        }

                        return Task.CompletedTask;
                    },

                    // Keycloak nests realm roles under realm_access.roles, which
                    // no standard handler reads. Without this every role policy
                    // would deny, and every [Authorize(Role)] would be dead.
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.Identity is ClaimsIdentity identity)
                            KeycloakRoles.Project(identity);

                        var token = ReadBearer(context.Request.Headers.Authorization);
                        if (token is not null && context.Principal is not null
                            && context.SecurityToken.ValidTo != default)
                        {
                            context.HttpContext.RequestServices
                                .GetRequiredService<ValidatedTokenCache>()
                                .Set(token, context.Principal,
                                    new DateTimeOffset(context.SecurityToken.ValidTo, TimeSpan.Zero),
                                    DateTimeOffset.UtcNow);
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(auth =>
        {
            auth.AddPolicy(Policies.Bidder, p => p.RequireRole(Roles.Bidder));
            auth.AddPolicy(Policies.AuctionAdmin, p => p.RequireRole(Roles.AuctionAdmin));
            auth.AddPolicy(Policies.AwardCommittee, p => p.RequireRole(Roles.AwardCommittee));
            auth.AddPolicy(Policies.Operator, p => p.RequireRole(Roles.Operator));

            // No step-up, and no other role folded in. Reading the audit trail
            // changes nothing, and an auditor who had to be an administrator to
            // reach it would be reading their own record.
            auth.AddPolicy(Policies.Auditor, p => p.RequireRole(Roles.Auditor));
            auth.AddPolicy(Policies.Inquiries, p => p.RequireRole(Roles.Inquiries));

            // Three roles, and the point of the first is that it is the only one of
            // the three that cannot change an auction.
            auth.AddPolicy(Policies.Reporting,
                p => p.RequireRole(Roles.Reporting, Roles.AuctionAdmin, Roles.AwardCommittee));

            // Either role reaches the catcher; the catcher then decides which one
            // this auction's channel actually permits.
            auth.AddPolicy(Policies.SubmitsBids,
                p => p.RequireRole(Roles.Bidder, Roles.Operator));

            auth.AddPolicy(Policies.StaffOnTheFloor,
                p => p.RequireRole(Roles.Operator, Roles.AuctionAdmin, Roles.AwardCommittee));

            // Nothing is reachable without a token unless it opts out.
            auth.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    private static string? ReadBearer(Microsoft.Extensions.Primitives.StringValues header)
    {
        var value = header.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : null;
    }
}

public static class KeycloakRoles
{
    /// <summary>
    /// Copies Keycloak's <c>realm_access.roles</c> into standard role claims.
    /// </summary>
    public static void Project(ClaimsIdentity identity)
    {
        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess)) return;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(realmAccess);
            if (!document.RootElement.TryGetProperty("roles", out var roles)) return;

            foreach (var role in roles.EnumerateArray())
            {
                var name = role.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    identity.AddClaim(new Claim(ClaimTypes.Role, name));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed claim means no roles, which means every role policy
            // denies. Failing closed is the only safe reading.
        }
    }
}

public static class PrincipalExtensions
{
    /// <summary>
    /// The caller's identity, which is also their bidder id.
    ///
    /// <c>Bidder.Id</c> is the Keycloak subject rather than a separate
    /// identifier, so no token mapper, no custom claim and no lookup stands
    /// between a request and knowing who made it. The bid catcher in
    /// particular cannot afford a lookup, and a service that cannot check
    /// ownership cheaply tends to stop checking it.
    /// </summary>
    public static Guid? SubjectId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public static bool IsInAnyRole(this ClaimsPrincipal principal, params string[] roles) =>
        roles.Any(principal.IsInRole);
}
