namespace EAuction.Reporting.Domain;

/// <summary>
/// Where an auction got to, as the reports read it.
///
/// Deliberately flatter and more generous than <c>AuctionStatus</c> in the auction
/// service: a report is about what happened, so "rejected before it ever opened"
/// and "closed with nobody above the reserve" are different outcomes even though
/// both are failures. The auction service's own statuses are a state machine; these
/// are the rows of a table somebody reads.
/// </summary>
public enum AuctionOutcome
{
    /// <summary>Approved, not yet opened.</summary>
    Scheduled = 0,

    /// <summary>Open for bidding.</summary>
    Live = 1,

    /// <summary>Bidding ended; the committee has not decided.</summary>
    Closed = 2,

    /// <summary>A candidate is in front of the committee.</summary>
    PendingAward = 3,

    /// <summary>The committee confirmed an award; the winner is inside their compliance window.</summary>
    Awarded = 4,

    /// <summary>البيع تم — the sale completed.</summary>
    Settled = 5,

    /// <summary>Nobody cleared the reserve, or the cascade ran out.</summary>
    Unsold = 6,

    /// <summary>Rejected at review and never published to bidders.</summary>
    Rejected = 7,
}

/// <summary>
/// One auction, as the reports see it.
///
/// Assembled from four topics by <see cref="Integration.ReportingConsumer"/> and
/// written nowhere else. Every field is a fact some service published; nothing here
/// is computed from a figure this service was not given — which is why there is no
/// reserve price and no "percentage of reserve achieved" (D-45).
/// </summary>
public sealed class AuctionRecord
{
    public Guid AuctionId { get; private set; }

    public string NameAr { get; private set; } = "";
    public string NameEn { get; private set; } = "";

    /// <summary>
    /// المخطط, e.g. "مخطط السعيد — المرحلة الأولى". The column almost every report
    /// groups by: a municipality asks what a phase raised, not what one auction did.
    /// </summary>
    public string? Phase { get; private set; }

    public string Channel { get; private set; } = "Online";
    public string BidderVisibility { get; private set; } = "Masked";

    public DateTimeOffset ScheduledStartsAt { get; private set; }
    public DateTimeOffset ScheduledEndsAt { get; private set; }

    public long OpeningPriceMinorUnits { get; private set; }
    public long MinIncrementMinorUnits { get; private set; }
    public long DepositMinorUnits { get; private set; }
    public long BookletPriceMinorUnits { get; private set; }
    public decimal BrokerageFeePercent { get; private set; }

    public int PlotCount { get; private set; }
    public decimal TotalAreaSqm { get; private set; }

    public AuctionOutcome Outcome { get; private set; } = AuctionOutcome.Scheduled;

    /// <summary>When the processor actually opened it, which is not always when it was scheduled to.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>The end time after quiet-period extensions, or after a clerk moved it.</summary>
    public DateTimeOffset? EffectiveEndsAt { get; private set; }

    public int ExtensionsUsed { get; private set; }

    /// <summary>
    /// Bids the processor judged, from <c>AuctionClosed</c>.
    ///
    /// The count the processor reports, not a count this service kept: the bids
    /// themselves are binary frames on a per-auction topic that nothing here
    /// consumes. §35 says what that costs — there is no "which bidders bid" column,
    /// because no control topic carries it.
    /// </summary>
    public int BidCount { get; private set; }

    public Guid? WinnerBidderId { get; private set; }

    /// <summary>
    /// السعر النهائي — what the auction sold for.
    ///
    /// The award's amount, not the last price seen on <c>auctions.current-winner</c>.
    /// They agree in the ordinary case and diverge exactly when it matters: after a
    /// disqualification the cascade awards to a lower bidder, and the price the
    /// municipality is paid is theirs, not the defaulter's.
    /// </summary>
    public long? FinalPriceMinorUnits { get; private set; }

    public DateTimeOffset? AwardedAt { get; private set; }
    public DateTimeOffset? ComplianceDeadline { get; private set; }

    /// <summary>How far down the ladder the award had to walk. 1 means the top bidder took it.</summary>
    public int CascadeStep { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }
    public DateTimeOffset? UnsoldAt { get; private set; }

    /// <summary>
    /// When the rejection was recorded.
    ///
    /// The time this service saw it, because <c>AuctionRejected</c> carries none —
    /// the same approximation as the eligibility dates, and §35 says so. It exists
    /// because a rejected auction has no <see cref="ClosedAt"/>: bidding never
    /// opened. Without a date of its own it fell out of every report with a `from`
    /// filter, which defeats the one thing recording it is for — "we prepared
    /// eleven auctions this quarter and rejected two".
    /// </summary>
    public DateTimeOffset? RejectedAt { get; private set; }

    public string? RejectionReason { get; private set; }

    public decimal PricePerSqmMinorUnits =>
        FinalPriceMinorUnits is null || TotalAreaSqm <= 0
            ? 0
            : Math.Round(FinalPriceMinorUnits.Value / TotalAreaSqm, 2);

    private AuctionRecord() { }

    public static AuctionRecord From(Integration.AuctionApprovedPayload a) => new()
    {
        AuctionId = a.AuctionId,
        NameAr = a.NameAr,
        NameEn = a.NameEn,
        Phase = a.Phase,
        Channel = a.Channel,
        BidderVisibility = a.BidderVisibility,
        ScheduledStartsAt = a.StartsAt,
        ScheduledEndsAt = a.EndsAt,
        OpeningPriceMinorUnits = a.OpeningPriceMinorUnits,
        MinIncrementMinorUnits = a.MinIncrementMinorUnits,
        DepositMinorUnits = a.DepositMinorUnits,
        BookletPriceMinorUnits = a.BookletPriceMinorUnits,
        BrokerageFeePercent = a.BrokerageFeePercent,
        PlotCount = a.PlotCount,
        TotalAreaSqm = a.TotalAreaSqm,
    };

    /// <summary>
    /// Re-applies the definition. <c>auctions.upcoming</c> is compacted and replayed
    /// in full on every start, so this runs again for every auction on every boot
    /// and must not disturb the outcome fields below.
    /// </summary>
    public void Redefine(Integration.AuctionApprovedPayload a)
    {
        NameAr = a.NameAr;
        NameEn = a.NameEn;
        Phase = a.Phase;
        Channel = a.Channel;
        BidderVisibility = a.BidderVisibility;
        ScheduledStartsAt = a.StartsAt;
        ScheduledEndsAt = a.EndsAt;
        OpeningPriceMinorUnits = a.OpeningPriceMinorUnits;
        MinIncrementMinorUnits = a.MinIncrementMinorUnits;
        DepositMinorUnits = a.DepositMinorUnits;
        BookletPriceMinorUnits = a.BookletPriceMinorUnits;
        BrokerageFeePercent = a.BrokerageFeePercent;
        PlotCount = a.PlotCount;
        TotalAreaSqm = a.TotalAreaSqm;
    }

    /// <summary>
    /// Advances the outcome, and never retreats it.
    ///
    /// The guard is the whole of this class's correctness. <c>auctions.lifecycle</c>
    /// is an event log replayed from offset 0 on every start, so a restart re-reads
    /// <c>AuctionStarted</c> for an auction that has since been settled — and
    /// without this, a report would say an auction that completed months ago is
    /// live. Rejected and Unsold are terminal in their own right, so they are
    /// ordered above the states they can arrive after.
    /// </summary>
    public void Reach(AuctionOutcome outcome)
    {
        if (outcome > Outcome) Outcome = outcome;
    }

    public void Started(DateTimeOffset at)
    {
        StartedAt ??= at;
        Reach(AuctionOutcome.Live);
    }

    public void Closed(Integration.AuctionClosedPayload c)
    {
        ClosedAt ??= c.At;
        EffectiveEndsAt = c.EffectiveEndsAt;

        // The maximum rather than the latest, for the same reason Reach exists: a
        // replay re-reads an earlier close for an auction a clerk later extended.
        ExtensionsUsed = Math.Max(ExtensionsUsed, c.ExtensionsUsed);
        BidCount = Math.Max(BidCount, c.BidCount);

        Reach(AuctionOutcome.Closed);
    }

    public void CandidateOffered() => Reach(AuctionOutcome.PendingAward);

    public void Awarded(Integration.AwardConfirmedPayload a)
    {
        // A settled sale is final. Nothing in the domain confirms an award after a
        // settlement, but this consumer replays the whole lifecycle log on every
        // start and the guard costs nothing.
        if (Outcome == AuctionOutcome.Settled) return;

        WinnerBidderId = a.WinnerBidderId;
        FinalPriceMinorUnits = a.AmountMinorUnits;
        ComplianceDeadline = a.ComplianceDeadline;
        CascadeStep = Math.Max(CascadeStep, a.CascadeStep);

        // The committee's own confirmation time, which the event now carries. It
        // used to be read off ComplianceDeadline — the deadline minus a window this
        // service does not know — so every award in every report would have been
        // dated five business days late.
        AwardedAt = a.ConfirmedAt;

        Reach(AuctionOutcome.Awarded);
    }

    /// <summary>
    /// The winner defaulted, so the award this record holds is no longer the sale.
    ///
    /// Cleared rather than kept, because a revenue report that counted a
    /// disqualified award would report money the municipality never received. What
    /// happens next is a cascade to a lower bidder (a second <c>AwardConfirmed</c>)
    /// or an exhausted ladder — and until one of those arrives, this auction has no
    /// price.
    /// </summary>
    public void WinnerDisqualified()
    {
        if (Outcome == AuctionOutcome.Settled) return;

        WinnerBidderId = null;
        FinalPriceMinorUnits = null;
        AwardedAt = null;
        ComplianceDeadline = null;

        // Back to Closed is the one retreat that is correct, and it is done here
        // rather than through Reach for exactly that reason. A settled auction
        // cannot be disqualified, so there is nothing to lose.
        if (Outcome == AuctionOutcome.Awarded) Outcome = AuctionOutcome.Closed;
    }

    public void Settled(Integration.AuctionSettledPayload s)
    {
        WinnerBidderId = s.WinnerBidderId;
        FinalPriceMinorUnits = s.AmountMinorUnits;
        SettledAt ??= s.At;
        Reach(AuctionOutcome.Settled);
    }

    public void Unsold(DateTimeOffset at)
    {
        // The one place the enum's ordering would otherwise bite: Unsold sorts above
        // Settled so a cascade that exhausts after a disqualification reaches it,
        // which means a replayed LadderExhausted could otherwise erase a completed
        // sale's price.
        if (Outcome == AuctionOutcome.Settled) return;

        UnsoldAt ??= at;
        FinalPriceMinorUnits = null;
        WinnerBidderId = null;
        Reach(AuctionOutcome.Unsold);
    }

    public void Rejected(string reason, DateTimeOffset at)
    {
        RejectionReason = reason;
        RejectedAt ??= at;
        Reach(AuctionOutcome.Rejected);
    }

    /// <summary>
    /// A date for an auction that was rejected before it was ever published, so it
    /// is not sorted to the beginning of time and filtered out of every report.
    /// </summary>
    public void ScheduleUnknown(DateTimeOffset at) => ScheduledStartsAt = at;
}

/// <summary>
/// One plot, and which auction's package it was in.
///
/// Flat rather than nested under the auction, because the plot inventory report is
/// the one a municipality opens most: how much of مخطط السعيد is sold, how much is
/// left, and at what price per square metre.
/// </summary>
public sealed class PlotRecord
{
    public Guid PlotId { get; private set; }
    public Guid AuctionId { get; private set; }

    public string DeedNumber { get; private set; } = "";
    public decimal AreaSqm { get; private set; }
    public string? Latitude { get; private set; }
    public string? Longitude { get; private set; }
    public string? DescriptionAr { get; private set; }

    private PlotRecord() { }

    public static PlotRecord From(Guid auctionId, Integration.PlotPayload p) => new()
    {
        PlotId = p.Id,
        AuctionId = auctionId,
        DeedNumber = p.DeedNumber,
        AreaSqm = p.AreaSqm,
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        DescriptionAr = p.DescriptionAr,
    };

    public void Redefine(Integration.PlotPayload p)
    {
        DeedNumber = p.DeedNumber;
        AreaSqm = p.AreaSqm;
        Latitude = p.Latitude;
        Longitude = p.Longitude;
        DescriptionAr = p.DescriptionAr;
    }
}
