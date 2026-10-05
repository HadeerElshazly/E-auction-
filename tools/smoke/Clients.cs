using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EAuction.Smoke;

/// <summary>A token from the real Keycloak, by password grant.</summary>
public static class Keycloak
{
    public static async Task<string> TokenAsync(
        HttpClient http, string issuer, string clientId, string user, string password)
    {
        var response = await http.PostAsync($"{issuer}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["username"] = user,
                ["password"] = password
            }));

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
