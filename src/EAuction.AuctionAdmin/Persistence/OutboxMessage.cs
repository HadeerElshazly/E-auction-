using System.Text.Json;
using EAuction.AuctionAdmin.Domain;

namespace EAuction.AuctionAdmin.Persistence;

/// <summary>
/// An outbox row, shaped for Debezium's EventRouter SMT so the production path
/// needs no bespoke publisher: Debezium tails the WAL, the SMT routes on
/// <see cref="AggregateType"/>, keys on <see cref="AggregateId"/> and uses
/// <see cref="Payload"/> as the message value.
///
/// The column names are the SMT's defaults on purpose (id, aggregatetype,
/// aggregateid, type, payload) — renaming them means configuring the SMT in
/// every environment, which is a thing to get wrong.
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

    public static OutboxMessage From(DomainEvent domainEvent) => new()
    {
        AggregateType = domainEvent.AggregateType,
        AggregateId = domainEvent.AggregateId,
        Type = domainEvent.Type,
        Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json)
    };
}
