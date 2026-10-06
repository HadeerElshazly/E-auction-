using System.Buffers.Binary;
using System.Text;
using EAuction.Core;
using EAuction.Outbox;

namespace EAuction.Audit.Domain;

/// <summary>
/// One link in the staff audit trail.
///
/// Two halves, and the division between them is the whole design.
///
/// <para>
/// <see cref="Payload"/>, <see cref="Key"/>, <see cref="EventType"/> and
/// <see cref="Offset"/> are <em>the evidence</em>: exactly what arrived on
/// <c>staff.actions</c>, byte for byte, and exactly what the hash covers. The
/// chain attests to what the topic said, which is a claim this service can
/// actually make — not to what it understood, which it cannot.
/// </para>
///
/// <para>
/// Everything from <see cref="ActorSubject"/> down is <em>a projection</em>: the
/// payload's fields lifted into columns so an auditor can filter on them. They are
/// convenience, not evidence, and they are nullable because a record this service
/// cannot parse still has to be recorded.
/// </para>
///
/// <para>
/// The projection is deliberately <em>not</em> hashed, and that is not a gap left
/// open: it is a pure function of the payload, so hashing it would add nothing a
/// chain over the payload does not already cover. What it does mean is that the
/// stored projection could be altered without breaking a link — and since the
/// projection is what the API returns and filters on, that would mislead a reader
/// looking at an entry whose evidence is intact. <see cref="ProjectionMatchesPayload"/>
/// is the answer, and <c>GET /audit/verify</c> checks it on every entry alongside
/// the hashes.
/// </para>
/// </summary>
public sealed class AuditEntry
{
    /// <summary>
    /// The record's own offset on <c>staff.actions</c>, and the primary key.
    ///
    /// Not a generated id, and that is deliberate on three counts. Kafka delivers
    /// at least once and <see cref="IEventStream"/> replays every topic from the
    /// start on each restart (D-12), so a replay has to be a no-op — with the
    /// offset as the key it is a unique violation this service swallows, rather
    /// than a second copy of the same action. The topic has one partition, so the
    /// offset is also a total order, which is what a chain needs. And a missing
    /// entry becomes visible rather than a matter of inference: consecutive
    /// offsets are what the trail should hold, so <c>GET /audit/verify</c> walks
    /// them and lists every break.
    /// </summary>
    public long Offset { get; private set; }

    /// <summary>The <c>eventType</c> header. Always <c>StaffActionRecorded</c> today.</summary>
    public string EventType { get; private set; } = "";

    /// <summary>The record's key, which the producers set to the subject.</summary>
    public string Key { get; private set; } = "";

    /// <summary>The record's value, verbatim. The thing <see cref="Hash"/> covers.</summary>
    public string Payload { get; private set; } = "";

    // --- the projection --------------------------------------------------------

    public Guid? ActorSubject { get; private set; }
    public string? ActorRoles { get; private set; }
    public string? Action { get; private set; }
    public string? Subject { get; private set; }
    public string? Details { get; private set; }
    public string? SourceAddress { get; private set; }

    /// <summary>When the acting service says it happened.</summary>
    public DateTimeOffset? At { get; private set; }

    /// <summary>
    /// Whether the payload parsed. True means the projected columns are empty and
    /// the payload is all there is — which is still a complete record of what the
    /// topic carried, and is why a malformed record never breaks the chain.
    /// </summary>
    public bool Malformed { get; private set; }

    // --- the chain -------------------------------------------------------------

    /// <summary>
    /// When this service wrote the row. Outside the hash on purpose.
    ///
    /// Two replicas read the same topic and write the same rows; if a clock reading
    /// were in the frame they would compute different hashes for the same offset
    /// and the chain would depend on which one got there first. Everything hashed
    /// comes from the record, so the chain is a function of the topic alone.
    /// </summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public byte[] Hash { get; private set; } = [];

    /// <summary>
    /// The hash this one was built on. Stored although it is derivable, because it
    /// is what makes a single row checkable: an auditor holding one entry and its
    /// predecessor can verify the link without the rest of the table.
    /// </summary>
    public byte[] PreviousHash { get; private set; } = [];

    private AuditEntry() { }

    /// <summary>
    /// Builds the entry for a record, chained onto <paramref name="chain"/>.
    ///
    /// The chain is mutated, so this must be called once per record in offset
    /// order — which is what a single-partition topic read from the beginning
    /// gives, and why <c>ControlTopics</c> fixes the partition count at one.
    /// </summary>
    public static AuditEntry From(StreamEvent record, LedgerChain chain, DateTimeOffset now)
    {
        var entry = new AuditEntry
        {
            Offset = record.Offset,
            EventType = record.EventType,
            Key = record.Key,
            Payload = record.Payload,
            RecordedAt = now,
            PreviousHash = chain.Head.ToArray(),
        };

        var parsed = TryProject(record.Payload);
        if (parsed is null)
        {
            entry.Malformed = true;
        }
        else
        {
            entry.ActorSubject = parsed.ActorSubject;
            entry.ActorRoles = parsed.ActorRoles;
            entry.Action = parsed.Action;
            entry.Subject = parsed.Subject;
            entry.Details = parsed.Details;
            entry.SourceAddress = parsed.SourceAddress;
            entry.At = parsed.At;
        }

        entry.Hash = chain.Append(Frame(record.Offset, record.EventType, record.Key, record.Payload));
        return entry;
    }

    /// <summary>
    /// The bytes the chain is computed over, for this entry as it is stored.
    ///
    /// Used by the verify endpoint, which recomputes the chain from the table. It
    /// goes through the same <see cref="Frame"/> as the consumer did — one
    /// implementation, because two would eventually disagree and the disagreement
    /// would read as tampering.
    /// </summary>
    public byte[] Frame() => Frame(Offset, EventType, Key, Payload);

    /// <summary>
    /// Length-prefixed, not delimited.
    ///
    /// A separator would make the frame ambiguous: <c>Details</c> is free text
    /// composed at a call site, and whatever character was chosen as the separator
    /// could appear in it. Two different sets of fields producing one frame means
    /// two different records producing one hash, which is the one property a chain
    /// must not have.
    /// </summary>
    private static byte[] Frame(long offset, string eventType, string key, string payload)
    {
        var parts = new[] { eventType, key, payload }
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();

        var frame = new byte[8 + parts.Sum(p => 4 + p.Length)];
        BinaryPrimitives.WriteInt64BigEndian(frame, offset);

        var at = 8;
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(at), part.Length);
            at += 4;
            part.CopyTo(frame, at);
            at += part.Length;
        }

        return frame;
    }

    /// <summary>
    /// Whether the projected columns still say what the payload says.
    ///
    /// Checked by the verify endpoint on every entry, because the hash does not
    /// cover these columns — see the note on the type. Without it, the one
    /// alteration that would survive verification is the one a reader would
    /// actually see: the <c>Details</c> of an approval rewritten while the payload
    /// beside it still holds the truth.
    /// </summary>
    public bool ProjectionMatchesPayload()
    {
        var parsed = TryProject(Payload);

        if (parsed is null)
            return Malformed
                && ActorSubject is null && ActorRoles is null && Action is null
                && Subject is null && Details is null && SourceAddress is null
                && At is null;

        return !Malformed
            && ActorSubject == parsed.ActorSubject
            && ActorRoles == parsed.ActorRoles
            && Action == parsed.Action
            && Subject == parsed.Subject
            && Details == parsed.Details
            && SourceAddress == parsed.SourceAddress

            // To the microsecond, because that is what the column keeps. A
            // DateTimeOffset carries 100-nanosecond ticks and Postgres
            // timestamptz does not, so an exact comparison would report every
            // entry whose clock reading happened not to land on a microsecond —
            // which is nine in ten of them.
            && Microseconds(At) == Microseconds(parsed.At);
    }

    private static DateTimeOffset? Microseconds(DateTimeOffset? value) =>
        value is null ? null : value.Value.AddTicks(-(value.Value.Ticks % 10));

    private static StaffActionRecorded? TryProject(string payload)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<StaffActionRecorded>(
                payload, new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            // A record this service cannot read is still a record it must keep.
            // Dropping it would leave a gap in the offsets and break every hash
            // after it, so an unreadable payload becomes a row whose projection is
            // empty and whose evidence is intact.
            return null;
        }
    }
}
