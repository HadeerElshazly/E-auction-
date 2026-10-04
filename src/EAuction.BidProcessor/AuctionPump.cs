using EAuction.Core;

namespace EAuction.BidProcessor;

/// <summary>
/// Drives one auction: reads its bid stream in offset order and feeds each
/// frame to the engine, publishing the verdict and the new current price.
///
/// One pump per auction, single consumer, so the engine never sees concurrent
/// calls and the offset order it observes is the log's order.
/// </summary>
public sealed class AuctionPump(
    AuctionDefinition auction,
    IBidLog log,
    Func<BidVerdict, CancellationToken, ValueTask> publishVerdict,
    Func<Guid, long, CancellationToken, ValueTask> publishCurrentPrice)
{
    public AuctionEngine Engine { get; } = new(auction);

    public async Task RunAsync(long fromOffset, CancellationToken ct)
    {
        await foreach (var record in log.ReadAsync(auction.AuctionId, fromOffset, ct))
        {
            var verdict = Engine.Apply(record.Offset, record.Frame.Span);

            await publishVerdict(verdict, ct).ConfigureAwait(false);

            // current-winner is compacted and keyed by auctionId: the catcher
            // and the fan-out both consume it (§5).
            if (verdict.Accepted)
                await publishCurrentPrice(auction.AuctionId, Engine.CurrentPrice, ct)
                    .ConfigureAwait(false);
        }
    }
}
