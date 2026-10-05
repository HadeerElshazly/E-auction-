using EAuction.Core;

namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// The auction aggregate: auction data, files, dates, and the two workflows
/// from slide 6 — إعداد المزاد (prepare) and الترسية (award).
///
/// An auction sells 1..N plots as one indivisible package, and bidding is on
/// the package (D-02). Every state change that others need to know about
/// raises a domain event, which <c>AdminDbContext</c> writes to the outbox
/// inside the same transaction (D-16).
/// </summary>
public sealed class Auction
{
    private readonly List<Plot> _plots = new();
    private readonly List<Award> _awards = new();
    private readonly List<DomainEvent> _events = new();

    public Guid Id { get; private set; } = Guid.NewGuid();
    public AuctionStatus Status { get; private set; } = AuctionStatus.Draft;

    /// <summary>Grouping label — Phase 1 / Phase 2. Not a bidding concept.</summary>
    public string? Phase { get; private set; }

    public string NameAr { get; private set; } = "";
    public string NameEn { get; private set; } = "";
    public BidChannel Channel { get; private set; } = BidChannel.Online;

    /// <summary>
    /// Whether bidders are named to each other, or masked (D-22). Masked unless an
    /// administrator says otherwise, and settable only while this is a draft — a
    /// bidder who paid a deposit under one answer must not find it changed to the
    /// other.
    /// </summary>
    public BidderVisibility BidderVisibility { get; private set; } = BidderVisibility.Masked;

    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }

    public long OpeningPriceMinorUnits { get; private set; }

    /// <summary>
    /// السعر الاحتياطي. Secret (D-06): stored here because an admin enters it,
    /// but it leaves this service on one restricted topic only and must never
    /// appear in a public API response or report — not even when an auction
    /// fails to reach it.
    /// </summary>
    public long ReservePriceMinorUnits { get; private set; }

    public long MinIncrementMinorUnits { get; private set; }
    public long DepositMinorUnits { get; private set; }
    public decimal BrokerageFeePercent { get; private set; }
    public long BookletPriceMinorUnits { get; private set; }

    public int? QuietPeriodSeconds { get; private set; }
    public int MaxExtensions { get; private set; } = 3;

    // Deferred blind final round (§6.4). Nullable and unused — present from
    // the first migration so enabling it later is additive.
    public bool? BlindRoundEnabled { get; private set; }
    public int? BlindDurationSeconds { get; private set; }
    public int? MaxBlindRounds { get; private set; }

    /// <summary>كراسة الشروط والمواصفات.</summary>
    public Guid? BookletDocumentId { get; private set; }
    public Guid? CoverImageDocumentId { get; private set; }

    public string? RejectionReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; private set; }

    public IReadOnlyList<Plot> Plots => _plots;
    public IReadOnlyList<Award> Awards => _awards;
    public IReadOnlyList<DomainEvent> Events => _events;

    public Award? CurrentAward => _awards.SingleOrDefault(a => a.IsOpen);
    public decimal TotalAreaSqm => _plots.Sum(p => p.AreaSqm);

    private Auction() { }

    public static Auction CreateDraft(Guid createdByUserId, string nameAr, string nameEn) =>
        new()
        {
            CreatedByUserId = createdByUserId,
            NameAr = nameAr?.Trim() ?? "",
            NameEn = nameEn?.Trim() ?? ""
        };

    public void ClearEvents() => _events.Clear();

    // -- إعداد المزاد ------------------------------------------------------

    /// <summary>
    /// Edits are confined to Draft. Once approved the auction is public and
    /// bidders have relied on its terms, so changing the dates or the deposit
    /// underneath them is not an edit — it is a different auction.
    /// </summary>
    private void RequireDraft(string action)
    {
        if (Status is not (AuctionStatus.Draft or AuctionStatus.Rejected))
            throw new InvalidAuctionTransitionException(Status, action);
    }

    public void UpdateDetails(
        string nameAr, string nameEn, BidChannel channel, BidderVisibility bidderVisibility,
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        long openingPriceMinorUnits, long? reservePriceMinorUnits,
        long minIncrementMinorUnits, long depositMinorUnits,
        decimal brokerageFeePercent, long bookletPriceMinorUnits,
        int? quietPeriodSeconds, int maxExtensions, string? phase = null)
    {
        RequireDraft("edit");

        NameAr = nameAr?.Trim() ?? "";
        NameEn = nameEn?.Trim() ?? "";
        Channel = channel;
        BidderVisibility = bidderVisibility;
        StartsAt = startsAt;
        EndsAt = endsAt;
        OpeningPriceMinorUnits = openingPriceMinorUnits;

        // Null means "leave it as it is", not "set it to nothing".
        //
        // The reserve is the one field no read path ever returns — it lives on its
        // own restricted topic and is deliberately absent from AuctionResponse
        // (D-23). So an editor cannot show the current value, and a required field
        // would force whoever corrects a typo in the auction's name to retype the
        // reserve from a piece of paper, with a wrong figure silently replacing the
        // real one. Write-only, and optional on a write.
        if (reservePriceMinorUnits is not null)
            ReservePriceMinorUnits = reservePriceMinorUnits.Value;
        MinIncrementMinorUnits = minIncrementMinorUnits;
        DepositMinorUnits = depositMinorUnits;
        BrokerageFeePercent = brokerageFeePercent;
        BookletPriceMinorUnits = bookletPriceMinorUnits;
        QuietPeriodSeconds = quietPeriodSeconds;
        MaxExtensions = maxExtensions;
        Phase = phase;
    }

    public void AddPlot(Plot plot)
    {
        RequireDraft("add a plot to");
        if (_plots.Any(p => p.DeedNumber == plot.DeedNumber))
            throw new AuctionValidationException(
                new[] { $"Plot with deed number {plot.DeedNumber} is already in this auction." });
        _plots.Add(plot);
    }

    public void RemovePlot(Guid plotId)
    {
        RequireDraft("remove a plot from");
        _plots.RemoveAll(p => p.Id == plotId);
    }

    public void AttachBooklet(Guid documentId)
    {
        RequireDraft("attach a booklet to");
        BookletDocumentId = documentId;
    }

    public void AttachCoverImage(Guid documentId)
    {
        RequireDraft("attach a cover image to");
        CoverImageDocumentId = documentId;
    }

    /// <summary>Everything that must be true before anyone can approve this.</summary>
    public IReadOnlyList<string> Validate(DateTimeOffset now)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(NameAr)) problems.Add("Arabic name is required.");
        if (string.IsNullOrWhiteSpace(NameEn)) problems.Add("English name is required.");
        if (_plots.Count == 0) problems.Add("At least one plot is required.");
        if (BookletDocumentId is null) problems.Add("The terms booklet (كراسة الشروط) is required.");

        if (StartsAt is null || EndsAt is null)
        {
            problems.Add("Start and end date/time are required.");
        }
        else
        {
            if (EndsAt <= StartsAt) problems.Add("End must be after start.");
            if (StartsAt <= now) problems.Add("Start must be in the future.");
        }

        if (OpeningPriceMinorUnits <= 0) problems.Add("Opening price must be positive.");
        if (MinIncrementMinorUnits <= 0) problems.Add("Minimum increment must be positive.");
        if (DepositMinorUnits <= 0) problems.Add("Deposit (التأمين) must be positive.");

        if (ReservePriceMinorUnits <= 0)
        {
            problems.Add("Reserve price must be positive.");
        }
        else if (ReservePriceMinorUnits < OpeningPriceMinorUnits)
        {
            // A reserve below the opening price is met by the first valid bid,
            // so it does nothing. Almost always a data entry slip.
            problems.Add("Reserve price cannot be below the opening price.");
        }

        if (BrokerageFeePercent is < 0 or > 100)
            problems.Add("Brokerage fee must be between 0 and 100 percent.");

        if (QuietPeriodSeconds is not null)
        {
            if (QuietPeriodSeconds <= 0) problems.Add("Quiet period must be positive when set.");
            if (MaxExtensions < 1) problems.Add("Max extensions must be at least 1 when extension is enabled.");
        }

        return problems;
    }

    public void SubmitForReview(DateTimeOffset now)
    {
        RequireDraft("submit");
        var problems = Validate(now);
        if (problems.Count > 0) throw new AuctionValidationException(problems);

        Status = AuctionStatus.PendingReview;
        RejectionReason = null;
    }

    /// <summary>
    /// Approval. Emits two events deliberately: the public-safe definition and
    /// the reserve, on separate topics with separate ACLs, so the reserve is
    /// kept from the public read path by configuration rather than by care.
    /// </summary>
    public void Approve(DateTimeOffset now)
    {
        if (Status != AuctionStatus.PendingReview)
            throw new InvalidAuctionTransitionException(Status, "approve");

        var problems = Validate(now);
        if (problems.Count > 0) throw new AuctionValidationException(problems);

        Status = AuctionStatus.Approved;

        _events.Add(new AuctionApproved
        {
            AuctionId = Id,
            NameAr = NameAr,
            NameEn = NameEn,
            StartsAt = StartsAt!.Value,
            EndsAt = EndsAt!.Value,
            OpeningPriceMinorUnits = OpeningPriceMinorUnits,
            MinIncrementMinorUnits = MinIncrementMinorUnits,
            DepositMinorUnits = DepositMinorUnits,
            BookletPriceMinorUnits = BookletPriceMinorUnits,
            QuietPeriodSeconds = QuietPeriodSeconds,
            MaxExtensions = MaxExtensions,
            Channel = Channel.ToString(),
            BidderVisibility = BidderVisibility.ToString(),
            PlotCount = _plots.Count,
            TotalAreaSqm = TotalAreaSqm,
            Plots = _plots
                .OrderBy(p => p.DeedNumber, StringComparer.Ordinal)
                .Select(p => new PublicPlot(
                    p.Id, p.DeedNumber, p.AreaSqm,
                    p.Latitude, p.Longitude, p.DescriptionAr, p.DescriptionEn))
                .ToArray()
        });

        _events.Add(new AuctionReserveSet
        {
            AuctionId = Id,
            ReservePriceMinorUnits = ReservePriceMinorUnits
        });
    }

    public void Reject(string reason)
    {
        if (Status != AuctionStatus.PendingReview)
            throw new InvalidAuctionTransitionException(Status, "reject");
        if (string.IsNullOrWhiteSpace(reason))
            throw new AuctionValidationException(new[] { "A rejection reason is required." });

        Status = AuctionStatus.Rejected;
        RejectionReason = reason.Trim();
        _events.Add(new AuctionRejected { AuctionId = Id, Reason = RejectionReason });
    }

    /// <summary>The relay confirms the auction reached auctions.upcoming.</summary>
    public void MarkScheduled()
    {
        if (Status != AuctionStatus.Approved)
            throw new InvalidAuctionTransitionException(Status, "schedule");
        Status = AuctionStatus.Scheduled;
    }

    /// <summary>Lifecycle transitions driven by the processor's auctions.lifecycle stream.</summary>
    public void MarkLive()
    {
        if (Status != AuctionStatus.Scheduled)
            throw new InvalidAuctionTransitionException(Status, "start");
        Status = AuctionStatus.Live;
    }

    public void MarkClosing()
    {
        if (Status != AuctionStatus.Live)
            throw new InvalidAuctionTransitionException(Status, "close");
        Status = AuctionStatus.Closing;
    }

    public void MarkPendingEligibilityReview()
    {
        if (Status != AuctionStatus.Closing)
            throw new InvalidAuctionTransitionException(Status, "send to eligibility review");
        Status = AuctionStatus.PendingEligibilityReview;
    }

    // -- الترسية -----------------------------------------------------------

    /// <summary>
    /// The processor offers the highest remaining bidder that clears the
    /// reserve. The admin service never reads the ladder: the processor owns
    /// bid state, this service owns the human workflow.
    /// </summary>
    public void OfferCandidate(Guid bidderId, long amountMinorUnits)
    {
        if (Status is not (AuctionStatus.PendingEligibilityReview or AuctionStatus.WinnerDisqualified))
            throw new InvalidAuctionTransitionException(Status, "offer a candidate for");
        Status = AuctionStatus.PendingAward;
        PendingCandidateBidderId = bidderId;
        PendingCandidateAmountMinorUnits = amountMinorUnits;
    }

    public Guid? PendingCandidateBidderId { get; private set; }
    public long? PendingCandidateAmountMinorUnits { get; private set; }

    /// <summary>تأكيد ترسية العطاء — the committee's decision, never the system's.</summary>
    public Award ConfirmAward(Guid committeeUserId, DateTimeOffset now, TimeSpan complianceWindow)
    {
        if (Status != AuctionStatus.PendingAward)
            throw new InvalidAuctionTransitionException(Status, "confirm an award for");
        if (PendingCandidateBidderId is null || PendingCandidateAmountMinorUnits is null)
            throw new InvalidOperationException("No candidate has been offered for this auction.");

        var award = new Award(
            Id, PendingCandidateBidderId.Value, PendingCandidateAmountMinorUnits.Value,
            cascadeStep: _awards.Count, committeeUserId, now, complianceWindow);

        _awards.Add(award);
        Status = AuctionStatus.Awarded;
        PendingCandidateBidderId = null;
        PendingCandidateAmountMinorUnits = null;

        _events.Add(new AwardConfirmed
        {
            AuctionId = Id,
            AwardId = award.Id,
            WinnerBidderId = award.BidderId,
            AmountMinorUnits = award.AmountMinorUnits,
            ComplianceDeadline = award.ComplianceDeadline,
            CascadeStep = award.CascadeStep
        });

        return award;
    }

    public void GenerateAwardLetter(Guid documentId) => RequireOpenAward().AttachLetter(documentId);

    public void UploadSignedAwardLetter(Guid documentId) =>
        RequireOpenAward().AttachSignedLetter(documentId);

    public void NotifyWinner(DateTimeOffset now) => RequireOpenAward().MarkWinnerNotified(now);

    /// <summary>
    /// The winner turned out non-compliant. The award is withdrawn and the
    /// auction waits for the processor to offer the next candidate (§8.2).
    /// </summary>
    public void DisqualifyWinner(string reason, bool forfeitDeposit, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Awarded)
            throw new InvalidAuctionTransitionException(Status, "disqualify the winner of");
        if (string.IsNullOrWhiteSpace(reason))
            throw new AuctionValidationException(new[] { "A disqualification reason is required." });

        var award = RequireOpenAward();
        award.Disqualify(reason.Trim(), forfeitDeposit, now);
        Status = AuctionStatus.WinnerDisqualified;

        _events.Add(new WinnerDisqualified
        {
            AuctionId = Id,
            AwardId = award.Id,
            BidderId = award.BidderId,
            Reason = award.DisqualificationReason!,
            DepositForfeited = forfeitDeposit
        });
    }

    /// <summary>The ladder is exhausted, or nothing reached the reserve.</summary>
    public void MarkUnsold()
    {
        if (Status is not (AuctionStatus.PendingEligibilityReview
            or AuctionStatus.WinnerDisqualified or AuctionStatus.PendingAward))
            throw new InvalidAuctionTransitionException(Status, "mark unsold");

        Status = AuctionStatus.Unsold;
        _events.Add(new AuctionUnsold { AuctionId = Id });
        _events.Add(new DepositsReleasable
        {
            AuctionId = Id,
            ForfeitForBidders = ForfeitedBidders()
        });
    }

    public void Settle(DateTimeOffset now)
    {
        if (Status != AuctionStatus.Awarded)
            throw new InvalidAuctionTransitionException(Status, "settle");

        var award = RequireOpenAward();
        award.Settle(now);
        Status = AuctionStatus.Settled;

        // Only now is the award final, so only now can the deposits held for
        // the bidders below the winner be released (§8.3).
        _events.Add(new DepositsReleasable
        {
            AuctionId = Id,
            ForfeitForBidders = ForfeitedBidders(),
            AppliedToPurchaseForBidder = award.BidderId
        });
    }

    public void Close()
    {
        if (Status is not (AuctionStatus.Settled or AuctionStatus.Unsold))
            throw new InvalidAuctionTransitionException(Status, "close");
        Status = AuctionStatus.Closed;
    }

    private Guid[] ForfeitedBidders() =>
        _awards.Where(a => a.DepositForfeited).Select(a => a.BidderId).Distinct().ToArray();

    private Award RequireOpenAward() =>
        CurrentAward ?? throw new InvalidOperationException("This auction has no open award.");
}
