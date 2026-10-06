using EAuction.Notifications.Domain;

namespace EAuction.Notifications.Delivery;

/// <summary>
/// An outbound channel — SMS, email, push — behind a seam (D-17).
///
/// None is implemented, and that is a contract problem rather than a coding one:
/// SMS to Saudi numbers goes through a licensed aggregator with a registered sender
/// name, and neither the aggregator nor the sender name exists yet (P-7). Push
/// needs the mobile app, which is not built.
///
/// <para>
/// What is built is the thing that does not need a contract: the in-product inbox,
/// which is a real delivered channel rather than a stub. A bidder sees their
/// notifications in the portal whether or not an SMS ever goes out.
/// </para>
///
/// <para>
/// Note what this interface deliberately does not take: a phone number or an email
/// address. Those live in the participant service with the national ID, and this
/// service does not hold them — a notification database that accumulated every
/// bidder's contact details would be a second copy of the PDPL-sensitive data the
/// participant service exists to confine. An adapter resolves the recipient at send
/// time, from the service that owns them.
/// </para>
/// </summary>
public interface INotificationChannel
{
    /// <summary>What this channel is, for the log and for operations.</summary>
    string Name { get; }

    /// <summary>
    /// Returns true when the channel accepted it. False is not an error — a
    /// bidder with no mobile number is an ordinary case — and the inbox has the
    /// notification either way.
    /// </summary>
    Task<bool> SendAsync(Notification notification, CancellationToken ct);
}
