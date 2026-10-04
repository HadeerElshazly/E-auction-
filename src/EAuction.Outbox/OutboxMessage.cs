using System.Text.Json;

namespace EAuction.Outbox;

/// <summary>
/// An outbox row, shaped for Debezium's EventRouter SMT so the production path
/// needs no bespoke publisher: Debezium tails the WAL, the SMT routes on
/// <see cref="AggregateType"/>, keys on <see cref="AggregateId"/> and uses
/// <see cref="Payload"/> as the message value.
///
/// The column names are the SMT's defaults on purpose — renaming them means
/// configuring the SMT in every environment, which is a thing to get wrong.
///
/// This is why the outbox exists at all (D-16): CDC straight off the domain
/// tables would make the internal schema the public event contract, and every
/// column rename would become a breaking change for consumers.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string AggregateType { get; init; } = "";
    public string AggregateId { get; init; } = "";
    public string Type { get; init; } = "";
    public string Payload { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set by the polling relay only. Debezium ignores this column — it reads
    /// the insert from the WAL and never looks at the row again.
    /// </summary>
    public DateTimeOffset? RelayedAt { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OutboxMessage From(IDomainEvent domainEvent) => new()
    {
        AggregateType = domainEvent.AggregateType,
        AggregateId = domainEvent.AggregateId,
        Type = domainEvent.GetType().Name,
        Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json)
    };
}

/// <summary>
/// Something the rest of the system needs to know about. Raised by an
/// aggregate and turned into an outbox row inside the same transaction as the
/// state change, so neither can exist without the other.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Debezium EventRouter routes on this. A routing detail, kept out of the payload.</summary>
    string AggregateType { get; }

    /// <summary>The message key. Also a column, also not part of the payload.</summary>
    string AggregateId { get; }
}
