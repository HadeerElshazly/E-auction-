namespace EAuction.Core;

/// <summary>
/// Read/write access to the control-plane topics — everything that is not the
/// bid hot path. <see cref="IBidLog"/> stays separate because the bid path has
/// different requirements (binary frames, no deserialisation, acks=all).
/// </summary>
public interface IEventStream : IAsyncDisposable
{
    Task PublishAsync(string topic, string key, string payload, string eventType, CancellationToken ct);

    /// <summary>
    /// Reads a topic from the beginning and then follows it. Compacted control
    /// topics are replayed in full on startup, which is how a consumer rebuilds
    /// its state without a database.
    /// </summary>
    IAsyncEnumerable<StreamEvent> ReadAsync(string topic, CancellationToken ct);
}

public readonly record struct StreamEvent(
    string Topic, string Key, string Payload, string EventType, long Offset);
