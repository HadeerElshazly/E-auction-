using System.Collections.Concurrent;
using System.Threading.Channels;

namespace EAuction.Core;

/// <summary>
/// In-process event stream mirroring Kafka's per-topic ordering. Used for
/// local development and tests; see <see cref="KafkaEventStream"/> for the
/// deployed implementation.
/// </summary>
public sealed class InMemoryEventStream : IEventStream
{
    private sealed class Topic
    {
        public readonly List<StreamEvent> Records = new();
        public readonly List<Channel<StreamEvent>> Subscribers = new();
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, Topic> _topics = new();

    public Task PublishAsync(
        string topic, string key, string payload, string eventType, CancellationToken ct)
    {
        var t = _topics.GetOrAdd(topic, _ => new Topic());

        StreamEvent record;
        List<Channel<StreamEvent>> subscribers;
        lock (t.Gate)
        {
            record = new StreamEvent(topic, key, payload, eventType, t.Records.Count);
            t.Records.Add(record);
            subscribers = t.Subscribers.ToList();
        }

        foreach (var s in subscribers) s.Writer.TryWrite(record);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StreamEvent> ReadAsync(
        string topic,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var t = _topics.GetOrAdd(topic, _ => new Topic());
        var channel = Channel.CreateUnbounded<StreamEvent>();

        List<StreamEvent> backlog;
        lock (t.Gate)
        {
            backlog = t.Records.ToList();
            t.Subscribers.Add(channel);
        }

        foreach (var record in backlog) yield return record;

        long next = backlog.Count;
        await foreach (var record in channel.Reader.ReadAllAsync(ct))
        {
            if (record.Offset < next) continue;
            next = record.Offset + 1;
            yield return record;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
