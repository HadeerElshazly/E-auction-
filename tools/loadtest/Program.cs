using System.Diagnostics;
using System.Net.Http.Headers;
using EAuction.Core;

// Load generator for the bid hot path.
//
// Replays the shape that matters: a burst of concurrent bidders hammering one
// auction, which is what the last thirty seconds of a hot lot looks like.
//
// Usage:
//   eauction-loadtest --url http://localhost:5080 --auction <guid>
//                     --bidders 200 --seconds 20 --rate 5000

var cfg = Args.Parse(args);

Console.WriteLine($"target      {cfg.Url}");
Console.WriteLine($"auction     {cfg.AuctionId}");
Console.WriteLine($"bidders     {cfg.Bidders}");
Console.WriteLine($"duration    {cfg.Seconds}s");
Console.WriteLine($"target rate {cfg.Rate}/s");
Console.WriteLine();

var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
var bidders = Enumerable.Range(0, cfg.Bidders).Select(_ => Guid.NewGuid()).ToArray();

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = Math.Max(64, cfg.Bidders),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    EnableMultipleHttp2Connections = true
};
using var http = new HttpClient(handler)
{
    BaseAddress = new Uri(cfg.Url),
    Timeout = TimeSpan.FromSeconds(10),
    DefaultRequestVersion = System.Net.HttpVersion.Version11
};

// Register the auction and the bidders' eligibility with the catcher. In a
// real environment this arrives over the compacted topics; the dev endpoint
// stands in for that so the harness can run standalone.
var seedBody = System.Text.Json.JsonSerializer.Serialize(new
{
    auctionId = cfg.AuctionId,
    bidders,
    secretHex = Convert.ToHexString(secret),
    durationMinutes = 60
});
using (var seedContent = new StringContent(seedBody, System.Text.Encoding.UTF8, "application/json"))
{
    var seedResponse = await http.PostAsync("/dev/seed", seedContent);
    if (!seedResponse.IsSuccessStatusCode)
    {
        Console.Error.WriteLine(
            $"seeding failed ({(int)seedResponse.StatusCode}). " +
            "Start the catcher with Catcher__EnableDevSeed=true.");
        return 1;
    }
}
Console.WriteLine($"seeded {bidders.Length} eligible bidders");
Console.WriteLine();

var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
var accepted = 0;
var rejected = 0;
var failed = 0;

// Ramp price upward so bids stay above the minimum increment for longer; the
// goal is to exercise the transport and the log, not the rejection branch.
var priceFloor = 1_000_000_00L;

var deadline = Stopwatch.StartNew();
var perWorkerDelayMs = cfg.Rate > 0
    ? Math.Max(0, (int)(1000.0 * cfg.Bidders / cfg.Rate))
    : 0;

var workers = bidders.Select(bidder => Task.Run(async () =>
{
    while (deadline.Elapsed.TotalSeconds < cfg.Seconds)
    {
        var amount = Interlocked.Add(ref priceFloor, 50_000_00);
        var frame = BidFrame.BuildClientFrame(
            cfg.AuctionId, bidder, amount,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Guid.NewGuid(), Random.Shared.NextInt64(), secret);

        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.PostAsync("/bids", content);
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                Interlocked.Increment(ref accepted);
            else
                Interlocked.Increment(ref rejected);
        }
        catch
        {
            sw.Stop();
            Interlocked.Increment(ref failed);
        }

        if (perWorkerDelayMs > 0) await Task.Delay(perWorkerDelayMs);
    }
})).ToArray();

await Task.WhenAll(workers);
deadline.Stop();

var samples = latencies.OrderBy(x => x).ToArray();
if (samples.Length == 0)
{
    Console.WriteLine("no samples collected");
    return 1;
}

double Pct(double p) => samples[(int)Math.Min(samples.Length - 1, p / 100.0 * samples.Length)];

var total = accepted + rejected + failed;
var throughput = total / deadline.Elapsed.TotalSeconds;

Console.WriteLine($"requests    {total}  (accepted {accepted}, rejected {rejected}, failed {failed})");
Console.WriteLine($"throughput  {throughput:N0} req/s over {deadline.Elapsed.TotalSeconds:N1}s");
Console.WriteLine();
Console.WriteLine($"p50         {Pct(50):N2} ms");
Console.WriteLine($"p90         {Pct(90):N2} ms");
Console.WriteLine($"p99         {Pct(99):N2} ms");
Console.WriteLine($"p99.9       {Pct(99.9):N2} ms");
Console.WriteLine($"max         {samples[^1]:N2} ms");
Console.WriteLine();

// The SLO from docs/ARCHITECTURE.md §2.
var p99 = Pct(99);
Console.WriteLine(p99 <= 50
    ? $"p99 {p99:N2} ms is within the 50 ms budget"
    : $"p99 {p99:N2} ms EXCEEDS the 50 ms budget");

return failed > 0 ? 2 : 0;

internal sealed record Args(string Url, Guid AuctionId, int Bidders, int Seconds, int Rate)
{
    public static Args Parse(string[] a)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(a, "--" + name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback;
        }

        return new Args(
            Get("url", "http://localhost:5080"),
            Guid.Parse(Get("auction", Guid.Empty.ToString())),
            int.Parse(Get("bidders", "200")),
            int.Parse(Get("seconds", "15")),
            int.Parse(Get("rate", "0")));
    }
}
