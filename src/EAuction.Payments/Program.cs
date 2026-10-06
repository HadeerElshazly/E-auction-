using EAuction.Core;
using EAuction.Payments;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

if (string.IsNullOrWhiteSpace(bootstrap))
{
    builder.Services.AddSingleton<IEventStream>(new InMemoryEventStream());
}
else
{
    builder.Services.AddSingleton<IEventStream>(new KafkaEventStream(new KafkaEventStreamOptions
    {
        BootstrapServers = bootstrap,
        ConsumerGroup = "payments",
    }));
}

// The gateway.
//
// Only the simulator exists, because PayTabs and SADAD both need merchant
// accounts that the contract has not produced yet (P-3). The seam is what matters
// and it is built: an adapter drops in here and nothing upstream changes.
//
// Production refuses to start on the simulator. A deployment that silently
// "settled" every deposit without taking a riyal would qualify every bidder in the
// country to bid on state land, and would look exactly like success.
var simulated = builder.Configuration.GetValue("Payments:AllowSimulatedGateway", false);

if (builder.Environment.IsProduction() && !simulated)
{
    throw new InvalidOperationException(
        "No real payment gateway is configured. Set Payments:AllowSimulatedGateway=true "
        + "only for a non-production environment — the simulator settles everything "
        + "without taking any money.");
}

// The sandbox: the simulator with a switch on the front and a window on the side,
// so a demonstration can show the booklet fee and the deposit being taken, and can
// reach the non-payment path on purpose.
//
// Gated on the simulator's own flag and not on the environment, because this stack
// runs every service as Production — ASPNETCORE_ENVIRONMENT is deliberately unset
// in Compose so the startup guards above are armed. An `IsProduction()` test would
// therefore switch the sandbox *off* in exactly the stack it is for, and on in a
// developer's `dotnet run`. The flag is the honest gate: wherever the simulated
// gateway is permitted, no real money exists to be at risk from a console over it.
var sandboxEnabled = simulated
    && builder.Configuration.GetValue("Payments:Sandbox", false);

SandboxGateway? sandbox = null;

if (sandboxEnabled)
{
    sandbox = new SandboxGateway();
    builder.Services.AddSingleton<IPaymentGateway>(sandbox);
    builder.Services.AddSingleton(sandbox);
}
else
{
    builder.Services.AddSingleton<IPaymentGateway>(new SimulatedGateway());
}

// Purposes as names, not ordinals.
//
// The sandbox surface is read by a person and by one small page, and
// System.Text.Json writes an enum as its number by default — so the booklet fee
// arrived on screen as "0". Worse than ugly: the ordinals are a wire contract
// nobody declared, and inserting a purpose in the middle of the enum one day would
// silently relabel every historical row on that screen.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton<PaymentsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PaymentsService>());

var app = builder.Build();

// Liveness and readiness, as every other service here has them. This service used
// to be a bare worker with no HTTP surface at all, which meant Compose and the
// chart could only ever report it as "started" — never as ready, and never as
// wedged. Readiness is its replay: until its own output topic has been read to the
// end it cannot know what it has already charged (D-12).
app.MapGet("/health/live", () => Results.Ok("ok"));

app.MapGet("/health/ready", (PaymentsService payments) =>
    payments.Ready ? Results.Ok("ok") : Results.StatusCode(503));

if (sandboxEnabled && sandbox is not null)
{
    // Read-only, and deliberately unauthenticated — which is only acceptable
    // because of what this is: a console over a gateway that moves no money, in a
    // stack whose keys are published in git. It is reachable in the first place
    // only where Payments:AllowSimulatedGateway is already set.
    //
    // What it must never become is a surface that can be reached from a portal
    // origin: no CORS is configured here, so a browser on another origin cannot
    // read it, and the sandbox service proxies it server-side instead.
    app.MapGet("/sandbox/instructions", () => Results.Ok(new
    {
        declineCharges = sandbox.DeclineCharges,
        declineReason = sandbox.DeclineReason,
        items = sandbox.Recent,
    }));

    app.MapPost("/sandbox/decline", (DeclineRequest r) =>
    {
        sandbox.DeclineCharges = r.Decline;

        if (!string.IsNullOrWhiteSpace(r.Reason)) sandbox.DeclineReason = r.Reason;

        return Results.Ok(new
        {
            declineCharges = sandbox.DeclineCharges,
            declineReason = sandbox.DeclineReason,
        });
    });
}

await app.RunAsync();

/// <summary>Whether the gateway refuses the next charge, and what it reports.</summary>
internal sealed record DeclineRequest(bool Decline, string? Reason);
