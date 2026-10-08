namespace EAuction.Payments;

/// <summary>
/// What a bidder is being charged for. Four kinds of money move through a land
/// auction and they behave differently, so they are not one amount with a label.
/// </summary>
public enum PaymentPurpose
{
    /// <summary>
    /// كراسة الشروط — the terms booklet. Small, paid to look, and not refunded —
    /// except when the municipality cancels the auction and chooses to return it.
    /// </summary>
    Booklet = 0,

    /// <summary>التأمين — the deposit. Large, and refunded unless the bidder defaults.</summary>
    Deposit = 1,

    /// <summary>مبلغ السعي — brokerage, a percentage of the final price, charged on award.</summary>
    Brokerage = 2,
}

/// <summary>An instruction to take money, with everything the gateway needs.</summary>
public sealed record PaymentInstruction
{
    public required Guid AuctionId { get; init; }
    public required Guid BidderId { get; init; }
    public required PaymentPurpose Purpose { get; init; }
    public required long AmountMinorUnits { get; init; }

    /// <summary>"Payment" or "BankGuarantee", as the bidder chose. Informational here.</summary>
    public string Method { get; init; } = "Payment";

    /// <summary>
    /// The key that makes charging safe to retry.
    ///
    /// Everything upstream of this service is at-least-once, so the same request
    /// arrives more than once as a matter of course. A gateway that cannot be told
    /// "this is the same charge you already took" turns ordinary redelivery into
    /// double-charging a citizen for a land deposit, and this service cannot
    /// prevent that on its own: it can be sure it did not *publish* twice, but a
    /// crash between the charge and the publish leaves no local trace of either.
    ///
    /// Both PayTabs and SADAD support a merchant-supplied reference for exactly
    /// this. An adapter that drops it is not finished.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>What the gateway did, as this service records it.</summary>
public sealed record PaymentOutcome(bool Settled, string Reference, string? FailureReason = null)
{
    public static PaymentOutcome Ok(string reference) => new(true, reference);

    public static PaymentOutcome Refused(string reason) => new(false, "", reason);
}

/// <summary>
/// The seam the real gateways sit behind (D-17: external integrations at the edge).
///
/// PayTabs and SADAD are named in the proposal and neither can be implemented here:
/// both need merchant accounts, credentials and a sandbox, and P-3 records that the
/// contract for them is still open. What can be built without them is this — the
/// shape of the conversation, the idempotency contract, and everything upstream
/// that has to be right whichever gateway ends up behind it.
///
/// So the simulator is not a placeholder for missing work. It is the thing that
/// lets the deposit, the refund and the forfeiture be exercised end to end now,
/// and the adapters drop in without the auction domain noticing.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Takes money. Must be safe to call twice with the same key.</summary>
    Task<PaymentOutcome> ChargeAsync(PaymentInstruction instruction, CancellationToken ct);

    /// <summary>
    /// Gives it back. <paramref name="originalReference"/> is what
    /// <see cref="ChargeAsync"/> returned — a refund is always against a charge,
    /// never a free-standing payment to a person.
    /// </summary>
    Task<PaymentOutcome> RefundAsync(
        PaymentInstruction instruction, string originalReference, CancellationToken ct);
}
