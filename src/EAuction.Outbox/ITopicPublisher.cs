namespace EAuction.Outbox;

/// <summary>Writes an outbox row onto its topic.</summary>
public interface ITopicPublisher
{
    /// <summary>
    /// Creates a topic if it does not exist. Must be idempotent — the relay is
    /// at-least-once and will call this again after a crash.
    /// </summary>
    Task EnsureTopicAsync(string topic, CancellationToken ct);

    Task PublishAsync(string topic, string key, string payload, string eventType, CancellationToken ct);
}

/// <summary>Records what was published, in order, so the relay can be tested.</summary>
public sealed class InMemoryTopicPublisher : ITopicPublisher
{
    private readonly List<string> _actions = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Actions { get { lock (_gate) return _actions.ToList(); } }
    public List<(string Topic, string Key, string Payload, string EventType)> Published { get; } = new();

    /// <summary>Set to fail the next publish, to test that the row stays queued.</summary>
    public Func<string, bool>? FailPublishFor { get; set; }

    public Task EnsureTopicAsync(string topic, CancellationToken ct)
    {
        lock (_gate) _actions.Add($"ensure-topic:{topic}");
        return Task.CompletedTask;
    }

    public Task PublishAsync(
        string topic, string key, string payload, string eventType, CancellationToken ct)
    {
        if (FailPublishFor?.Invoke(eventType) == true)
            throw new InvalidOperationException($"Simulated publish failure for {eventType}.");

        lock (_gate)
        {
            _actions.Add($"publish:{topic}:{eventType}:{key}");
            Published.Add((topic, key, payload, eventType));
        }
        return Task.CompletedTask;
    }
}
