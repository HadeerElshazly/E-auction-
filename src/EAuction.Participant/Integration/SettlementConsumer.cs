using System.Text.Json;
using EAuction.Core;
using EAuction.Participant.Domain;
using EAuction.Participant.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EAuction.Participant.Integration;

/// <summary>
/// Closes the loop on money: the payment service reports what it took, and this
/// turns that into the two facts the subscription cares about — the booklet is paid
/// for, and the deposit is settled.
///
/// This is the half that was missing. Before it, the endpoints took a payment
/// reference from the caller and believed it, so a bidder could reach the bid floor
/// of a land auction without a riyal having moved. Eligibility now has exactly one
/// cause, and it is a settlement on a topic this service does not write.
///
/// It is also why eligibility is asynchronous. <c>POST .../deposit</c> returns 202
/// and the subscription sits at <c>AwaitingDeposit</c> until the gateway answers —
/// which is the truth about taking money and worth showing honestly, rather than a
/// synchronous call that pretends the bank is in the same process.
/// </summary>
public sealed class SettlementConsumer(
    IDbContextFactory<ParticipantDbContext> dbFactory,
    IEventStream events,
    ILogger<SettlementConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var record in events.ReadAsync(Topics.Settlements, stoppingToken))
            {
                try
                {
                    await ApplyAsync(record, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A settlement this service cannot apply must not stop the ones
                    // behind it: every bidder after it in the log would stay stuck
                    // awaiting a deposit the gateway has already taken.
                    logger.LogError(ex,
                        "Failed to apply a settlement at offset {Offset} (key {Key}).",
                        record.Offset, record.Key);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ApplyAsync(StreamEvent record, CancellationToken ct)
    {
        // The payment service also writes a recovery marker to this topic on every
        // restart, to prove its own replay reached the end. It carries no money.
        if (record.EventType != nameof(PaymentSettled)) return;

        var settled = JsonSerializer.Deserialize<PaymentSettled>(record.Payload, Json);
        if (settled is null) return;

        // Brokerage is charged to the winner after the award and changes nothing
        // about their subscription; refunds and forfeitures are resolved from
        // auctions.deposits, which the catalogue consumer already follows.
        if (settled.Purpose is not (PaymentPurposes.Booklet or PaymentPurposes.Deposit)) return;
        if (settled.Outcome is not (PaymentOutcomeNames.Charged or PaymentOutcomeNames.Refused))
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var subscription = await db.Subscriptions.FirstOrDefaultAsync(
            s => s.AuctionId == settled.AuctionId && s.BidderId == settled.BidderId, ct);

        if (subscription is null)
        {
            // A settlement for a subscription this service has no row for. On a
            // replay that is ordinary — the database was reset, the topic was not —
            // so it is worth a line but not an error.
            logger.LogInformation(
                "Settlement for an unknown subscription {AuctionId}:{BidderId}; ignored.",
                settled.AuctionId, settled.BidderId);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (settled.Outcome == PaymentOutcomeNames.Refused)
        {
            subscription.RecordPaymentRefused(
                settled.Purpose, settled.FailureReason ?? "Refused", now);
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "{Purpose} refused for bidder {BidderId} on auction {AuctionId}: {Reason}.",
                settled.Purpose, settled.BidderId, settled.AuctionId, settled.FailureReason);
            return;
        }

        try
        {
            if (settled.Purpose == PaymentPurposes.Booklet)
            {
                subscription.ConfirmBookletPayment(settled.Reference, now);
            }
            else
            {
                // Eligibility re-checks the bidder and the terms, so both are needed.
                var bidder = await db.Bidders.FindAsync(new object?[] { settled.BidderId }, ct);
                var terms = await db.AuctionTerms.FindAsync(new object?[] { settled.AuctionId }, ct);

                if (bidder is null || terms is null)
                {
                    logger.LogWarning(
                        "Deposit settled for {AuctionId}:{BidderId} but the "
                        + "{Missing} is not known here yet; the bidder stays awaiting.",
                        settled.AuctionId, settled.BidderId,
                        bidder is null ? "bidder" : "auction");
                    return;
                }

                subscription.ConfirmDepositPayment(settled.Reference, bidder, terms, now);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (ParticipantValidationException ex)
        {
            // The money was taken but the bidder does not qualify — an incomplete
            // profile, an unverified identity. Not a crash and not a success: it is
            // a refund somebody has to decide about, so it is logged loudly and the
            // subscription is left where it is.
            logger.LogError(
                "{Purpose} settled for bidder {BidderId} on auction {AuctionId}, but they "
                + "cannot be qualified: {Problems}. The payment stands and needs resolving.",
                settled.Purpose, settled.BidderId, settled.AuctionId,
                string.Join("; ", ex.Problems));
        }
        catch (InvalidSubscriptionTransitionException ex)
        {
            // A settlement that does not fit the subscription's current state: a
            // deposit landing on a subscription already revoked, for instance.
            logger.LogWarning(
                "{Purpose} settled for bidder {BidderId} on auction {AuctionId} but {Message}.",
                settled.Purpose, settled.BidderId, settled.AuctionId, ex.Message);
        }
    }

    /// <summary>
    /// This service's own view of the payment service's contract — the five fields
    /// it acts on, and nothing else. Declared here rather than referenced, because
    /// the payment service consumes this one's events: a project reference would
    /// close the loop at build time as well as at run time.
    /// </summary>
    private sealed record PaymentSettled
    {
        public Guid AuctionId { get; init; }
        public Guid BidderId { get; init; }
        public string Purpose { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string Reference { get; init; } = "";
        public string? FailureReason { get; init; }
    }
}
