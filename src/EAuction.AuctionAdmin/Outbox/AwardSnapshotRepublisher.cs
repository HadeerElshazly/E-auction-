using EAuction.AuctionAdmin.Domain;
using EAuction.AuctionAdmin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EAuction.AuctionAdmin.Outbox;

/// <summary>
/// Publishes the current <see cref="AwardFollowUpUpdated"/> snapshot of every award
/// once, at startup.
///
/// The snapshot is how the winner's portal learns where their award stands, and it
/// is raised on every change — but an award made before the event existed, or one
/// whose snapshot a consumer lost with its database, would otherwise never be
/// announced until somebody touched it again. A snapshot says the whole state, so
/// sending it again is harmless: the consumer keeps the newest and the content is
/// the same. A few rows per restart, at this platform's scale.
/// </summary>
public sealed class AwardSnapshotRepublisher(
    IDbContextFactory<AdminDbContext> dbFactory,
    ILogger<AwardSnapshotRepublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var auctions = await db.Auctions
                .Include(a => a.Awards)
                .Where(a => a.Awards.Any())
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;
            foreach (var auction in auctions) auction.RepublishFollowUp(now);

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Republished the award snapshot of {Count} auctions.", auctions.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A convenience for consumers, not something to stop the service over.
            logger.LogWarning(e, "Could not republish award snapshots.");
        }
    }
}
