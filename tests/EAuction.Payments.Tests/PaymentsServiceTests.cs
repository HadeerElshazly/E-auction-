using System.Text.Json;
using EAuction.Core;
using EAuction.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Payments.Tests;

/// <summary>
/// The payment service against its own topics.
///
/// The thing under test is not really "does it charge" — it is "does it charge
/// once". Everything upstream is at-least-once, this service holds no database,
/// and the failure it has to be impossible for is taking a hundred thousand riyals
/// off a citizen twice because a pod restarted.
/// </summary>
public class PaymentsServiceTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InMemoryEventStream _events = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PaymentsService> _running = [];

    private static readonly Guid Auction = Guid.NewGuid();
    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly Guid Khalid = Guid.NewGuid();

    private async Task<(PaymentsService Service, SimulatedGateway Gateway)> StartAsync(
        SimulatedGateway? gateway = null)
    {
        gateway ??= new SimulatedGateway();
        var service = new PaymentsService(
            gateway, _events, NullLogger<PaymentsService>.Instance)
        {
            RecoveryTimeout = TimeSpan.FromSeconds(10)
        };

        await service.StartAsync(_cts.Token);
        _running.Add(service);

        // Recovery replays the settlement log before the service will read a
        // request, and that is the point: until it is done, it does not know what
        // it has already charged.
        await Until(() => service.Ready, "the service to finish recovering");
        return (service, gateway);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var service in _running)
        {
            try { await service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            service.Dispose();
        }
        await _events.DisposeAsync();
        _cts.Dispose();
    }

    // --- the money ----------------------------------------------------------

    [Fact]
    public async Task A_deposit_request_is_charged_and_reported()
    {
        await StartAsync();
        await RequestAsync("DepositRequested", Sara, 100_000_00);

        var settled = await SettlementAsync(Sara, "Deposit");

        Assert.Equal(PaymentOutcomes.Charged, settled.Outcome);
        Assert.Equal(100_000_00, settled.AmountMinorUnits);
        Assert.StartsWith("SIM-DEP-", settled.Reference);
    }

    [Fact]
    public async Task The_same_request_delivered_twice_is_charged_once()
    {
        var (_, gateway) = await StartAsync();

        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await SettlementAsync(Sara, "Deposit");

        // The outbox relay, Kafka, or a rebalance — any of them can hand the same
        // row over again, and all of them do.
        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await RequestAsync("DepositRequested", Sara, 100_000_00);

        await Task.Delay(300, _cts.Token);

        Assert.Equal(1, gateway.Charges);
        Assert.Single(await AllSettlementsAsync());
    }

    [Fact]
    public async Task A_restart_does_not_charge_the_deposit_again()
    {
        // The one failure in this service that cannot be undone by fixing the code
        // afterwards. It has no database: everything it knows about what it has
        // already taken comes from replaying its own output topic, so if that
        // replay is wrong, every bidder in the country is charged a second time
        // the first time a pod is rescheduled.
        var (first, firstGateway) = await StartAsync();

        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await SettlementAsync(Sara, "Deposit");
        await first.StopAsync(CancellationToken.None);

        var (second, secondGateway) = await StartAsync();

        Assert.Equal(1, second.SettledCount);

        // The request is still on the topic, so the restarted service reads it again.
        await Task.Delay(300, _cts.Token);

        Assert.Equal(0, secondGateway.Charges);
        Assert.Equal(1, firstGateway.Charges);
        Assert.Single(await AllSettlementsAsync());
    }

    [Fact]
    public async Task A_refusal_is_reported_and_does_not_block_a_second_attempt()
    {
        // A refusal is not a settlement: the bidder still owes the money. Keeping
        // it as one would leave them stuck behind a card that was declined once.
        var (_, gateway) = await StartAsync();

        await RequestAsync("DepositRequested", Sara, 100_000_13);
        var refused = await SettlementAsync(Sara, "Deposit");

        Assert.Equal(PaymentOutcomes.Refused, refused.Outcome);
        Assert.Equal("InsufficientFunds", refused.FailureReason);
        Assert.Equal("", refused.Reference);
        Assert.Equal(0, gateway.Charges);

        // Paid off a different card for an amount the gateway accepts.
        await RequestAsync("DepositRequested", Sara, 100_000_00);

        await Until(
            async () => (await AllSettlementsAsync())
                .Any(s => s.Outcome == PaymentOutcomes.Charged),
            "the retry to be charged");
    }

    [Fact]
    public async Task Brokerage_is_a_percentage_of_the_price_actually_won_at()
    {
        await StartAsync();

        // 2.5% of 1,234,567.89 SAR is 30,864.19725 — rounded away from zero at the
        // halala, the direction a cashier rounds. Down, the municipality would be
        // short on every single sale.
        await ApproveAsync(brokeragePercent: 2.5m);
        await AwardAsync(Sara, 1_234_567_89);

        var settled = await SettlementAsync(Sara, "Brokerage");

        Assert.Equal(PaymentOutcomes.Charged, settled.Outcome);
        Assert.Equal(30_864_20, settled.AmountMinorUnits);
    }

    [Fact]
    public async Task An_award_that_arrives_before_its_percentage_waits_for_it()
    {
        // The two topics are followed concurrently, so this ordering is ordinary,
        // not exotic. Charging a guessed percentage on a land sale is not an option
        // and neither is dropping the fee: the municipality would simply be short
        // on that sale with nothing in the logs to say so.
        await StartAsync();

        await AwardAsync(Sara, 1_000_000_00);
        await Task.Delay(300, _cts.Token);

        Assert.DoesNotContain(await AllSettlementsAsync(), s => s.Purpose == "Brokerage");

        await ApproveAsync(brokeragePercent: 2.5m);

        var settled = await SettlementAsync(Sara, "Brokerage");
        Assert.Equal(25_000_00, settled.AmountMinorUnits);
    }

    // --- the end of an auction's money --------------------------------------

    [Fact]
    public async Task A_cancellation_with_refund_returns_deposits_and_booklet_fees_through_the_gateway()
    {
        await StartAsync();

        await RequestAsync("BookletFeeRequested", Sara, 1_000_00);
        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await RequestAsync("BookletFeeRequested", Khalid, 1_000_00);
        await SettlementAsync(Sara, "Deposit");
        await SettlementAsync(Khalid, "Booklet");

        await CancelReleaseAsync(refund: true);

        await Until(
            async () => (await AllSettlementsAsync()).Count(s => s.Outcome == PaymentOutcomes.Refunded) == 3,
            "both booklets and the deposit refunded");

        var all = await AllSettlementsAsync();
        Assert.Equal(PaymentOutcomes.Refunded, all.Last(s => s.BidderId == Sara && s.Purpose == "Booklet").Outcome);
        Assert.Equal(PaymentOutcomes.Refunded, all.Last(s => s.BidderId == Sara && s.Purpose == "Deposit").Outcome);
        Assert.Equal(PaymentOutcomes.Refunded, all.Last(s => s.BidderId == Khalid && s.Purpose == "Booklet").Outcome);
    }

    [Fact]
    public async Task A_cancellation_without_refund_keeps_every_deposit_and_booklet_fee()
    {
        await StartAsync();

        await RequestAsync("BookletFeeRequested", Sara, 1_000_00);
        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await RequestAsync("DepositRequested", Khalid, 100_000_00);
        await SettlementAsync(Sara, "Booklet");
        await SettlementAsync(Sara, "Deposit");
        await SettlementAsync(Khalid, "Deposit");

        await CancelReleaseAsync(refund: false);

        await Until(
            async () => (await AllSettlementsAsync()).Count(s => s.Outcome == PaymentOutcomes.Forfeited) == 2,
            "both deposits kept");

        var all = await AllSettlementsAsync();
        Assert.DoesNotContain(all, s => s.Outcome == PaymentOutcomes.Refunded);
        Assert.Equal(PaymentOutcomes.Charged, all.Last(s => s.Purpose == "Booklet").Outcome);
    }

    [Fact]
    public async Task Releasing_deposits_refunds_the_losers_and_keeps_the_defaulter_s()
    {
        await StartAsync();

        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await RequestAsync("DepositRequested", Khalid, 100_000_00);
        await SettlementAsync(Sara, "Deposit");
        await SettlementAsync(Khalid, "Deposit");

        // Sara won and defaulted; Khalid lost and gets his money back.
        await ReleaseAsync(forfeit: [Sara], appliedToPurchase: null);

        await Until(
            async () => (await AllSettlementsAsync()).Any(
                s => s.BidderId == Khalid && s.Outcome == PaymentOutcomes.Refunded),
            "Khalid's refund");

        var all = await AllSettlementsAsync();

        var sara = all.Last(s => s.BidderId == Sara);
        Assert.Equal(PaymentOutcomes.Forfeited, sara.Outcome);

        // Forfeiting keeps the charge's reference: no money moved, so there is no
        // refund reference to report, and inventing one would overstate what was
        // returned to bidders.
        Assert.StartsWith("SIM-DEP-", sara.Reference);

        var khalid = all.Last(s => s.BidderId == Khalid);
        Assert.Equal(PaymentOutcomes.Refunded, khalid.Outcome);
        Assert.StartsWith("RFND-DEP-", khalid.Reference);
    }

    [Fact]
    public async Task The_winner_s_deposit_is_applied_to_the_price_not_refunded()
    {
        // Reporting it as a refund would overstate what came back to bidders by the
        // largest deposit in the auction.
        await StartAsync();

        await RequestAsync("DepositRequested", Sara, 100_000_00);
        await SettlementAsync(Sara, "Deposit");

        await ReleaseAsync(forfeit: [], appliedToPurchase: Sara);

        await Until(
            async () => (await AllSettlementsAsync()).Any(
                s => s.Outcome == PaymentOutcomes.AppliedToPurchase),
            "the winner's deposit to be applied");

        Assert.DoesNotContain(
            await AllSettlementsAsync(), s => s.Outcome == PaymentOutcomes.Refunded);
    }

    [Fact]
    public async Task A_release_delivered_twice_refunds_once()
    {
        var (_, gateway) = await StartAsync();

        await RequestAsync("DepositRequested", Khalid, 100_000_00);
        await SettlementAsync(Khalid, "Deposit");

        await ReleaseAsync(forfeit: [], appliedToPurchase: null);
        await Until(
            async () => (await AllSettlementsAsync())
                .Any(s => s.Outcome == PaymentOutcomes.Refunded),
            "the refund");

        var before = (await AllSettlementsAsync()).Count;

        await ReleaseAsync(forfeit: [], appliedToPurchase: null);
        await Task.Delay(300, _cts.Token);

        // The deposit is no longer Charged, so it is no longer in the set this
        // service releases — and the gateway's idempotency key would have caught it
        // even if it were.
        Assert.Equal(before, (await AllSettlementsAsync()).Count);
        Assert.Equal(1, gateway.Charges);
    }

    [Fact]
    public async Task Nothing_is_refunded_to_a_bidder_who_never_paid()
    {
        // A refund is always against a charge. A free-standing payment to a person
        // is the shape of every payments bug that ends up in a newspaper.
        await StartAsync();

        await ReleaseAsync(forfeit: [], appliedToPurchase: null);
        await Task.Delay(300, _cts.Token);

        Assert.Empty(await AllSettlementsAsync());
    }

    // --- the simulator's own contract ---------------------------------------

    [Fact]
    public async Task The_simulator_honours_the_idempotency_key()
    {
        // The service relies on this to be safe under a crash between charging and
        // publishing. A simulator that charged twice would hide exactly the bug the
        // key exists to prevent, so this asserts the stand-in keeps the promise the
        // PayTabs and SADAD adapters will have to keep.
        var gateway = new SimulatedGateway();
        var instruction = new PaymentInstruction
        {
            AuctionId = Auction,
            BidderId = Sara,
            Purpose = PaymentPurpose.Deposit,
            AmountMinorUnits = 100_000_00,
            IdempotencyKey = $"{Auction}:{Sara}:Deposit"
        };

        var first = await gateway.ChargeAsync(instruction, _cts.Token);
        var second = await gateway.ChargeAsync(instruction, _cts.Token);

        Assert.True(first.Settled);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(1, gateway.Charges);
    }

    [Fact]
    public async Task A_reference_does_not_say_who_paid_what()
    {
        // References end up in emails, bank statements and support tickets. One
        // derived from the auction and the bidder is one that leaks them.
        var gateway = new SimulatedGateway();
        var outcome = await gateway.ChargeAsync(new PaymentInstruction
        {
            AuctionId = Auction,
            BidderId = Sara,
            Purpose = PaymentPurpose.Deposit,
            AmountMinorUnits = 100_000_00,
            IdempotencyKey = "k"
        }, _cts.Token);

        Assert.DoesNotContain(Auction.ToString("N"), outcome.Reference);
        Assert.DoesNotContain(Sara.ToString("N"), outcome.Reference);
        Assert.DoesNotContain("100000", outcome.Reference);
    }

    // --- harness ------------------------------------------------------------

    private Task RequestAsync(string eventType, Guid bidderId, long amount) =>
        _events.PublishAsync(
            Topics.ParticipantPayments, $"{Auction}:{bidderId}",
            JsonSerializer.Serialize(new
            {
                auctionId = Auction, bidderId, amountMinorUnits = amount, method = "Payment"
            }, Json),
            eventType, _cts.Token);

    private Task ApproveAsync(decimal brokeragePercent) =>
        _events.PublishAsync(
            Topics.Upcoming, Auction.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = Auction, brokerageFeePercent = brokeragePercent
            }, Json),
            "AuctionApproved", _cts.Token);

    private Task AwardAsync(Guid winner, long amount) =>
        _events.PublishAsync(
            Topics.Lifecycle, Auction.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = Auction, winnerBidderId = winner, amountMinorUnits = amount
            }, Json),
            "AwardConfirmed", _cts.Token);

    private Task ReleaseAsync(Guid[] forfeit, Guid? appliedToPurchase) =>
        _events.PublishAsync(
            Topics.Deposits, Auction.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = Auction,
                forfeitForBidders = forfeit,
                appliedToPurchaseForBidder = appliedToPurchase
            }, Json),
            "DepositsReleasable", _cts.Token);

    /// <summary>«إغلاق للإلغاء», with or without the bidders' money going back.</summary>
    private Task CancelReleaseAsync(bool refund) =>
        _events.PublishAsync(
            Topics.Deposits, Auction.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = Auction,
                forfeitForBidders = Array.Empty<Guid>(),
                forfeitAll = !refund,
                refundBooklets = refund,
            }, Json),
            "DepositsReleasable", _cts.Token);

    private async Task<List<PaymentSettled>> AllSettlementsAsync()
    {
        var settled = new List<PaymentSettled>();
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        quiet.CancelAfter(TimeSpan.FromMilliseconds(150));

        try
        {
            await foreach (var record in _events.ReadAsync(Topics.Settlements, quiet.Token))
            {
                // The recovery marker rides this topic too, and carries no money.
                // Every real consumer has to skip it, so this one does as well.
                if (record.EventType != nameof(PaymentSettled)) continue;

                var one = JsonSerializer.Deserialize<PaymentSettled>(record.Payload, Json);
                if (one is not null) settled.Add(one);
                quiet.CancelAfter(TimeSpan.FromMilliseconds(80));
            }
        }
        catch (OperationCanceledException) { }

        return settled;
    }

    private async Task<PaymentSettled> SettlementAsync(Guid bidderId, string purpose)
    {
        PaymentSettled? found = null;

        await Until(async () =>
        {
            found = (await AllSettlementsAsync())
                .LastOrDefault(s => s.BidderId == bidderId && s.Purpose == purpose);
            return found is not null;
        }, $"a {purpose} settlement for {bidderId}");

        return found!;
    }

    private async Task Until(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(50, _cts.Token);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    private async Task Until(Func<Task<bool>> condition, string what)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(50, _cts.Token);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
