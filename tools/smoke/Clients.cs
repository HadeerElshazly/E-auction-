using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EAuction.Smoke;

/// <summary>A token from the real Keycloak, by password grant.</summary>
public static class Keycloak
{
    /// <summary>
    /// The TOTP secret seeded on the dev users in the realm file.
    ///
    /// A password grant for a user who has a second factor configured must supply
    /// it: Keycloak's built-in direct-grant flow includes a conditional OTP step,
    /// and it triggers on the credential existing rather than on anything the
    /// client asks for. The resulting token is still level 1 — an OTP on a direct
    /// grant satisfies the flow without raising the acr — which is why a stepped-up
    /// token has to come from the browser flow instead.
    /// </summary>
    public const string DevTotpSecret = "eauctiondevsecret1234567890";

    /// <summary>RFC 6238, the six-digit flavour Keycloak expects.</summary>
    public static string Totp(string secret, DateTimeOffset? at = null)
    {
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

        // The counter as eight big-endian bytes, which is what RFC 6238 specifies
        // and what every authenticator agrees on.
        var message = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);

        var mac = System.Security.Cryptography.HMACSHA1.HashData(
            System.Text.Encoding.ASCII.GetBytes(secret), message);

        var offset = mac[^1] & 0x0F;
        var code = ((mac[offset] & 0x7F) << 24)
                   | (mac[offset + 1] << 16)
                   | (mac[offset + 2] << 8)
                   | mac[offset + 3];

        return (code % 1_000_000).ToString("D6");
    }

    public static async Task<string> TokenAsync(
        HttpClient http, string issuer, string clientId, string user, string password,
        string? totpSecret = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = user,
            ["password"] = password
        };

        if (totpSecret is null)
        {
            return await PostAsync(http, issuer, user, form);
        }

        // A TOTP code is single-use: Keycloak remembers the counter it last accepted
        // for a user and refuses a replay. The step-up tokens are minted moments
        // before this runs and consume the current window's code for these same
        // users, so the first attempt here can legitimately fail. Waiting for the
        // next window is the only remedy — and it is the same one a human with an
        // authenticator app has.
        form["otp"] = Totp(totpSecret);

        try
        {
            return await PostAsync(http, issuer, user, form);
        }
        catch (SmokeException) when (totpSecret is not null)
        {
            await Task.Delay(SecondsUntilNextWindow() * 1000 + 1000);
            form["otp"] = Totp(totpSecret);
            return await PostAsync(http, issuer, user, form);
        }
    }

    private static int SecondsUntilNextWindow() =>
        30 - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30);

    private static async Task<string> PostAsync(
        HttpClient http, string issuer, string user, Dictionary<string, string> form)
    {
        var response = await http.PostAsync($"{issuer}/protocol/openid-connect/token",
            new FormUrlEncodedContent(form));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new SmokeException($"Keycloak refused a token for {user}: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>The subject claim IS the bidder id and the signing-key input (D-18).</summary>
    public static Guid Subject(string accessToken)
    {
        var part = accessToken.Split('.')[1];
        var json = Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/')
            .PadRight(part.Length + (4 - part.Length % 4) % 4, '='));
        return Guid.Parse(JsonDocument.Parse(json).RootElement.GetProperty("sub").GetString()!);
    }
}

/// <summary>A service client that carries one person's token and fails loudly.</summary>
public sealed class Caller(HttpClient http, string baseUrl, string token, string who)
{
    public string Who => who;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);

        // An empty token means genuinely anonymous. Sending "Bearer " with nothing
        // after it is not the same request a citizen's browser makes before login,
        // and the public catalogue has to answer that one.
        if (token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    public async Task<JsonElement> PostAsync(string path, object? body = null)
    {
        var request = Request(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await SendAsync(request, path);
    }

    public async Task<JsonElement> PutAsync(string path, object body)
    {
        var request = Request(HttpMethod.Put, path);
        request.Content = JsonContent.Create(body);
        return await SendAsync(request, path);
    }

    public Task<JsonElement> GetAsync(string path) =>
        SendAsync(Request(HttpMethod.Get, path), path);

    /// <summary>For the cases where the status code is the thing being asserted.</summary>
    public async Task<(HttpStatusCode Status, string Body)> TryPostAsync(string path, object? body = null)
    {
        var request = Request(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, string Body)> TryPostBytesAsync(
        string path, byte[] payload)
    {
        var request = Request(HttpMethod.Post, path);
        request.Content = new ByteArrayContent(payload);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string path)
    {
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new SmokeException(
                $"{who} → {request.Method} {path} returned {(int)response.StatusCode}: {Brief(body)}");

        return body.Length == 0
            ? default
            : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// ASP.NET's developer exception page echoes the request headers, bearer token and
    /// all. Printing that to a log, even a local one, is a habit worth not having.
    /// </summary>
    private static string Brief(string body)
    {
        var cut = body.IndexOf("\nHEADERS", StringComparison.Ordinal);
        if (cut > 0) body = body[..cut];
        return body.Length > 600 ? body[..600] + "…" : body;
    }
}

public sealed class SmokeException(string message) : Exception(message);
