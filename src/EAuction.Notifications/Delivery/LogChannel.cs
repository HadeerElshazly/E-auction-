using EAuction.Notifications.Domain;
using Microsoft.Extensions.Logging;

namespace EAuction.Notifications.Delivery;

/// <summary>
/// Writes what would have been sent, and nothing more.
///
/// It exists so the dispatch path is exercised end to end — a notification is
/// composed, handed to a channel, and marked dispatched — rather than the whole
/// outbound half being unreachable until an aggregator contract lands. When the
/// SMS adapter arrives it replaces this and nothing upstream changes.
///
/// It deliberately does not log the body. A notification body names an auction, a
/// price and sometimes an award: a log that carried all of it would be a readable
/// record of who is bidding on what, retained wherever logs go, which is exactly
/// what D-22 keeps off the public topics.
/// </summary>
public sealed class LogChannel(ILogger<LogChannel> logger) : INotificationChannel
{
    public string Name => "log";

    public Task<bool> SendAsync(Notification notification, CancellationToken ct)
    {
        logger.LogInformation(
            "Notification {Kind} for bidder {BidderId} on auction {AuctionId} "
            + "would be sent here.",
            notification.Kind, notification.BidderId, notification.AuctionId);

        return Task.FromResult(true);
    }
}
