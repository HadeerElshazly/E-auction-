using EAuction.Core;
using EAuction.Payments;

var builder = Host.CreateApplicationBuilder(args);

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
if (builder.Environment.IsProduction()
    && !builder.Configuration.GetValue("Payments:AllowSimulatedGateway", false))
{
    throw new InvalidOperationException(
        "No real payment gateway is configured. Set Payments:AllowSimulatedGateway=true "
        + "only for a non-production environment — the simulator settles everything "
        + "without taking any money.");
}

builder.Services.AddSingleton<IPaymentGateway>(new SimulatedGateway());
builder.Services.AddHostedService<PaymentsService>();

var host = builder.Build();
await host.RunAsync();
