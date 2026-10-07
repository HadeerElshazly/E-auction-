namespace EAuction.Audit.Domain;

/// <summary>What happened without a member of staff doing it.</summary>
public static class SystemEventKinds
{
    /// <summary>A bidder became eligible — the deposit settled or the guarantee was accepted.</summary>
    public const string EligibilityGranted = "EligibilityGranted";

    /// <summary>A bidder stopped being eligible.</summary>
    public const string EligibilityWithdrawn = "EligibilityWithdrawn";

    /// <summary>Money moved, or the gateway refused it: a booklet, a deposit, a refund, a forfeit.</summary>
    public const string Payment = "Payment";

    /// <summary>The processor refused a bid the catcher had recorded, and why.</summary>
    public const string BidRejected = "BidRejected";
}

/// <summary>
/// «سجل المزايدات والإجراءات» (المرحلة الأولى، الخاصية 14), its second half: the
/// changes to eligibility and payments that the platform makes itself, read from
/// the topics the participant, payment and processor services already publish.
///
/// Deliberately a separate table from <see cref="AuditEntry"/>. That trail is
/// hash-chained over one topic so it can prove nothing was edited; mixing in
/// records from three other topics would make the chain's order depend on how
/// four consumers happened to interleave. This is a projection — reproducible
/// from the topics at any time — and is labelled as the system's, not staff's.
/// </summary>
public sealed class SystemEvent
{
    public long Id { get; private set; }

    /// <summary>Where it came from: topic, key and offset, unique together, so a replay adds nothing.</summary>
    public string Topic { get; private set; } = "";
    public string Key { get; private set; } = "";
    public long Offset { get; private set; }

    public string Kind { get; private set; } = "";
    public Guid? AuctionId { get; private set; }
    public Guid? BidderId { get; private set; }

    /// <summary>For a payment: "Booklet", "Deposit", "Brokerage".</summary>
    public string? Purpose { get; private set; }

    /// <summary>For a payment: "Charged", "Refused", "Refunded", "Forfeited", "AppliedToPurchase".</summary>
    public string? Outcome { get; private set; }

    public long? AmountMinorUnits { get; private set; }
    public string? Reference { get; private set; }

    /// <summary>Why — a gateway's refusal, or a bid's rejection reason.</summary>
    public string? Reason { get; private set; }

    /// <summary>For a rejected bid: which one, to join with the bid log.</summary>
    public Guid? ClientBidId { get; private set; }

    /// <summary>When it happened, as the source says; else when this service read it.</summary>
    public DateTimeOffset At { get; private set; }

    private SystemEvent() { }

    public static SystemEvent From(
        string topic, string key, long offset, string kind, DateTimeOffset at,
        Guid? auctionId = null, Guid? bidderId = null, string? purpose = null, string? outcome = null,
        long? amount = null, string? reference = null, string? reason = null, Guid? clientBidId = null) =>
        new()
        {
            Topic = topic, Key = key, Offset = offset, Kind = kind, At = at,
            AuctionId = auctionId, BidderId = bidderId, Purpose = purpose, Outcome = outcome,
            AmountMinorUnits = amount, Reference = reference, Reason = reason, ClientBidId = clientBidId,
        };
}
