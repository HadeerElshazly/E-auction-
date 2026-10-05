using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Confluent.Kafka;
using EAuction.Core;

// ---------------------------------------------------------------------------
// Load test for the push channel.
//
// Measures the thing the fan-out exists to make possible: thousands of watchers
// held open on one query BFF replica, each getting every price change promptly.
// The polling it replaces costs ~10,000 requests a second at the design target of
// 10,000 concurrent bidders in the final minute of an auction — the same order as
// the bid load itself — so what matters here is whether one replica can hold that
// many connections and how long a change takes to reach them.
//
//   eauction-fanouttest --bff http://localhost:5105 --kafka 127.0.0.1:9092
//                       --watchers 2000 --changes 50 --interval-ms 200
//
// It publishes the auction definition and the price changes to Kafka directly,
// standing in for auction-admin's outbox and the bid processor. That is deliberate:
// the bid path has its own load test in tools/loadtest, and driving real bids here
// would measure the catcher and the processor again instead of the fan-out.
//
// Latency is measured from the `asOf` the BFF stamps when it serialises a change to
// the moment the watcher's socket delivers it, so it is the fan-out's own cost and
// needs no clock synchronisation — both ends are this machine.
// ---------------------------------------------------------------------------

var cfg = Args.Parse(args);

// The thread pool grows by about one thread every half second, so a client that
// starts thousands of socket readers at once spends the first seconds queueing its
// own work and then reports that as the server's latency. Pre-sizing it means the
// numbers below measure the fan-out rather than this tool.
ThreadPool.SetMinThreads(Math.Max(Environment.ProcessorCount * 4, 256), 256);

Console.WriteLine($"BFF        {cfg.Bff}");
Console.WriteLine($"Kafka      {cfg.Kafka}");
Console.WriteLine($"Watchers   {cfg.Watchers:N0}");
Console.WriteLine($"Changes    {cfg.Changes} every {cfg.IntervalMs}ms");
Console.WriteLine();

var auctionId = Guid.NewGuid();
using var producer = new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = cfg.Kafka,
    Acks = Acks.All,
    EnableIdempotence = true
}).Build();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- the auction, so the BFF will accept a stream for it -------------------

const long opening = 1_000_000_00;
const long increment = 50_000_00;

await Publish(Topics.Upcoming, "AuctionApproved", new
{
    auctionId,
    nameAr = "اختبار الحمل",
    nameEn = "Fan-out load test",
    channel = "Online",
    startsAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    endsAt = DateTimeOffset.UtcNow.AddHours(1),
    openingPriceMinorUnits = opening,
    minIncrementMinorUnits = increment,
    depositMinorUnits = 100_000_00,
    bookletPriceMinorUnits = 1_000_00,
    quietPeriodSeconds = (int?)null,
    maxExtensions = 0,
    plotCount = 1,
    totalAreaSqm = 600m,
    plots = new[]
    {
        new { id = Guid.NewGuid(), deedNumber = "LOAD/1", areaSqm = 600m,
              latitude = (string?)null, longitude = (string?)null,
              descriptionAr = (string?)null, descriptionEn = (string?)null }
    }
});

await Publish(Topics.Lifecycle, "AuctionStarted", new { auctionId, at = DateTimeOffset.UtcNow });

Console.Write("Waiting for the BFF to see the auction… ");
using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
if (!await WaitForAuction(probe, cfg.Bff, auctionId))
{
    Console.WriteLine("not there.");
    Console.Error.WriteLine(
        $"The BFF never showed {auctionId}. Is it consuming {Topics.Upcoming} on {cfg.Kafka}?");
    return 2;
}
Console.WriteLine("ready.");

// --- hold the watchers open ------------------------------------------------

// No timeout: HttpClient.Timeout covers reading the body even with
// ResponseHeadersRead, so an ordinary timeout severs every stream at that mark.
using var http = new HttpClient
{
    Timeout = Timeout.InfiniteTimeSpan,
    DefaultRequestVersion = new Version(1, 1)
};

var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
var connected = 0;
var failed = 0;
var received = 0;
using var stop = new CancellationTokenSource();

Console.Write($"Opening {cfg.Watchers:N0} streams… ");
var openWatch = Stopwatch.StartNew();

var watchers = Enumerable.Range(0, cfg.Watchers)
    .Select(_ => Task.Run(() => WatchAsync(http, cfg.Bff, auctionId, stop.Token)))
    .ToArray();

// Wait for them all to be established before driving any change, or the early
// changes are measured against a partly-connected fleet.
var deadline = DateTime.UtcNow.AddSeconds(120);
while (Volatile.Read(ref connected) + Volatile.Read(ref failed) < cfg.Watchers
       && DateTime.UtcNow < deadline)
{
    await Task.Delay(100);
}
openWatch.Stop();

Console.WriteLine(
    $"{connected:N0} connected, {failed:N0} failed in {openWatch.Elapsed.TotalSeconds:0.0}s");

if (connected == 0)
{
    Console.Error.WriteLine("No watcher connected; nothing to measure.");
    stop.Cancel();
    return 1;
}

var rssAfterConnect = BffMemoryMiB();
Console.WriteLine();

// --- drive the price changes ----------------------------------------------

Console.WriteLine($"Driving {cfg.Changes} price changes…");
var before = Volatile.Read(ref received);

for (var i = 1; i <= cfg.Changes; i++)
{
    await Publish(Topics.CurrentWinner, "CurrentWinner", new
    {
        auctionId,
        priceMinorUnits = opening + i * increment,
        leaderBidderId = Guid.NewGuid(),
        leaderClientBidId = Guid.NewGuid(),
        effectiveEndsAt = DateTimeOffset.UtcNow.AddHours(1),
        extensionsUsed = 0
    });

    await Task.Delay(cfg.IntervalMs);
}

// Let the tail arrive.
await Task.Delay(3000);
stop.Cancel();
try { await Task.WhenAll(watchers); } catch { /* cancelled */ }

// --- report ---------------------------------------------------------------

var delivered = Volatile.Read(ref received) - before;
var expected = (long)connected * cfg.Changes;
var samples = latencies.ToArray();
Array.Sort(samples);

Console.WriteLine();
Console.WriteLine("── Delivery ────────────────────────────────────────────");
Console.WriteLine($"  watchers connected   {connected:N0}");
Console.WriteLine($"  changes published    {cfg.Changes}");
Console.WriteLine($"  messages expected    {expected:N0}");
Console.WriteLine($"  messages delivered   {delivered:N0}  ({(expected == 0 ? 0 : 100.0 * delivered / expected):0.0}%)");

if (samples.Length > 0)
{
    Console.WriteLine();
    Console.WriteLine("── Latency, BFF serialise → watcher receives ───────────");
    Console.WriteLine($"  p50   {Percentile(samples, 50),8:0.00} ms");
    Console.WriteLine($"  p95   {Percentile(samples, 95),8:0.00} ms");
    Console.WriteLine($"  p99   {Percentile(samples, 99),8:0.00} ms");
    Console.WriteLine($"  max   {samples[^1],8:0.00} ms");
}

Console.WriteLine();
Console.WriteLine("── Cost on one BFF replica ────────────────────────────");
Console.WriteLine($"  RSS with {connected:N0} streams   {rssAfterConnect:0} MiB");
if (rssAfterConnect > 0 && connected > 0)
    Console.WriteLine($"  per stream                 ~{rssAfterConnect * 1024 / connected:0} KiB");

Console.WriteLine();
Console.WriteLine("Compare: polling the same fleet once a second would be "
                  + $"{connected:N0} requests/second for the whole final minute, for "
                  + $"{cfg.Changes} actual changes.");

return 0;

// ---------------------------------------------------------------------------

async Task WatchAsync(HttpClient client, string bff, Guid auction, CancellationToken ct)
{
    try
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{bff.TrimEnd('/')}/auctions/{auction}/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        Interlocked.Increment(ref connected);

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);

        string? dataLine = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLine = line[5..].TrimStart(' ');
                continue;
            }

            // A blank line ends the frame; measure then.
            if (line.Length == 0 && dataLine is not null)
            {
                Measure(dataLine);
                dataLine = null;
            }
        }
    }
    catch (OperationCanceledException) { /* the run ended */ }
    catch
    {
        Interlocked.Increment(ref failed);
    }
}

void Measure(string data)
{
    try
    {
        var asOf = JsonDocument.Parse(data).RootElement.GetProperty("asOf").GetDateTimeOffset();
        latencies.Add((DateTimeOffset.UtcNow - asOf).TotalMilliseconds);
        Interlocked.Increment(ref received);
    }
    catch
    {
        // A snapshot or a frame this tool does not model. Not a delivery failure.
    }
}

async Task Publish(string topic, string eventType, object payload)
{
    var result = await producer.ProduceAsync(topic, new Message<string, string>
    {
        Key = auctionId.ToString(),
        Value = JsonSerializer.Serialize(payload, json),
        Headers = new Headers
        {
            { "eventType", System.Text.Encoding.UTF8.GetBytes(eventType) }
        }
    });

    if (result.Status != PersistenceStatus.Persisted)
        throw new InvalidOperationException($"{eventType} not persisted to {topic}");
}

static async Task<bool> WaitForAuction(HttpClient client, string bff, Guid auction)
{
    for (var i = 0; i < 90; i++)
    {
        try
        {
            using var response = await client.GetAsync(
                $"{bff.TrimEnd('/')}/auctions/{auction}");
            if (response.IsSuccessStatusCode) return true;
        }
        catch (HttpRequestException) { /* still starting */ }
        catch (TaskCanceledException) { /* still starting */ }

        await Task.Delay(1000);
    }
    return false;
}

static double Percentile(double[] sorted, int percentile)
{
    if (sorted.Length == 0) return 0;
    var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

/// <summary>Resident memory of the BFF process, in MiB, or 0 if it cannot be found.</summary>
static double BffMemoryMiB()
{
    try
    {
        foreach (var process in Process.GetProcessesByName("EAuction.QueryBff"))
            return process.WorkingSet64 / 1024.0 / 1024.0;

        // Launched via `dotnet EAuction.QueryBff.dll`, so the process is "dotnet".
        foreach (var process in Process.GetProcessesByName("dotnet"))
        {
            var cmdline = File.ReadAllText($"/proc/{process.Id}/cmdline");
            if (cmdline.Contains("EAuction.QueryBff", StringComparison.Ordinal))
                return process.WorkingSet64 / 1024.0 / 1024.0;
        }
    }
    catch
    {
        // Not on Linux, or no permission. The latency numbers are the point anyway.
    }
    return 0;
}

internal sealed record Args(string Bff, string Kafka, int Watchers, int Changes, int IntervalMs)
{
    public static Args Parse(string[] a) => new(
        Value(a, "bff") ?? "http://localhost:5105",
        Value(a, "kafka") ?? "127.0.0.1:9092",
        int.Parse(Value(a, "watchers") ?? "2000"),
        int.Parse(Value(a, "changes") ?? "50"),
        int.Parse(Value(a, "interval-ms") ?? "200"));

    private static string? Value(string[] a, string name)
    {
        var i = Array.IndexOf(a, "--" + name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
