using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Persistence;
using EAuction.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EAuction.AuctionAdmin.Outbox;

/// <summary>
/// The auction-specific parts of relaying: where each row goes, and the two
/// things that have to happen around publishing an approval.
/// </summary>
public sealed class AuctionOutboxRouter : IOutboxRouter
{
    public string? Resolve(string aggregateType) => TopicMap.Resolve(aggregateType);

    /// <summary>
    /// An auction must not reach auctions.upcoming before its bid topic
    /// exists, or a bid could arrive for a topic that is not there. Broker
    /// auto-create is disabled precisely so this cannot be papered over.
    /// </summary>
    public async Task BeforePublishAsync(
        OutboxMessage message, ITopicPublisher publisher, CancellationToken ct)
    {
        if (message.Type != nameof(AuctionApproved)) return;
        if (!Guid.TryParse(message.AggregateId, out var auctionId)) return;

        await publisher.EnsureTopicAsync(TopicMap.BidTopicFor(auctionId), ct);
    }

    /// <summary>
    /// Publication is what makes an auction visible to bidders, so the relay
    /// is the only thing that knows when Approved becomes Scheduled. Saved in
    /// the same transaction as the relayed marker.
    /// </summary>
    public async Task AfterPublishAsync(
        DbContext db, OutboxMessage message, CancellationToken ct)
    {
        if (message.Type != nameof(AuctionApproved)) return;
        if (!Guid.TryParse(message.AggregateId, out var auctionId)) return;

        var auction = await ((AdminDbContext)db).Auctions
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);

        if (auction?.Status == AuctionStatus.Approved) auction.MarkScheduled();
    }
}
