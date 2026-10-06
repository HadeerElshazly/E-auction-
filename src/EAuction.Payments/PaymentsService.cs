using System.Collections.Concurrent;
using System.Text.Json;
using EAuction.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAuction.Payments;

/// <summary>
/// Takes the money a land auction needs taken, and gives back the money it needs
/// given back.
///
/// Four flows, all of them started by an event somebody else published:
///
/// | Event | What this does |
/// |---|---|
/// | <c>BookletFeeRequested</c> | Charges كراسة الشروط, so the bidder may read the terms |
/// | <c>DepositRequested</c> | Charges التأمين, which is what makes them eligible to bid |
/// | <c>AwardConfirmed</c> | Charges مبلغ السعي, a percentage of the price they won at |
/// | <c>DepositsReleasable</c> | Refunds the losers, forfeits the defaulters, applies the winner's to the price |
///
/// It holds no database. What it has already settled is rebuilt by replaying its
/// own output topic from offset 0, the same way the bid catcher rebuilds its
/// control state (D-12) — and here it is not merely convenient: that replay is what
/// stops a redelivered request becoming a second charge.
/// </summary>
public sealed class PaymentsService(
    IPaymentGateway gateway,
    IEventStream events,
    ILogger<PaymentsService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The event type of the marker this service writes to its own output topic to
    /// prove its replay reached the end. Carries no money and no bidder.
    /// </summary>
    public const string RecoveryMarker = "PaymentsRecoveryMarker";

    /// <summary>What has been settled, by <c>auction:bidder:purpose</c>.</summary>
    private readonly ConcurrentDictionary<string, PaymentSettled> _settled = new();

    /// <summary>Brokerage percentages, from the public auction definition.</summary>
    private readonly ConcurrentDictionary<Guid, decimal> _brokerage = new();

    /// <summary>
    /// Awards waiting for their auction's brokerage percentage.
    ///
    /// The two topics are followed concurrently and replayed independently, so an
    /// award can and does arrive before the definition it needs. Dropping it would
    /// lose the municipality its fee on that sale silently, which is the worst shape
    /// a money bug can take: nothing fails, the number is just smaller.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, AwardConfirmedPayload> _pendingAwards = new();

    /// <summary>
    /// Guards the park-or-charge decision across the two topics.
    ///
    /// Without it the two handlers interleave into a lost update: the award looks
    /// for a percentage that is not there yet and parks itself a moment after the
    /// definition looked for a parked award and found none. Both return having done
    /// nothing, and no brokerage is ever charged.
    /// </summary>
    private readonly object _brokerageGate = new();

    private volatile bool _recovered;

    /// <summary>
    /// How long the replay may take before the service gives up and refuses to
    /// start. Not a quiet period — see <see cref="RecoverAsync"/>.
    /// </summary>
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public bool Ready => _recovered;
    public int SettledCount => _settled.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Its own history first, and completely.
        //
        // Reading the requests before knowing what has already been paid would
        // charge every bidder again on a restart — the one failure in this service
        // that cannot be undone by fixing the code afterwards.
        await RecoverAsync(stoppingToken);
        _recovered = true;

        logger.LogInformation(
            "Payments recovered: {Count} settlement(s) already on record.", _settled.Count);

        await Task.WhenAll(
            FollowAsync(Topics.Upcoming, ApplyAuctionAsync, stoppingToken),
            FollowAsync(Topics.ParticipantPayments, ApplyRequestAsync, stoppingToken),
            FollowAsync(Topics.Deposits, ApplyReleaseAsync, stoppingToken),
            FollowAsync(Topics.Lifecycle, ApplyLifecycleAsync, stoppingToken));
    }

    /// <summary>
    /// Replays the settlement log until it reaches a marker this process has just
    /// written to the end of it.
    ///
    /// Every other service here decides it is caught up when a topic has been
    /// silent for a while (<c>ProcessorService.DrainUntilQuietAsync</c>), and for
    /// those it is the right call: being a little behind on the auction catalogue
    /// costs a retry. Here it is a guess about whether this service has already
    /// charged somebody, and the guess is wrong in the expensive direction — a slow
    /// broker or a slow consumer-group assignment looks exactly like an empty
    /// topic, and an empty topic means charge everybody again.
    ///
    /// Reading back a record this process wrote is not a guess. The settlements
    /// topic has one partition, so partition offset order is a total order over it
    /// (D-03): the marker is at the end, and seeing it means every settlement
    /// written before this process started has already been applied.
    ///
    /// It costs one record per restart on a topic that is never compacted, which is
    /// a fair price and doubles as a restart log.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        var marker = JsonSerializer.Serialize(new { marker = Guid.NewGuid() }, Json);

        // Written before the replay, and read during it. If this throws, the
        // service does not start — and should not: a payment service that cannot
        // write its own audit log must not take anyone's money.
        await events.PublishAsync(Topics.Settlements, "recovery", marker, RecoveryMarker, ct);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(RecoveryTimeout);

        try
        {
            await foreach (var record in events.ReadAsync(Topics.Settlements, bounded.Token))
            {
                if (record.EventType == RecoveryMarker)
                {
                    // Ours: provably at the end. Anyone else's is an older restart.
                    if (record.Payload == marker) return;
                    continue;
                }

                Remember(record);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The marker never came back. Carrying on would mean charging from an
            // incomplete picture of what has already been paid, so it stops instead
            // and takes the host down with it.
            throw new InvalidOperationException(
                $"Could not replay {Topics.Settlements} within {RecoveryTimeout}. "
                + "Refusing to start: without the full settlement history this service "
                + "cannot tell which bidders it has already charged.");
        }
    }

    private void Remember(StreamEvent record)
    {
        var settled = JsonSerializer.Deserialize<PaymentSettled>(record.Payload, Json);
        if (settled is null) return;

        // A refusal is not a settlement: the bidder still owes the money, and a
        // retry must be allowed to charge. Keeping it would leave them stuck
        // forever behind a card that was declined once.
        if (settled.Outcome == PaymentOutcomes.Refused) return;

        _settled[settled.Key] = settled;
    }

    private async Task FollowAsync(
        string topic, Func<StreamEvent, CancellationToken, Task> handle, CancellationToken ct)
    {
        try
        {
            await foreach (var record in events.ReadAsync(topic, ct))
            {
                try
                {
                    await handle(record, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Never let one unreadable record stop the service: a payment
                    // stream that stops is a room full of bidders who cannot qualify.
                    logger.LogError(e, "Failed to handle {EventType} on {Topic}:{Offset}.",
                        record.EventType, topic, record.Offset);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // --- what it listens to -------------------------------------------------

    private Task ApplyAuctionAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.AuctionApproved) return Task.CompletedTask;

        var payload = JsonSerializer.Deserialize<AuctionTermsPayload>(record.Payload, Json);
        if (payload is null) return Task.CompletedTask;

        AwardConfirmedPayload? waiting;
        lock (_brokerageGate)
        {
            _brokerage[payload.AuctionId] = payload.BrokerageFeePercent;
            _pendingAwards.Remove(payload.AuctionId, out waiting);
        }

        return waiting is null
            ? Task.CompletedTask
            : ChargeBrokerageAsync(waiting, payload.BrokerageFeePercent, ct);
    }

    private Task ApplyRequestAsync(StreamEvent record, CancellationToken ct)
    {
        var purpose = record.EventType switch
        {
            InboundEvents.BookletFeeRequested => PaymentPurpose.Booklet,
            InboundEvents.DepositRequested => PaymentPurpose.Deposit,
            _ => (PaymentPurpose?)null,
        };

        if (purpose is null) return Task.CompletedTask;

        var payload = JsonSerializer.Deserialize<PaymentRequestPayload>(record.Payload, Json);
        if (payload is null) return Task.CompletedTask;

        return ChargeAsync(
            payload.AuctionId, payload.BidderId, purpose.Value,
            payload.AmountMinorUnits, payload.Method, ct);
    }

    private Task ApplyLifecycleAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.AwardConfirmed) return Task.CompletedTask;

        var payload = JsonSerializer.Deserialize<AwardConfirmedPayload>(record.Payload, Json);
        if (payload is null) return Task.CompletedTask;

        decimal percent;
        lock (_brokerageGate)
        {
            if (!_brokerage.TryGetValue(payload.AuctionId, out percent))
            {
                // The auction definition has not arrived yet. Charging a guessed
                // brokerage on a land sale is not an option, so the award waits for
                // the percentage rather than being charged at nothing.
                _pendingAwards[payload.AuctionId] = payload;

                logger.LogInformation(
                    "Auction {AuctionId}: award confirmed before its brokerage percentage "
                    + "was known; holding the fee until the definition arrives.",
                    payload.AuctionId);

                return Task.CompletedTask;
            }
        }

        return ChargeBrokerageAsync(payload, percent, ct);
    }

    private Task ChargeBrokerageAsync(
        AwardConfirmedPayload award, decimal percent, CancellationToken ct)
    {
        // Rounded away from zero at the halala, the same direction a cashier would:
        // a fee that rounded down would be short on every single sale.
        var amount = (long)Math.Round(
            award.AmountMinorUnits * percent / 100m, MidpointRounding.AwayFromZero);

        if (amount <= 0) return Task.CompletedTask;

        return ChargeAsync(
            award.AuctionId, award.WinnerBidderId, PaymentPurpose.Brokerage,
            amount, "Payment", ct);
    }

    /// <summary>
    /// The end of an auction's money: the losers get their deposits back, the
    /// defaulters do not, and the winner's is set against what they now owe.
    /// </summary>
    private async Task ApplyReleaseAsync(StreamEvent record, CancellationToken ct)
    {
        if (record.EventType != InboundEvents.DepositsReleasable) return;

        var payload = JsonSerializer.Deserialize<DepositsReleasablePayload>(record.Payload, Json);
        if (payload is null) return;

        var forfeit = (payload.ForfeitForBidders ?? []).ToHashSet();

        // Everyone this auction took a deposit from, which this service knows
        // because it took them. Nobody else has to be asked.
        var deposits = _settled.Values
            .Where(s => s.AuctionId == payload.AuctionId
                        && s.Purpose == nameof(PaymentPurpose.Deposit)
                        && s.Outcome == PaymentOutcomes.Charged)
            .ToList();

        foreach (var deposit in deposits)
        {
            if (forfeit.Contains(deposit.BidderId))
            {
                await RecordAsync(deposit with
                {
                    Outcome = PaymentOutcomes.Forfeited,
                    At = DateTimeOffset.UtcNow,
                }, ct);
                continue;
            }

            if (payload.AppliedToPurchaseForBidder == deposit.BidderId)
            {
                await RecordAsync(deposit with
                {
                    Outcome = PaymentOutcomes.AppliedToPurchase,
                    At = DateTimeOffset.UtcNow,
                }, ct);
                continue;
            }

            await RefundAsync(deposit, ct);
        }
    }

    // --- what it does -------------------------------------------------------

    private async Task ChargeAsync(
        Guid auctionId, Guid bidderId, PaymentPurpose purpose,
        long amount, string method, CancellationToken ct)
    {
        var key = $"{auctionId}:{bidderId}:{purpose}";

        // Already taken. The request has simply been delivered again, which is
        // normal and must cost the bidder nothing.
        if (_settled.ContainsKey(key)) return;

        var instruction = new PaymentInstruction
        {
            AuctionId = auctionId,
            BidderId = bidderId,
            Purpose = purpose,
            AmountMinorUnits = amount,
            Method = method,
            IdempotencyKey = key,
        };

        var outcome = await gateway.ChargeAsync(instruction, ct);

        await RecordAsync(new PaymentSettled
        {
            AuctionId = auctionId,
            BidderId = bidderId,
            Purpose = purpose.ToString(),
            Outcome = outcome.Settled ? PaymentOutcomes.Charged : PaymentOutcomes.Refused,
            AmountMinorUnits = amount,
            Reference = outcome.Reference,
            FailureReason = outcome.FailureReason,
            At = DateTimeOffset.UtcNow,
        }, ct);
    }

    private async Task RefundAsync(PaymentSettled deposit, CancellationToken ct)
    {
        var instruction = new PaymentInstruction
        {
            AuctionId = deposit.AuctionId,
            BidderId = deposit.BidderId,
            Purpose = PaymentPurpose.Deposit,
            AmountMinorUnits = deposit.AmountMinorUnits,
            IdempotencyKey = $"{deposit.Key}:refund",
        };

        var outcome = await gateway.RefundAsync(instruction, deposit.Reference, ct);

        await RecordAsync(deposit with
        {
            Outcome = outcome.Settled ? PaymentOutcomes.Refunded : PaymentOutcomes.Refused,
            Reference = outcome.Settled ? outcome.Reference : deposit.Reference,
            FailureReason = outcome.FailureReason,
            At = DateTimeOffset.UtcNow,
        }, ct);
    }

    /// <summary>
    /// Publishes a settlement and remembers it, in that order.
    ///
    /// Publishing first means a crash in between leaves a record on the topic that
    /// this service will read back on recovery. The other order would leave it
    /// believing it had settled something nobody downstream was ever told about.
    /// </summary>
    private async Task RecordAsync(PaymentSettled settled, CancellationToken ct)
    {
        await events.PublishAsync(
            Topics.Settlements, settled.Key,
            JsonSerializer.Serialize(settled, Json), nameof(PaymentSettled), ct);

        if (settled.Outcome != PaymentOutcomes.Refused) _settled[settled.Key] = settled;

        logger.LogInformation(
            "{Purpose} {Outcome} for bidder {BidderId} on auction {AuctionId}: {Amount} halala.",
            settled.Purpose, settled.Outcome, settled.BidderId, settled.AuctionId,
            settled.AmountMinorUnits);
    }
}
