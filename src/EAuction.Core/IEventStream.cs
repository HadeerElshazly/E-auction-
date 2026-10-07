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

    /// <summary>
    /// The offset of the last record on a topic, or -1 when it has none.
    ///
    /// Exists because "have I reached the end of the history" cannot be answered by
    /// waiting for silence. A consumer takes a second or two to be assigned a
    /// partition and deliver its first record, which is indistinguishable from an
    /// empty topic — and a service that guesses wrong in that direction treats the
    /// platform's entire history as news.
    ///
    /// <para>
    /// That is not a hypothetical. The notification service's first version used a
    /// two-second quiet period, saw nothing because the broker had not answered
    /// yet, and announced every auction on the cluster to every bidder who had ever
    /// registered. This answers the question instead of estimating it.
    /// </para>
    /// </summary>
    Task<long> LatestOffsetAsync(string topic, CancellationToken ct);
}

/// <param name="Timestamp">
/// When the broker recorded it — for consumers whose events carry no time of their
/// own and must not stamp a replayed history with the moment of the replay.
/// </param>
public readonly record struct StreamEvent(
    string Topic, string Key, string Payload, string EventType, long Offset,
    DateTimeOffset? Timestamp = null);
