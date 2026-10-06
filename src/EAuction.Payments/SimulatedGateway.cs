using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EAuction.Payments;

/// <summary>
/// Stands in for PayTabs and SADAD until the merchant accounts exist (P-3).
///
/// It settles, and it is honest about the two things a real gateway does that a
/// naive stub would not:
///
/// **It honours the idempotency key.** The same key returns the same reference and
/// takes no second payment, because that is the contract this service relies on to
/// be safe under redelivery. A simulator that charged twice would hide exactly the
/// bug the key exists to prevent.
///
/// **It can refuse.** A gateway that always says yes makes the failure path
/// unreachable, and the failure path is the one where a bidder is left awaiting a
/// deposit they think they paid. <see cref="RefuseAmountsEndingIn"/> drives it from
/// the amount, so a test can ask for a refusal without reaching in here.
///
/// What it is not: a queue, a timeout, a 3-D Secure redirect, or a settlement that
/// arrives tomorrow. Those are real and they are the adapter's problem.
/// </summary>
public sealed class SimulatedGateway : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, string> _charged = new();
    private readonly ConcurrentDictionary<string, string> _refunded = new();

    /// <summary>
    /// Amounts whose last two digits (the halalas) make the gateway refuse.
    ///
    /// Picked rather than random so a refusal is reproducible: a test asks for
    /// 100,000.13 and gets a refusal every time, on any machine.
    /// </summary>
    public int? RefuseAmountsEndingIn { get; init; } = 13;

    public int Charges => _charged.Count;

    public Task<PaymentOutcome> ChargeAsync(PaymentInstruction instruction, CancellationToken ct)
    {
        if (instruction.AmountMinorUnits <= 0)
            return Task.FromResult(PaymentOutcome.Refused("The amount must be positive."));

        // Replayed instruction: the same answer, and no second charge.
        if (_charged.TryGetValue(instruction.IdempotencyKey, out var existing))
            return Task.FromResult(PaymentOutcome.Ok(existing));

        if (RefuseAmountsEndingIn is { } refuse
            && instruction.AmountMinorUnits % 100 == refuse)
        {
            return Task.FromResult(PaymentOutcome.Refused("InsufficientFunds"));
        }

        var reference = Reference("SIM", instruction);
        _charged[instruction.IdempotencyKey] = reference;
        return Task.FromResult(PaymentOutcome.Ok(reference));
    }

    public Task<PaymentOutcome> RefundAsync(
        PaymentInstruction instruction, string originalReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originalReference))
            return Task.FromResult(PaymentOutcome.Refused("Nothing was charged to refund."));

        if (_refunded.TryGetValue(instruction.IdempotencyKey, out var existing))
            return Task.FromResult(PaymentOutcome.Ok(existing));

        var reference = Reference("RFND", instruction);
        _refunded[instruction.IdempotencyKey] = reference;
        return Task.FromResult(PaymentOutcome.Ok(reference));
    }

    /// <summary>
    /// A reference in the shape a gateway returns: a prefix and an opaque tail.
    ///
    /// Random rather than derived from the auction and bidder, because a reference
    /// that encodes who paid what is a reference that leaks it — they end up in
    /// emails, bank statements and support tickets.
    /// </summary>
    private static string Reference(string prefix, PaymentInstruction instruction) =>
        $"{prefix}-{instruction.Purpose.ToString().ToUpperInvariant()[..3]}-"
        + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
}
