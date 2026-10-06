namespace EAuction.Notifications.Domain;

/// <summary>
/// How far this service has already read each topic.
///
/// It is this service's offset store, and it exists because
/// <see cref="EAuction.Core.IEventStream"/> deliberately has none:
/// every consumer gets a unique group and replays from offset 0, which is what
/// lets the bid catcher and the bid processor rebuild their state from nothing
/// (D-12). Those services only need the *latest* value per key, so replaying costs
/// them nothing. This one has to tell a person something, once, which makes "have I
/// already seen this record" a question it cannot avoid answering.
///
/// <para>
/// Keeping it is what makes the two halves work. Below the watermark, a record is
/// history: absorbed into the roster and announced to nobody. Above it, a record is
/// news — including everything that happened while the service was down, which is
/// exactly what a restart should catch up on and send.
/// </para>
/// </summary>
public sealed class TopicWatermark
{
    public string Topic { get; private set; } = "";

    /// <summary>The highest offset already handled. -1 when nothing has been.</summary>
    public long Offset { get; private set; } = -1;

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private TopicWatermark() { }

    public TopicWatermark(string topic, long offset, DateTimeOffset now)
    {
        Topic = topic;
        Offset = offset;
        UpdatedAt = now;
    }

    /// <summary>
    /// Never moves backwards. Two readers of the same topic would otherwise be able
    /// to lower it between them and re-announce what the other had passed.
    /// </summary>
    public void Advance(long offset, DateTimeOffset now)
    {
        if (offset <= Offset) return;
        Offset = offset;
        UpdatedAt = now;
    }
}
