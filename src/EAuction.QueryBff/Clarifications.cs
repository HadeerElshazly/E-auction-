using System.Collections.Concurrent;
using System.Text.Json;
using EAuction.Core;

namespace EAuction.QueryBff;

/// <summary>One public clarification: staff's question and answer, never a bidder's words.</summary>
public sealed record Clarification(Guid Id, string QuestionAr, string AnswerAr, DateTimeOffset PublishedAt);

/// <summary>
/// «التوضيحات العامة» (الخاصية 10) per auction, as the catalogue serves them.
///
/// Fed by <c>ClarificationPublished</c> on <c>auctions.inquiries</c>, which carries
/// only the text staff wrote and a committee approved — so nothing here can name a
/// bidder, because nothing that names one is on that event.
/// </summary>
public sealed class Clarifications
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Clarification>> _byAuction = new();

    public void Add(Guid auctionId, Clarification c) =>
        _byAuction.GetOrAdd(auctionId, _ => new())[c.Id] = c;

    public IReadOnlyList<Clarification> For(Guid auctionId) =>
        _byAuction.TryGetValue(auctionId, out var items)
            ? items.Values.OrderBy(c => c.PublishedAt).ToArray()
            : [];
}

/// <summary>Replays <c>auctions.inquiries</c> into <see cref="Clarifications"/>.</summary>
public sealed class ClarificationsConsumer(
    Clarifications store, IEventStream events, ILogger<ClarificationsConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var record in events.ReadAsync(Topics.Inquiries, ct))
            {
                // The reply to a bidder travels on the same topic and is none of the
                // public catalogue's business.
                if (record.EventType != "ClarificationPublished") continue;
                try
                {
                    var p = JsonSerializer.Deserialize<Payload>(record.Payload, Json);
                    if (p is null) continue;
                    store.Add(p.AuctionId, new Clarification(p.ClarificationId, p.QuestionAr, p.AnswerAr, p.PublishedAt));
                }
                catch (JsonException e)
                {
                    logger.LogError(e, "Unreadable clarification at offset {Offset}.", record.Offset);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private sealed record Payload(
        Guid ClarificationId, Guid AuctionId, string QuestionAr, string AnswerAr, DateTimeOffset PublishedAt);
}
