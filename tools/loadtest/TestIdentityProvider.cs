using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace EAuction.LoadTest;

/// <summary>
/// A minimal RS256 issuer: serves an OIDC discovery document and a JWKS, and
/// mints tokens the catcher will accept.
///
/// This is not a stand-in for Keycloak — it exists to measure the one thing
/// Keycloak is not involved in. The catcher validates every token offline
/// against cached signing keys and never calls the issuer per request, so the
/// per-request cost is signature verification and nothing else. Whether the
/// JWKS came from Keycloak or from here makes no difference to that number.
///
/// RS256 specifically, because that is what Keycloak issues. The test suite's
/// HS256 tokens verify in about a microsecond; RS256 is roughly fifty times
/// that, and measuring the cheap one would flatter the result.
/// </summary>
public sealed class TestIdentityProvider : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly HttpListener _listener = new();
    private readonly RsaSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly CancellationTokenSource _cts = new();

    public string Issuer => _issuer;

    public TestIdentityProvider(int port = 8099, string audience = "eauction")
    {
        _issuer = $"http://127.0.0.1:{port}";
        _audience = audience;
        _key = new RsaSecurityKey(_rsa) { KeyId = "loadtest-key-1" };

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string TokenFor(Guid subject, params string[] realmRoles)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            // Keycloak nests realm roles, and the services read them from
            // there. Flattening would not exercise the same path.
            new("realm_access",
                JsonSerializer.Serialize(new { roles = realmRoles }),
                JsonClaimValueTypes.Json)
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task ServeAsync()
    {
        var parameters = _rsa.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = _key.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        });

        var discovery = JsonSerializer.Serialize(new
        {
            issuer = _issuer,
            jwks_uri = $"{_issuer}/jwks",
            id_token_signing_alg_values_supported = new[] { "RS256" }
        });

        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            var body = context.Request.Url?.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => discovery,
                "/jwks" => jwks,
                _ => null
            };

            context.Response.StatusCode = body is null ? 404 : 200;
            context.Response.ContentType = "application/json";

            if (body is not null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
            }

            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        _rsa.Dispose();
        _cts.Dispose();
    }
}
