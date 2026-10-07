using System.Collections.Concurrent;

namespace EAuction.Payments;

/// <summary>
/// What the gateway did, as a sandbox operator needs to see it.
/// </summary>
/// <param name="At">When this service asked the gateway, not when a bank moved money.</param>
public sealed record SandboxInstruction(
    DateTimeOffset At,
    Guid AuctionId,
    Guid BidderId,
    PaymentPurpose Purpose,
    long AmountMinorUnits,
    string Method,
    string IdempotencyKey,
    bool Settled,
    string Reference,
    string? FailureReason,
    bool Replayed);

/// <summary>
/// <see cref="SimulatedGateway"/> with a switch on the front and a window on the
/// side, for demonstrating the platform to people.
///
/// It exists because of what the simulator does right: it settles everything
/// instantly and invisibly. That is correct for a test and useless in a room — the
/// deposit is the moment a citizen becomes eligible to bid on state land, and in a
/// demonstration it passes with nothing on screen at all. Worse, the one path that
/// most needs showing is the one nobody can trigger on purpose: a bidder who does
/// not pay, is never eligible, and is disqualified (C-2).
///
/// So this adds exactly two things and no cleverness:
///
/// **A record of what was asked.** The last <see cref="Capacity"/> instructions with
/// their outcome, so the booklet fee and the deposit are visible as they happen,
/// with the reference the bidder would quote.
///
/// **A switch.** <see cref="DeclineCharges"/> makes the next charge fail, so the
/// non-payment path is reachable deliberately rather than by knowing that an amount
/// ending in ٠٫١٣ is refused.
///
/// Charges only. A refund always passes through: a sandbox that could refuse to
/// give a losing bidder their deposit back would be demonstrating a defect rather
/// than a path, and the forfeiture case — where the money is deliberately not
/// returned — is a decision the auction domain makes upstream, not the gateway.
///
/// It delegates rather than reimplements, so the idempotency contract and the
/// reference shape are the simulator's and cannot drift from what the tests pin.
/// </summary>
public sealed class SandboxGateway(SimulatedGateway? inner = null) : IPaymentGateway
{
    /// <summary>
    /// How many instructions are kept. A demonstration is a few dozen actions; this
    /// is bounded because an unbounded list on a long-running service is a leak
    /// whatever it is holding.
    /// </summary>
    public const int Capacity = 200;

    private readonly SimulatedGateway _inner = inner ?? new SimulatedGateway();
    private readonly ConcurrentQueue<SandboxInstruction> _seen = new();

    /// <summary>
    /// Whether the next charge is refused, and why.
    ///
    /// Set before the bidder pays, not while they are paying: this stands in for a
    /// card that will be declined, and a real gateway decides at the moment of the
    /// charge. The operator flips it, then walks through the flow.
    /// </summary>
    public bool DeclineCharges { get; set; }

    /// <summary>
    /// The reason a refused charge reports. A gateway's own vocabulary, because this
    /// string reaches the bidder's screen and the audit trail.
    /// </summary>
    public string DeclineReason { get; set; } = "InsufficientFunds";

    /// <summary>Newest first, which is the order a screen wants to show them.</summary>
    public IReadOnlyList<SandboxInstruction> Recent => _seen.Reverse().ToArray();

    public async Task<PaymentOutcome> ChargeAsync(
        PaymentInstruction instruction, CancellationToken ct)
    {
        // Asked before charging, so a redelivery is labelled as one on screen. A
        // demonstration that showed the same deposit being taken three times —
        // because Kafka redelivered it three times — would be alarming and wrong:
        // the charge happens once and this is how that is visible.
        var replayed = _inner.HasCharged(instruction.IdempotencyKey);

        var outcome = DeclineCharges
            ? PaymentOutcome.Refused(DeclineReason)
            : await _inner.ChargeAsync(instruction, ct);

        Record(instruction, outcome, replayed);
        return outcome;
    }

    public async Task<PaymentOutcome> RefundAsync(
        PaymentInstruction instruction, string originalReference, CancellationToken ct)
    {
        var outcome = await _inner.RefundAsync(instruction, originalReference, ct);
        Record(instruction, outcome, replayed: false);
        return outcome;
    }

    private void Record(
        PaymentInstruction instruction, PaymentOutcome outcome, bool replayed)
    {
        _seen.Enqueue(new SandboxInstruction(
            DateTimeOffset.UtcNow,
            instruction.AuctionId,
            instruction.BidderId,
            instruction.Purpose,
            instruction.AmountMinorUnits,
            instruction.Method,
            instruction.IdempotencyKey,
            outcome.Settled,
            outcome.Reference,
            outcome.FailureReason,
            replayed));

        while (_seen.Count > Capacity) _seen.TryDequeue(out _);
    }
}
