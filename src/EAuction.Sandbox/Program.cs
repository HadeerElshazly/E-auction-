using EAuction.Sandbox;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// ---------------------------------------------------------------------------
// What this is, and why it refuses to start by default.
//
// Two external systems the platform depends on do not exist yet: Nafath, which
// says who a bidder is, and a payment gateway, which takes the deposit that makes
// them eligible to bid. Both are blocked on contracts — Elm/NIC for the first
// (P-1) and merchant accounts for the second (P-3) — and in the meantime the
// platform stands in for them. That substitution is honest in a test and invisible
// in a room: the step-up gate on registration asks for a six-digit code only the
// test suite can produce, so the first thing a person tries to do by hand is the
// thing they cannot do.
//
// This service is the stand-in made visible and operable. It is NOT part of the
// platform: it is not in the Helm chart, it holds no data, and it must never be
// deployed anywhere near a real citizen's identity. Hence the flag: the default is
// off and the refusal below says why, so this cannot be switched on by forgetting
// something.
//
// It is gated on an explicit flag rather than on the environment because this
// stack runs every service as Production — ASPNETCORE_ENVIRONMENT is unset in
// Compose so each service's startup guards are armed. An `IsProduction()` test
// would disable the sandbox in exactly the stack it exists for.
// ---------------------------------------------------------------------------
if (!builder.Configuration.GetValue("Sandbox:Enabled", false))
{
    throw new InvalidOperationException(
        "The sandbox is off. Set Sandbox:Enabled=true to run it, and only in an "
        + "environment where no real identity and no real money exist: it prints a "
        + "second factor on a web page and can make a payment gateway refuse.");
}

// The secret the realm seeds on the dev bidders. Public by design — it is in
// `deploy/keycloak/eauction-realm.json`, which is in git — and this service may
// never hold any other kind.
var secret = builder.Configuration["Sandbox:TotpSecret"]
    ?? "eauctiondevsecret1234567890";

// Who to show codes for. Configurable so a stack with different seeded users does
// not need a rebuild, and so the list cannot silently include somebody real: the
// names here produce a code only if the realm seeded the same secret on them.
var people = builder.Configuration.GetSection("Sandbox:People").Get<Person[]>()
    ?? [
        new Person("sara", "سارة الحربي", "1012345678"),
        new Person("khalid", "خالد العتيبي", "1087654321"),
    ];

var paymentsBaseUrl = builder.Configuration["Sandbox:PaymentsBaseUrl"];

builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok("ok"));

// Ready as soon as it is listening, and deliberately not conditional on the payment
// service answering. The half of this console that unblocks a person — the Nafath
// codes — needs nothing but this process, and tying readiness to the other half
// would make the stack refuse to come up for want of a demonstration aid. The
// payment panel reports its own unavailability on screen instead.
app.MapGet("/health/ready", () => Results.Ok("ok"));

// --- نفاذ ------------------------------------------------------------------

app.MapGet("/api/nafath", () =>
{
    var now = DateTimeOffset.UtcNow;

    return Results.Ok(new
    {
        at = now,
        people = people.Select(p =>
        {
            var (code, remaining) = Totp.At(secret, now);
            return new
            {
                p.Username,
                p.NameAr,
                p.NationalId,
                code,
                secondsRemaining = remaining,
            };
        }),
    });
});

// --- بوابة الدفع -----------------------------------------------------------
//
// Proxied rather than called from the page. The payment service configures no CORS
// — correctly, it is not a browser-facing service — so a fetch from this origin
// would be refused. Going through here also means the console works without the
// payment service being published to the host at all.

app.MapGet("/api/payments", async (IHttpClientFactory factory, CancellationToken ct) =>
{
    if (paymentsBaseUrl is null) return Unconfigured();

    try
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var response = await client.GetAsync(
            $"{paymentsBaseUrl.TrimEnd('/')}/sandbox/instructions", ct);

        return await Relay(response, ct);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return Unreachable(e);
    }
});

app.MapPost("/api/payments/decline", async (
    DeclineRequest body, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (paymentsBaseUrl is null) return Unconfigured();

    try
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var response = await client.PostAsJsonAsync(
            $"{paymentsBaseUrl.TrimEnd('/')}/sandbox/decline", body, ct);

        return await Relay(response, ct);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return Unreachable(e);
    }
});

await app.RunAsync();

// The payment panel is the half of this console that depends on another service, so
// both ways it can be unavailable say which it is. "The sandbox is broken" and "the
// payment service is not running, or is running without its sandbox flag" need
// different actions from whoever is reading the screen.
static IResult Unconfigured() => Results.Json(
    new { problem = "Sandbox:PaymentsBaseUrl is not set, so the payment panel has nothing to read." },
    statusCode: 501);

static IResult Unreachable(Exception e) => Results.Json(
    new
    {
        problem = "The payment service did not answer. It may be starting, or running "
                + "without Payments:Sandbox=true.",
        detail = e.Message,
    },
    statusCode: 502);

static async Task<IResult> Relay(HttpResponseMessage response, CancellationToken ct)
{
    var body = await response.Content.ReadAsStringAsync(ct);

    // Status and body as they came. A proxy that turned the payment service's 404
    // into its own 200 would make a missing sandbox flag look like an empty queue.
    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
}

namespace EAuction.Sandbox
{
    /// <summary>A seeded dev bidder, as the realm has them.</summary>
    public sealed record Person(string Username, string NameAr, string NationalId);

    /// <summary>Whether the gateway refuses the next charge, and what it reports.</summary>
    public sealed record DeclineRequest(bool Decline, string? Reason);
}
