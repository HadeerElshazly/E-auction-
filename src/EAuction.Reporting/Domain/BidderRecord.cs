using EAuction.Reporting.Integration;

namespace EAuction.Reporting.Domain;

/// <summary>
/// One bidder's passage through one auction, as far as the control topics show it.
///
/// This is the participation funnel, and it is honest about where it stops. The
/// steps up to eligibility are all published — a booklet charged, a deposit
/// charged, eligibility granted — and so is winning. What is missing is the middle:
/// whether this bidder actually bid. Bids are binary frames on a per-auction topic
/// that nothing here consumes (D-12), so the count of bids is the processor's
/// per-auction total and never per-bidder. §35 states that gap rather than
/// inventing a column for it.
/// </summary>
public sealed class BidderRecord
{
    public Guid AuctionId { get; private set; }
    public Guid BidderId { get; private set; }

    /// <summary>
    /// The bidder's name, and only on an auction that names its bidders (D-22).
    ///
    /// Null on a masked auction because the topic carries null — not blanked here.
    /// A report of a masked auction therefore cannot name anyone, which is the
    /// setting working as intended rather than a missing feature.
    /// </summary>
    public string? DisplayNameAr { get; private set; }

    public DateTimeOffset? BookletPaidAt { get; private set; }
    public DateTimeOffset? DepositPaidAt { get; private set; }

    /// <summary>A refusal, which is the step a bidder most often falls out of the funnel at.</summary>
    public DateTimeOffset? PaymentRefusedAt { get; private set; }
    public string? PaymentRefusedReason { get; private set; }

    public DateTimeOffset? EligibleAt { get; private set; }

    /// <summary>
    /// Eligibility withdrawn — a revocation by staff, or a deposit that was never
    /// settled. Recorded separately from never having been eligible, because they
    /// are different stories about the same bidder.
    /// </summary>
    public DateTimeOffset? EligibilityEndedAt { get; private set; }

    public bool Won { get; private set; }
    public DateTimeOffset? DisqualifiedAt { get; private set; }
    public string? DisqualificationReason { get; private set; }
    public bool DepositForfeited { get; private set; }

    /// <summary>How much of the deposit is still with the municipality for this bidder.</summary>
    public long DepositHeldMinorUnits { get; private set; }

    private BidderRecord() { }

    public static BidderRecord For(Guid auctionId, Guid bidderId) =>
        new() { AuctionId = auctionId, BidderId = bidderId };

    public void Eligibility(EligibilityPayload e, DateTimeOffset at)
    {
        // Only ever set, never cleared: on a masked auction the topic sends null,
        // and an auction switched from named to masked should not retrospectively
        // erase a name the report already carried under the setting that allowed it.
        if (!string.IsNullOrWhiteSpace(e.DisplayNameAr)) DisplayNameAr = e.DisplayNameAr;

        if (e.Eligible)
        {
            EligibleAt ??= at;
            EligibilityEndedAt = null;
        }
        else if (EligibleAt is not null)
        {
            EligibilityEndedAt = at;
        }
    }

    /// <summary>
    /// Applies one settlement. Called in topic order, which is what makes a refund
    /// after a charge land the right way round.
    /// </summary>
    public void Settlement(PaymentSettledPayload p)
    {
        switch (p.Purpose, p.Outcome)
        {
            case (PaymentPurposes.Booklet, PaymentOutcomes.Charged):
                BookletPaidAt ??= p.At;
                break;

            case (PaymentPurposes.Deposit, PaymentOutcomes.Charged):
                DepositPaidAt ??= p.At;
                DepositHeldMinorUnits += p.AmountMinorUnits;
                break;

            case (PaymentPurposes.Deposit, PaymentOutcomes.Refunded):
                DepositHeldMinorUnits -= p.AmountMinorUnits;
                break;

            // Forfeited and AppliedToPurchase move no money at this instant — the
            // deposit was taken when it was paid. Both end the hold, and they are
            // kept apart because reporting a forfeiture as a refund would overstate
            // what was returned to bidders, and reporting the winner's applied
            // deposit as one would overstate it by the largest deposit in the
            // auction.
            case (PaymentPurposes.Deposit, PaymentOutcomes.Forfeited):
                DepositForfeited = true;
                DepositHeldMinorUnits -= p.AmountMinorUnits;
                break;

            case (PaymentPurposes.Deposit, PaymentOutcomes.AppliedToPurchase):
                DepositHeldMinorUnits -= p.AmountMinorUnits;
                break;

            case (_, PaymentOutcomes.Refused):
                PaymentRefusedAt = p.At;
                PaymentRefusedReason = p.FailureReason;
                break;
        }

        // Never below zero. A replay applies the same refund twice and the arithmetic
        // would otherwise report the municipality as owing money it holds.
        if (DepositHeldMinorUnits < 0) DepositHeldMinorUnits = 0;
    }

    public void WonIt() => Won = true;

    public void Disqualified(WinnerDisqualifiedPayload d, DateTimeOffset at)
    {
        Won = false;
        DisqualifiedAt ??= at;
        DisqualificationReason = d.Reason;
        if (d.DepositForfeited) DepositForfeited = true;
    }
}

/// <summary>Purpose names on <c>payments.settlements</c>, as this service matches them.</summary>
public static class PaymentPurposes
{
    public const string Booklet = nameof(Booklet);
    public const string Deposit = nameof(Deposit);
    public const string Brokerage = nameof(Brokerage);
}

/// <summary>Outcome names on <c>payments.settlements</c>, as this service matches them.</summary>
public static class PaymentOutcomes
{
    public const string Charged = nameof(Charged);
    public const string Refused = nameof(Refused);
    public const string Refunded = nameof(Refunded);
    public const string Forfeited = nameof(Forfeited);
    public const string AppliedToPurchase = nameof(AppliedToPurchase);
}

/// <summary>
/// One movement of money, kept verbatim.
///
/// The aggregates above are convenient; this is the ledger they are computed from,
/// and it exists because a revenue figure nobody can take apart is a revenue figure
/// nobody trusts. Every row in every money report traces to rows here.
/// </summary>
public sealed class SettlementRecord
{
    /// <summary>
    /// The record's offset on <c>payments.settlements</c>, and the primary key.
    ///
    /// The same device the audit trail uses, for the same reason: this topic is an
    /// event log that is replayed from the start on every restart (D-12), and a
    /// money ledger that double-counted a replay would report twice the revenue.
    /// With the offset as the key a replay is a unique violation this service
    /// swallows.
    /// </summary>
    public long Offset { get; private set; }

    public Guid AuctionId { get; private set; }
    public Guid BidderId { get; private set; }
    public string Purpose { get; private set; } = "";
    public string Outcome { get; private set; } = "";
    public long AmountMinorUnits { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset At { get; private set; }

    private SettlementRecord() { }

    public static SettlementRecord From(PaymentSettledPayload p, long offset) => new()
    {
        Offset = offset,
        AuctionId = p.AuctionId,
        BidderId = p.BidderId,
        Purpose = p.Purpose,
        Outcome = p.Outcome,
        AmountMinorUnits = p.AmountMinorUnits,
        FailureReason = p.FailureReason,
        At = p.At,
    };
}
