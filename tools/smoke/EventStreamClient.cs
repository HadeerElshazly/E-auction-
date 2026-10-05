using System.Net.Http.Headers;
using System.Text.Json;

namespace EAuction.Smoke;

/// <summary>
/// Reads a server-sent event stream and records what arrived.
///
/// Deliberately a second implementation of the framing in
/// <c>web/shared/src/sse.ts</c>, for the same reason the bid frame has two: a wire
/// format with one implementation is a wire format nobody has checked. This one is
/// the simple version — the stream is read to the end of the test rather than
/// reconnected — and its job is to assert that the server emits what the portal
/// expects.
/// </summary>
public sealed class EventStreamClient : IDisposable
{
    private readonly List<(string Event, string Data)> _received = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public EventStreamClient(HttpClient http, string url, string? token)
    {
        _pump = Task.Run(() => PumpAsync(http, url, token, _stop.Token));
    }

    /// <summary>Set once the response headers have arrived and framing has begun.</summary>
    public bool Open { get; private set; }

    public Exception? Failure { get; private set; }

    private async Task PumpAsync(HttpClient http, string url, string? token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // ResponseHeadersRead, or HttpClient buffers the whole response and a
            // stream that never ends never returns.
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != "text/event-stream")
                throw new SmokeException($"stream served {contentType}, not text/event-stream");

            Open = true;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body);

            var frame = new List<string>();
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (line.Length == 0)
                {
                    Dispatch(frame);
                    frame.Clear();
                    continue;
                }

                frame.Add(line);
            }
        }
        catch (OperationCanceledException) { /* the test finished */ }
        catch (Exception e) { Failure = e; }
    }

    private void Dispatch(List<string> frame)
    {
        var name = "message";
        var data = new List<string>();

        foreach (var line in frame)
        {
            if (line.StartsWith(':')) continue;            // a keep-alive comment

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');

            if (field == "event") name = value;
            else if (field == "data") data.Add(value);
        }

        if (data.Count == 0) return;

        lock (_gate) _received.Add((name, string.Join('\n', data)));
    }

    /// <summary>Waits for an event of the given name whose payload matches.</summary>
    public async Task<JsonElement> WaitForAsync(
        string eventName, Func<JsonElement, bool> matches, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            if (Failure is not null)
                throw new SmokeException($"the stream failed: {Failure.Message}");

            foreach (var (name, data) in Snapshot())
            {
                if (name != eventName) continue;

                var parsed = JsonDocument.Parse(data).RootElement;
                if (matches(parsed)) return parsed.Clone();
            }

            await Task.Delay(100);
        }

        // The payloads, not just the event names: "saw [snapshot, price]" says the
        // stream is alive but not why nothing matched, and the difference between
        // those two is most of the debugging.
        var all = Snapshot();
        var recent = all.TakeLast(4)
            .Select(e => $"{e.Event}: {Truncate(e.Data)}");

        throw new SmokeException(
            $"no matching '{eventName}' on the stream within "
            + $"{(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0}s. "
            + $"{all.Count} event(s); last few: {string.Join(" | ", recent)}");
    }

    private static string Truncate(string data) =>
        data.Length > 220 ? data[..220] + "…" : data;

    public IReadOnlyList<(string Event, string Data)> Snapshot()
    {
        lock (_gate) return _received.ToArray();
    }

    public int CountOf(string eventName) => Snapshot().Count(e => e.Event == eventName);

    public void Dispose()
    {
        _stop.Cancel();
        try { _pump.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        _stop.Dispose();
    }
}
