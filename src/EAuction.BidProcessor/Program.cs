using EAuction.BidProcessor;
using EAuction.Core;

var builder = Host.CreateApplicationBuilder(args);

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

if (string.IsNullOrWhiteSpace(bootstrap))
{
    // No broker configured: run entirely in process. Useful for local work,
    // and the ordering contract is the same, but nothing survives a restart.
    builder.Services.AddSingleton<IBidLog>(new InMemoryBidLog());
    builder.Services.AddSingleton<IEventStream>(new InMemoryEventStream());
}
else
{
    builder.Services.AddSingleton<IBidLog>(
        new KafkaBidLog(new KafkaBidLogOptions { BootstrapServers = bootstrap }));
    builder.Services.AddSingleton<IEventStream>(
        new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "bid-processor"
        }));
}

builder.Services.AddSingleton(new SupervisorOptions
{
    CloseGrace = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Processor:CloseGraceSeconds", 5))
});

builder.Services.AddSingleton(new ProcessorServiceOptions
{
    TickInterval = TimeSpan.FromMilliseconds(
        builder.Configuration.GetValue("Processor:TickIntervalMs", 500))
});

builder.Services.AddSingleton<AuctionRegistry>();
builder.Services.AddSingleton<AuctionSupervisor>();
builder.Services.AddHostedService<ProcessorService>();

await builder.Build().RunAsync();
