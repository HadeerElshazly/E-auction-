using EAuction.Payments;
using Xunit;

namespace EAuction.Payments.Tests;

/// <summary>
/// The console over the simulator.
///
/// Two things are worth pinning. The switch has to actually reach the charge, or a
/// demonstration of the non-payment path quietly shows a payment succeeding. And
/// the switch must not reach a refund: a sandbox that could withhold a losing
/// bidder's deposit would be demonstrating a defect rather than a path, and
/// forfeiture — the case where the money is deliberately kept — is decided upstream
/// in the auction domain, not here.
/// </summary>
public class SandboxGatewayTests
{
    private static PaymentInstruction Instruction(
        PaymentPurpose purpose = PaymentPurpose.Deposit,
        long amount = 100_000_00,
        string? key = null) => new()
    {
        AuctionId = Guid.NewGuid(),
        BidderId = Guid.NewGuid(),
        Purpose = purpose,
        AmountMinorUnits = amount,
        IdempotencyKey = key ?? Guid.NewGuid().ToString(),
    };

    [Fact]
    public async Task It_settles_and_records_what_it_settled()
    {
        var gateway = new SandboxGateway();
        var instruction = Instruction(PaymentPurpose.Booklet, 500_00);

        var outcome = await gateway.ChargeAsync(instruction, default);

        Assert.True(outcome.Settled);

        var recorded = Assert.Single(gateway.Recent);
        Assert.Equal(PaymentPurpose.Booklet, recorded.Purpose);
        Assert.Equal(500_00, recorded.AmountMinorUnits);
        Assert.Equal(outcome.Reference, recorded.Reference);
        Assert.True(recorded.Settled);
        Assert.False(recorded.Replayed);
    }

    [Fact]
    public async Task The_switch_refuses_the_charge_with_the_reason_it_was_given()
    {
        var gateway = new SandboxGateway { DeclineCharges = true, DeclineReason = "CardDeclined" };

        var outcome = await gateway.ChargeAsync(Instruction(), default);

        Assert.False(outcome.Settled);
        Assert.Equal("CardDeclined", outcome.FailureReason);
        Assert.Equal("CardDeclined", Assert.Single(gateway.Recent).FailureReason);
    }

    [Fact]
    public async Task A_refund_is_paid_even_while_charges_are_being_refused()
    {
        var gateway = new SandboxGateway();
        var deposit = Instruction();

        var charge = await gateway.ChargeAsync(deposit, default);
        gateway.DeclineCharges = true;

        var refund = await gateway.RefundAsync(
            Instruction(key: deposit.IdempotencyKey + ":refund"), charge.Reference, default);

        Assert.True(refund.Settled);
    }

    [Fact]
    public async Task A_refused_charge_can_succeed_once_the_switch_is_flipped_back()
    {
        // A declined card is retryable, and the simulator only remembers charges that
        // succeeded. If a refusal were cached, flipping the switch back would leave
        // the bidder permanently unable to pay and the sandbox unusable after one
        // demonstration of the failure path.
        var gateway = new SandboxGateway { DeclineCharges = true };
        var instruction = Instruction();

        Assert.False((await gateway.ChargeAsync(instruction, default)).Settled);

        gateway.DeclineCharges = false;

        Assert.True((await gateway.ChargeAsync(instruction, default)).Settled);
    }

    [Fact]
    public async Task The_same_instruction_twice_is_charged_once_and_marked_as_replayed()
    {
        // Everything upstream is at-least-once, so this happens as a matter of
        // course. The screen has to say "this is the same charge" rather than show
        // what looks like a second hundred thousand riyals off a citizen.
        var gateway = new SandboxGateway();
        var instruction = Instruction();

        var first = await gateway.ChargeAsync(instruction, default);
        var second = await gateway.ChargeAsync(instruction, default);

        Assert.Equal(first.Reference, second.Reference);

        var recent = gateway.Recent;
        Assert.Equal(2, recent.Count);
        Assert.True(recent[0].Replayed);   // newest first
        Assert.False(recent[1].Replayed);
    }

    [Fact]
    public async Task The_newest_instruction_is_first()
    {
        var gateway = new SandboxGateway();

        await gateway.ChargeAsync(Instruction(PaymentPurpose.Booklet, 500_00), default);
        await gateway.ChargeAsync(Instruction(PaymentPurpose.Deposit, 100_000_00), default);

        Assert.Equal(PaymentPurpose.Deposit, gateway.Recent[0].Purpose);
    }

    [Fact]
    public async Task It_does_not_grow_without_bound()
    {
        // This service runs for as long as the stack is up. An unbounded list of
        // every instruction it ever saw is a leak whatever it is holding.
        var gateway = new SandboxGateway();

        for (var i = 0; i < SandboxGateway.Capacity + 25; i++)
            await gateway.ChargeAsync(Instruction(amount: 1_00 + i), default);

        Assert.Equal(SandboxGateway.Capacity, gateway.Recent.Count);

        // The newest survived, so what was dropped is the oldest.
        Assert.Equal(1_00 + SandboxGateway.Capacity + 24, gateway.Recent[0].AmountMinorUnits);
    }

    [Fact]
    public async Task It_keeps_the_simulators_own_refusal_lever()
    {
        // The amount ending in .13 is how the test suite asks for a refusal without
        // reaching into the gateway. The console must not have taken that away.
        var gateway = new SandboxGateway();

        var outcome = await gateway.ChargeAsync(Instruction(amount: 100_000_13), default);

        Assert.False(outcome.Settled);
        Assert.Equal("InsufficientFunds", outcome.FailureReason);
    }
}
