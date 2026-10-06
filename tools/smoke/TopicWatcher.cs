using Confluent.Kafka;

namespace EAuction.Smoke;

/// <summary>
/// Tails control-plane topics from the beginning so a step can wait for the event a
/// service was supposed to emit. This is how the smoke test proves the services are
/// actually talking over Kafka rather than each being separately plausible.
/// </summary>
public sealed class TopicWatcher : IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly List<(string Topic, string Key, string Type, string Payload)> _seen = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public TopicWatcher(string bootstrap, IEnumerable<string> topics)
    {
        _consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = $"smoke-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        }).Build();

        _consumer.Subscribe(topics);
        _pump = Task.Run(Pump);
    }

    private void Pump()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is null) continue;

                var type = result.Message.Headers
                    .TryGetLastBytes("eventType", out var bytes)
                    ? System.Text.Encoding.UTF8.GetString(bytes)
                    : "";

                lock (_gate)
                    _seen.Add((result.Topic, result.Message.Key ?? "", type, result.Message.Value ?? ""));
            }
            catch (OperationCanceledException) { return; }
            catch (ConsumeException) { /* a topic not created yet; it will be */ }
        }
    }

    /// <summary>
    /// Waits for one auction's event on a topic and returns its payload.
    ///
    /// The auction id is a required argument rather than something the caller folds
    /// into <paramref name="where"/>, because the control topics are compacted and
    /// long-lived: every previous run's auctions are still on them. A filter that
    /// matched only on a price accepted a verdict from an auction held minutes
    /// earlier and reported the wrong winner as this run's.
    /// </summary>
    public async Task<string> WaitForAsync(
        string topic, string eventType, Guid auctionId, Func<string, bool>? where = null,
        TimeSpan? timeout = null)
    {
        var id = auctionId.ToString();

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                foreach (var e in _seen)
                    if (e.Topic == topic && (eventType.Length == 0 || e.Type == eventType)
                        && e.Payload.Contains(id, StringComparison.OrdinalIgnoreCase)
                        && (where is null || where(e.Payload)))
                        return e.Payload;
            }
            await Task.Delay(250);
        }

        lock (_gate)
        {
            var onTopic = _seen.Where(e => e.Topic == topic).Select(e => e.Type).Distinct();
            throw new SmokeException(
                $"No {eventType} for {auctionId} on {topic} within "
                + $"{(timeout ?? TimeSpan.FromSeconds(45)).TotalSeconds:0}s. "
                + $"Saw on that topic: [{string.Join(", ", onTopic)}]");
        }
    }

    /// <summary>
    /// Looks for an event and returns null if it never comes, rather than throwing.
    ///
    /// For the assertions that are about absence: that the winner was not refunded
    /// as well as credited, for instance. Absence needs its own method because
    /// <see cref="WaitForAsync"/> treats not finding something as the failure, and
    /// here it is the pass.
    /// </summary>
    public async Task<string?> TryFindAsync(
        string topic, string eventType, Func<string, bool> where, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                foreach (var e in _seen)
                    if (e.Topic == topic
                        && (eventType.Length == 0 || e.Type == eventType)
                        && where(e.Payload))
                        return e.Payload;
            }
            await Task.Delay(250);
        }

        return null;
    }

    /// <summary>Asserts an event did NOT arrive — the reserve price must never leave its topic.</summary>
    public bool SawAnythingOn(string topic)
    {
        lock (_gate) return _seen.Any(e => e.Topic == topic);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _pump.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        _consumer.Close();
        _consumer.Dispose();
        _stop.Dispose();
    }
}
