using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
        SigningCredentials credentials)
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

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
