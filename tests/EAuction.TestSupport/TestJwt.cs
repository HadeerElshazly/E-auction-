using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EAuction.TestSupport;

/// <summary>
/// Mints tokens the services will accept, so authorization can be tested
/// without standing up Keycloak.
///
/// The shape matters as much as the signature: realm roles are nested under
/// <c>realm_access.roles</c> exactly as Keycloak emits them, because that
/// nesting is the thing a handler gets wrong, and a test using flat role
/// claims would prove nothing about the real token.
/// </summary>
public static class TestJwt
{
    public const string Issuer = "https://id.test/realms/eauction";
    public const string Audience = "eauction";

    private static readonly SymmetricSecurityKey Key =
        new(Encoding.UTF8.GetBytes("test-signing-key-not-for-any-real-environment-0123456789"));

    public static TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = Key,
        NameClaimType = "sub",
        RoleClaimType = ClaimTypes.Role,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    public static string For(Guid subject, params string[] realmRoles) =>
        Build(subject, realmRoles, DateTime.UtcNow.AddMinutes(15));

    /// <summary>
    /// A token shaped like one Keycloak issues after a Nafath login: it carries the
    /// identity claims the <c>nafath-identity</c> mappers produce. Registration
    /// accepts nothing else, so a test that registers a bidder needs this and not
    /// <see cref="For"/>.
    /// </summary>
    /// <remarks>
    /// Includes a second factor confirmed just now, because registration is one of
    /// the endpoints that requires one. <see cref="FromNafathWithoutStepUp"/> is the
    /// ordinary-sign-in version, for asserting that the gate holds.
    /// </remarks>
    public static string FromNafath(
        Guid subject, string nationalId, string nameAr, string nameEn,
        params string[] realmRoles) =>
        Nafath(subject, nationalId, nameAr, nameEn, steppedUp: true, realmRoles);

    /// <summary>A Nafath login with no second factor — acr "low".</summary>
    public static string FromNafathWithoutStepUp(
        Guid subject, string nationalId, string nameAr, string nameEn,
        params string[] realmRoles) =>
        Nafath(subject, nationalId, nameAr, nameEn, steppedUp: false, realmRoles);

    private static string Nafath(
        Guid subject, string nationalId, string nameAr, string nameEn,
        bool steppedUp, string[] realmRoles) =>
        Write(subject, realmRoles, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow,
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
            StepUpClaims(steppedUp, DateTimeOffset.UtcNow)
                .Append(new Claim("national_id", nationalId))
                .Append(new Claim("name_ar", nameAr))
                .Append(new Claim("name", nameEn))
                .ToArray());

    /// <summary>An ordinary token plus a second factor confirmed just now.</summary>
    public static string SteppedUp(Guid subject, params string[] realmRoles) =>
        Write(subject, realmRoles, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow,
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
            StepUpClaims(true, DateTimeOffset.UtcNow).ToArray());

    /// <summary>
    /// A second factor that happened, but too long ago to authorise anything now.
    /// </summary>
    public static string StepUpExpired(Guid subject, params string[] realmRoles) =>
        Write(subject, realmRoles, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow,
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
            StepUpClaims(true, DateTimeOffset.UtcNow.AddHours(-1)).ToArray());

    /// <summary>
    /// The claims Keycloak's step-up produces: the level reached, and when.
    /// "low"/"high" are the names from the realm's acr.loa.map, which is what
    /// Keycloak emits — not the numbers behind them.
    /// </summary>
    private static IEnumerable<Claim> StepUpClaims(bool steppedUp, DateTimeOffset authTime) =>
    [
        new Claim("acr", steppedUp ? "high" : "low"),
        new Claim("auth_time", authTime.ToUnixTimeSeconds().ToString()),
    ];

    /// <summary>A token that has already expired, for testing that lifetime is checked.</summary>
    public static string Expired(Guid subject, params string[] realmRoles) =>
        Build(subject, realmRoles, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-10));

    /// <summary>Correct in every way except the signature.</summary>
    public static string SignedWithTheWrongKey(Guid subject, params string[] realmRoles)
    {
        var wrong = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("a-different-key-entirely-9876543210987654321098765"));

        return Write(subject, realmRoles, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow,
            new SigningCredentials(wrong, SecurityAlgorithms.HmacSha256));
    }

    private static string Build(
        Guid subject, string[] realmRoles, DateTime expires, DateTime? notBefore = null) =>
        Write(subject, realmRoles, expires, notBefore ?? DateTime.UtcNow,
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));

    private static string Write(
        Guid subject, string[] realmRoles, DateTime expires, DateTime notBefore,
        SigningCredentials credentials, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            // Keycloak nests realm roles. Flattening them here would hide the
            // one thing most likely to be wrong in the handler.
            new("realm_access",
                JsonSerializer.Serialize(new { roles = realmRoles }),
                JsonClaimValueTypes.Json)
        };
        claims.AddRange(extra);

        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claims,
            notBefore: notBefore, expires: expires, signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>
/// A host whose JWT handler trusts <see cref="TestJwt"/> instead of reaching
/// for an identity provider that is not there.
/// </summary>
public sealed class AuthenticatedFactory<TEntryPoint>
    : WebApplicationFactory<TEntryPoint> where TEntryPoint : class
{
    /// <summary>
    /// Extra service registrations. A property rather than a constructor
    /// argument because xUnit activates class fixtures with no arguments.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// Configuration the host sees, for settings read at startup rather than from
    /// a service. A key the service reads once into a local — a signing key, a
    /// master key — cannot be replaced by registering a different service
    /// afterwards, so a test that wants to know which key was used has to set it
    /// before the host is built.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // UseSetting, not ConfigureAppConfiguration. A minimal-API entry point reads
        // builder.Configuration while its own Program body runs, which is before any
        // source added here would be in place — so a configured value would be seen
        // by services resolved later and missed by everything read at startup, which
        // is exactly the kind of setting worth overriding in a test.
        if (Settings is not null)
            foreach (var (key, value) in Settings)
                builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.Configure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.MetadataAddress = null;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = TestJwt.ValidationParameters();

                    // Supplying configuration up front stops the handler
                    // trying to fetch metadata from an authority that does
                    // not exist.
                    options.Configuration = new OpenIdConnectConfiguration();
                });

            ConfigureServices?.Invoke(services);
        });
    }
}

public static class HttpClientAuthExtensions
{
    public static HttpClient As(this HttpClient client, Guid subject, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", TestJwt.For(subject, roles));
        return client;
    }

    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static HttpClient Anonymous(this HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }
}
