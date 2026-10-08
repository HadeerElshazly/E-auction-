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
    private readonly List<PublicAttachment> _attachments = new();

    public string? CancellationReason { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Whether the cancellation returned the bidders' deposits and booklet fees. Null before this was recorded.</summary>
    public bool? CancellationRefunded { get; private set; }
    public IReadOnlyList<PublicAttachment> Attachments => _attachments;
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

    /// <summary>
    /// The clerk who runs this auction from the hall (§29). Null for an online
    /// auction, and null for an onsite one until somebody is put on the floor.
    /// </summary>
    public Guid? ClerkUserId { get; private set; }

    /// <summary>
    /// Which derivation of the clerk's signing key is current. Rotated the same way
    /// a bidder's is, and for the same reasons — a terminal left logged in, a clerk
    /// replaced mid-auction.
    /// </summary>
    public int ClerkKeyEpoch { get; private set; }

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

    /// <param name="minIncrementMinorUnits">
    /// «زيادة المزايدة», asked for up front: the amount each click of a bidder's
    /// raise adds. Optional — a draft without it is completed in the details, and
    /// approval still refuses one that is not set (see Validate).
    /// </param>
    public static Auction CreateDraft(
        Guid createdByUserId, string nameAr, string nameEn, long? minIncrementMinorUnits = null)
    {
        if (minIncrementMinorUnits is <= 0)
            throw new AuctionValidationException(new[] { "زيادة المزايدة يجب أن تكون أكبر من صفر." });
        return new()
        {
            CreatedByUserId = createdByUserId,
            NameAr = nameAr?.Trim() ?? "",
            NameEn = nameEn?.Trim() ?? "",
            MinIncrementMinorUnits = minIncrementMinorUnits ?? 0,
        };
    }

    /// <summary>
    /// «إعادة الطرح بسعر مخفّض»: the land of an auction that ended unsold, offered
    /// again as a new draft at a lower opening price — never below the reserve, which
    /// is exactly what the reserve is for. Everything else is carried over (plots,
    /// documents, terms); the schedule is not, because the old one is in the past,
    /// and the draft goes through review and approval like any other.
    ///
    /// A new auction rather than the old one reopened: the first run's bids, result
    /// and record stay exactly as they happened.
    /// </summary>
    public Auction Reoffer(Guid byUserId, long newOpeningPriceMinorUnits)
    {
        if (Status != AuctionStatus.Unsold)
            throw new InvalidAuctionTransitionException(Status, "re-offer");

        var problems = new List<string>();
        if (newOpeningPriceMinorUnits >= OpeningPriceMinorUnits)
            problems.Add("سعر الافتتاح الجديد يجب أن يكون أقل من السابق.");
        if (newOpeningPriceMinorUnits < ReservePriceMinorUnits)
            problems.Add("لا يمكن خفض سعر الافتتاح إلى ما دون السعر الاحتياطي.");
        if (problems.Count > 0) throw new AuctionValidationException(problems);

        var next = new Auction
        {
            CreatedByUserId = byUserId,
            NameAr = NameAr,
            NameEn = NameEn,
            Phase = Phase,
            Channel = Channel,
            BidderVisibility = BidderVisibility,
            OpeningPriceMinorUnits = newOpeningPriceMinorUnits,
            ReservePriceMinorUnits = ReservePriceMinorUnits,
            MinIncrementMinorUnits = MinIncrementMinorUnits,
            DepositMinorUnits = DepositMinorUnits,
            BrokerageFeePercent = BrokerageFeePercent,
            BookletPriceMinorUnits = BookletPriceMinorUnits,
            QuietPeriodSeconds = QuietPeriodSeconds,
            MaxExtensions = MaxExtensions,
            BookletDocumentId = BookletDocumentId,
            CoverImageDocumentId = CoverImageDocumentId,
        };
        foreach (var p in _plots)
            next._plots.Add(new Plot(
                next.Id, p.PlotNumber, p.AreaSqm, p.Latitude, p.Longitude, p.DescriptionAr, p.DescriptionEn,
                p.StreetWidthMeters, p.FrontageMeters, p.LandUse, p.Facing));
        foreach (var a in _attachments)
            next._attachments.Add(new PublicAttachment(a.DocumentId, a.TitleAr, a.Kind));
        return next;
    }

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

    /// <summary>
    /// The auction's land. One plot per auction: each plot is offered, bid on and
    /// awarded on its own. A second plot is a second auction.
    /// </summary>
    public void AddPlot(Plot plot)
    {
        RequireDraft("add a plot to");
        if (_plots.Count > 0)
            throw new AuctionValidationException(
                new[] { "المزاد لقطعة واحدة فقط. عدّل القطعة الحالية، أو أنشئ مزاداً آخر للقطعة الجديدة." });
        _plots.Add(plot);
    }

    /// <summary>«تعديل القطعة» — the auction's one plot, replaced while it is a draft.</summary>
    public void ReplacePlot(Plot plot)
    {
        RequireDraft("edit the plot of");
        _plots.Clear();
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

    /// <summary>A catalogue page is not a file share; this is a backstop, not a policy.</summary>
    public const int MaxAttachments = 20;

    public void AddAttachment(Guid documentId, string titleAr, string? kind = null)
    {
        if (kind is not (null or "Photo" or "Document"))
            throw new AuctionValidationException(new[] { "نوع المرفق يجب أن يكون صورة أو مستنداً." });
        RequireDraft("attach a public document to");

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(titleAr)) problems.Add("عنوان المرفق مطلوب.");
        else if (titleAr.Trim().Length > 200) problems.Add("عنوان المرفق طويل جداً.");
        if (_attachments.Count >= MaxAttachments)
            problems.Add($"الحد الأقصى {MaxAttachments} مرفقاً.");
        if (documentId == BookletDocumentId)
            // The booklet is Restricted; listing it publicly would advertise an id
            // that does not open, and suggest the paid document is free.
            problems.Add("كراسة الشروط لا تُضاف كمرفق عام.");
        if (problems.Count > 0) throw new AuctionValidationException(problems);

        if (_attachments.Any(a => a.DocumentId == documentId)) return;
        _attachments.Add(new PublicAttachment(documentId, titleAr.Trim(), kind));
    }

    public void RemoveAttachment(Guid documentId)
    {
        RequireDraft("remove a public document from");
        _attachments.RemoveAll(a => a.DocumentId == documentId);
    }

    /// <summary>Everything that must be true before anyone can approve this.</summary>
    public IReadOnlyList<string> Validate(DateTimeOffset now)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(NameAr)) problems.Add("اسم المزاد بالعربي مطلوب.");
        if (string.IsNullOrWhiteSpace(NameEn)) problems.Add("اسم المزاد بالإنجليزي مطلوب.");
        if (_plots.Count == 0) problems.Add("يجب إضافة القطعة.");
        else if (_plots.Count > 1) problems.Add("المزاد لقطعة واحدة فقط.");
        if (BookletDocumentId is null) problems.Add("كراسة الشروط مطلوبة.");

        if (StartsAt is null || EndsAt is null)
        {
            problems.Add("تاريخ ووقت بداية المزاد ونهايته مطلوبان.");
        }
        else
        {
            if (EndsAt <= StartsAt) problems.Add("وقت النهاية يجب أن يكون بعد وقت البداية.");
            if (StartsAt <= now) problems.Add("وقت البداية يجب أن يكون في المستقبل.");
        }

        if (OpeningPriceMinorUnits <= 0) problems.Add("سعر الافتتاح يجب أن يكون أكبر من صفر.");
        if (MinIncrementMinorUnits <= 0) problems.Add("أقل مزايدة يجب أن تكون أكبر من صفر.");
        if (DepositMinorUnits <= 0) problems.Add("مبلغ التأمين يجب أن يكون أكبر من صفر.");
        // Zero is allowed: the policy may make the booklet free, and a free booklet
        // is obtained without the gateway. Below zero is never a price.
        if (BookletPriceMinorUnits < 0) problems.Add("سعر الكراسة لا يمكن أن يكون سالباً.");

        if (ReservePriceMinorUnits <= 0)
        {
            problems.Add("السعر الاحتياطي يجب أن يكون أكبر من صفر.");
        }
        else if (ReservePriceMinorUnits > OpeningPriceMinorUnits)
        {
            // السعر الاحتياطي is the floor the price may be lowered to — the lowest
            // the municipality will re-offer the land at if it draws no bids at the
            // opening price (see Reoffer). So it cannot sit above the opening price.
            problems.Add("السعر الاحتياطي لا يمكن أن يزيد على سعر الافتتاح.");
        }

        if (BrokerageFeePercent is < 0 or > 100)
            problems.Add("نسبة السعي يجب أن تكون بين صفر ومئة بالمئة.");

        if (QuietPeriodSeconds is not null)
        {
            if (QuietPeriodSeconds <= 0) problems.Add("مدة الهدوء يجب أن تكون أكبر من صفر عند تفعيلها.");
            if (MaxExtensions < 1) problems.Add("عدد مرات التمديد يجب أن يكون واحدًا على الأقل عند تفعيل التمديد.");
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
            BrokerageFeePercent = BrokerageFeePercent,
            BookletDocumentId = BookletDocumentId,
            CoverImageDocumentId = CoverImageDocumentId,
            QuietPeriodSeconds = QuietPeriodSeconds,
            MaxExtensions = MaxExtensions,
            Channel = Channel.ToString(),
            BidderVisibility = BidderVisibility.ToString(),
            Phase = Phase,
            PlotCount = _plots.Count,
            TotalAreaSqm = TotalAreaSqm,
            Plots = _plots
                .OrderBy(p => p.PlotNumber, StringComparer.Ordinal)
                .Select(p => new PublicPlot(
                    p.Id, p.PlotNumber, p.AreaSqm,
                    p.Latitude, p.Longitude, p.DescriptionAr, p.DescriptionEn,
                    p.StreetWidthMeters, p.FrontageMeters, p.LandUse?.ToString(), p.Facing?.ToString()))
                .ToArray(),
            Attachments = _attachments
                .Select(a => new PublicDocument(a.DocumentId, a.TitleAr, a.Kind))
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

    /// <summary>
    /// How close to its start an auction can still be withdrawn. Inside this the
    /// processor may already be opening it, and a cancellation that raced the start
    /// would leave an auction taking bids that the register says is cancelled.
    /// </summary>
    public static readonly TimeSpan CancellationCutoff = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Withdraws an approved auction before it opens, with the reason on record.
    ///
    /// Not once it is live: bids are then on the log in order, and stopping an
    /// auction mid-bid is a decision about those bidders the first phase does not
    /// make. Before the start nobody has bid, so every deposit simply goes back.
    /// </summary>
    /// <param name="refund">
    /// Return the bidders' money — paid deposits and booklet fees through the gateway;
    /// bank guarantees are released by hand from «التسويات والإفراغ». Without it,
    /// every deposit is retained.
    /// </param>
    public void Cancel(string reason, Guid cancelledByUserId, DateTimeOffset now, bool refund = true)
    {
        // While bidding is under way too: an administrator may stop a running auction
        // outright — no award, every deposit back (see DepositsReleasable below).
        if (Status is not (AuctionStatus.Approved or AuctionStatus.Scheduled or AuctionStatus.Live))
            throw new InvalidAuctionTransitionException(Status, "cancel");

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(reason)) problems.Add("سبب الإلغاء مطلوب.");
        else if (reason.Trim().Length > 2000) problems.Add("سبب الإلغاء طويل جداً.");
        // The cut-off guards the moment of opening, when the processor may already
        // be announcing it; once it is live the cancellation is a decision about a
        // running auction and the processor stops it.
        if (Status != AuctionStatus.Live && StartsAt is { } starts && now >= starts - CancellationCutoff)
            problems.Add("لا يمكن إلغاء المزاد في الدقائق السابقة لبدئه مباشرة.");
        if (problems.Count > 0) throw new AuctionValidationException(problems);

        Status = AuctionStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancelledAt = now;
        CancellationRefunded = refund;

        _events.Add(new AuctionCancelled
        {
            AuctionId = Id,
            Reason = CancellationReason,
            CancelledByUserId = cancelledByUserId,
            At = now,
            Refund = refund,
        });

        // The sale itself is withdrawn, so no bidder is singled out: either every
        // bidder's money goes back — deposits and the booklet fees — or the
        // administrator keeps all of it.
        _events.Add(new DepositsReleasable
        {
            AuctionId = Id,
            ForfeitForBidders = [],
            ForfeitAll = !refund,
            RefundBooklets = refund,
        });
    }

    /// <summary>The relay confirms the auction reached auctions.upcoming.</summary>
    public void MarkScheduled()
    {
        // Cancelled before the relay confirmed publication: the cancellation went
        // out after the definition, so there is nothing to move forward.
        if (Status == AuctionStatus.Cancelled) return;
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

    // -- قاعة المزاد: the clerk on the floor (§29) --------------------------

    /// <summary>
    /// Puts a clerk on the floor of a hall auction.
    ///
    /// Operational rather than a term of sale, so unlike the dates and the deposit
    /// it is settable after approval — a clerk falls ill, a shift changes, and the
    /// auction cannot be re-approved to deal with it. It is refused once the hall
    /// has finished: an auction already closed has nobody left to enter bids for.
    /// </summary>
    public void AssignClerk(Guid clerkUserId)
    {
        if (Channel != BidChannel.Onsite)
            throw new AuctionValidationException(
                new[] { "Only an onsite auction has a clerk on the floor." });

        if (clerkUserId == Guid.Empty)
            throw new AuctionValidationException(new[] { "A clerk must be identified." });

        if (Status is not (AuctionStatus.Draft or AuctionStatus.Rejected
            or AuctionStatus.PendingReview or AuctionStatus.Approved
            or AuctionStatus.Scheduled or AuctionStatus.Live))
            throw new InvalidAuctionTransitionException(Status, "assign a clerk to");

        // A different person means a different key, or the outgoing clerk's
        // terminal could keep entering bids after they have been replaced.
        if (ClerkUserId is not null && ClerkUserId != clerkUserId) ClerkKeyEpoch++;

        ClerkUserId = clerkUserId;
        PublishClerkAssignment(assigned: true, clerkUserId);
    }

    /// <summary>Takes the clerk off the floor; their key stops verifying at once.</summary>
    public void UnassignClerk()
    {
        if (ClerkUserId is not { } outgoing) return;

        ClerkUserId = null;
        ClerkKeyEpoch++;
        PublishClerkAssignment(assigned: false, outgoing);
    }

    private void PublishClerkAssignment(bool assigned, Guid clerkUserId) =>
        _events.Add(new AuctionClerkAssigned
        {
            AuctionId = Id,
            ClerkUserId = clerkUserId,
            Assigned = assigned,
            KeyEpoch = ClerkKeyEpoch
        });

    /// <summary>
    /// The clerk moves the end time, because in the hall the auctioneer decides
    /// when bidding has stopped rather than a clock (§29).
    ///
    /// The cap itself lives in the engine, which holds the live extension count;
    /// this service does not and must not duplicate it. What is enforced here is
    /// only who may ask and in what state.
    /// </summary>
    public void ExtendByClerk(Guid clerkUserId, int seconds)
    {
        RequireClerkOnTheFloor(clerkUserId, "extend");

        if (seconds <= 0)
            throw new AuctionValidationException(
                new[] { "An extension must be a positive number of seconds." });

        _events.Add(new AuctionExtendedByClerk
        {
            AuctionId = Id,
            ClerkUserId = clerkUserId,
            ExtendBySeconds = seconds
        });
    }

    /// <summary>
    /// The hammer. The only way a hall auction ends — nothing in the processor
    /// closes one on a clock, because the room is still bidding until the
    /// auctioneer says otherwise.
    /// </summary>
    /// <summary>
    /// «إغلاق المزاد الآن»: ends a running auction early and keeps its result. The
    /// processor closes it and offers the highest valid bid as the candidate; the
    /// committee then decides, exactly as after a normal close. The status moves when
    /// the processor's close arrives, not here, so there is one source for "closed".
    /// </summary>
    public void CloseEarly(Guid closedByUserId, string reason, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Live)
            throw new InvalidAuctionTransitionException(Status, "close early");
        if (string.IsNullOrWhiteSpace(reason))
            throw new AuctionValidationException(new[] { "سبب الإغلاق مطلوب." });

        _events.Add(new AuctionClosedByAdmin
        {
            AuctionId = Id,
            ClosedByUserId = closedByUserId,
            Reason = reason.Trim(),
            At = now,
        });
    }

    public void CloseByClerk(Guid clerkUserId)
    {
        RequireClerkOnTheFloor(clerkUserId, "close");

        _events.Add(new AuctionClosedByClerk
        {
            AuctionId = Id,
            ClerkUserId = clerkUserId
        });
    }

    private void RequireClerkOnTheFloor(Guid clerkUserId, string action)
    {
        if (Channel != BidChannel.Onsite)
            throw new AuctionValidationException(
                new[] { $"Only an onsite auction's clerk can {action} it." });

        // Not merely "a clerk": this auction's clerk. A clerk running the hall next
        // door has a valid token and the operator role, and neither entitles them
        // to bring somebody else's hammer down.
        if (ClerkUserId is null || ClerkUserId != clerkUserId)
            throw new AuctionValidationException(
                new[] { "Only the clerk assigned to this auction can do that." });

        if (Status is not (AuctionStatus.Scheduled or AuctionStatus.Live))
            throw new InvalidAuctionTransitionException(Status, action);
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

        PendingCandidateBidderId = bidderId;
        PendingCandidateAmountMinorUnits = amountMinorUnits;

        // After a disqualification the next bidder is only a suggestion. The
        // requirements (الخاصية 08 and the scope decisions) rule out re-awarding
        // automatically when a winner defaults: the case goes to manual review, and
        // the committee decides whether to refer it down the ladder or end unsold.
        if (Status == AuctionStatus.WinnerDisqualified) return;

        Status = AuctionStatus.PendingAward;
    }

    /// <summary>
    /// The committee's decision, after review, to put the next bidder up for award.
    /// Only that: the award itself is still a separate confirmation, with its own
    /// letters and signature, exactly as the first one was.
    /// </summary>
    public void ReferToNextBidder()
    {
        if (Status != AuctionStatus.WinnerDisqualified)
            throw new InvalidAuctionTransitionException(Status, "refer to the next bidder for");
        if (PendingCandidateBidderId is null || PendingCandidateAmountMinorUnits is null)
            throw new AuctionValidationException(new[] { "لا يوجد مزايد تالٍ مؤهل لهذا المزاد." });

        Status = AuctionStatus.PendingAward;
        _events.Add(new NextBidderReferred
        {
            AuctionId = Id,
            BidderId = PendingCandidateBidderId.Value,
            AmountMinorUnits = PendingCandidateAmountMinorUnits.Value
        });
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
            ConfirmedAt = award.ConfirmedAt,
            ComplianceDeadline = award.ComplianceDeadline,
            CascadeStep = award.CascadeStep
        });
        RaiseFollowUp(award, now);

        return award;
    }

    public void GenerateAwardLetter(Guid documentId) => RequireOpenAward().AttachLetter(documentId);

    public void UploadSignedAwardLetter(Guid documentId, DateTimeOffset? now = null)
    {
        var award = RequireOpenAward();
        award.AttachSignedLetter(documentId);
        RaiseFollowUp(award, now ?? DateTimeOffset.UtcNow);
    }

    public void NotifyWinner(DateTimeOffset now)
    {
        var award = RequireOpenAward();
        award.MarkWinnerNotified(now);
        RaiseFollowUp(award, now);
    }

    /// <summary>
    /// The latest award's snapshot again, unchanged — see AwardSnapshotRepublisher.
    /// A withdrawn award is sent too, so its former winner is told it was withdrawn.
    /// </summary>
    public void RepublishFollowUp(DateTimeOffset now)
    {
        var latest = _awards.OrderByDescending(a => a.CascadeStep).FirstOrDefault();
        if (latest is not null) RaiseFollowUp(latest, now);
    }

    /// <summary>The winner's view of the award, after anything that changed it.</summary>
    private void RaiseFollowUp(Award award, DateTimeOffset now) =>
        _events.Add(new AwardFollowUpUpdated
        {
            AuctionId = Id,
            AwardId = award.Id,
            WinnerBidderId = award.BidderId,
            AmountMinorUnits = award.AmountMinorUnits,
            BrokerageMinorUnits = (long)Math.Round(award.AmountMinorUnits * BrokerageFeePercent / 100m),
            ConfirmedAt = award.ConfirmedAt,
            ComplianceDeadline = award.ComplianceDeadline,
            SignedLetterDocumentId = award.SignedLetterDocumentId,
            WinnerNotifiedAt = award.WinnerNotifiedAt,
            PaidMinorUnits = award.PaidMinorUnits,
            RemainingMinorUnits = award.RemainingMinorUnits,
            TransferStatus = award.TransferStatus.ToString(),
            TransferCompletedAt = award.TransferCompletedAt,
            SettledAt = award.SettledAt,
            DisqualifiedAt = award.DisqualifiedAt,
            At = now
        });

    /// <summary>
    /// The award the follow-up is about: the open one, or once settled the one that
    /// settled — its transfer to the notary usually comes after. Never a withdrawn one.
    /// </summary>
    public Award? FollowUpAward =>
        _awards.Where(a => a.DisqualifiedAt is null)
            .OrderByDescending(a => a.CascadeStep)
            .FirstOrDefault();

    public void RecordAwardPayment(
        long amountMinorUnits, DateTimeOffset paidOn, string reference, Guid? documentId,
        Guid recordedByUserId, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Awarded)
            throw new InvalidAuctionTransitionException(Status, "record a payment for");
        var award = RequireOpenAward();
        award.RecordReceipt(
            AwardReceiptKind.Payment, amountMinorUnits, paidOn, reference, documentId,
            recordedByUserId, now);
        RaiseFollowUp(award, now);
    }

    /// <summary>
    /// The winner's paid deposit, counted towards the price. Always the auction's
    /// deposit amount — the figure is not the clerk's to type.
    /// </summary>
    public void CreditDepositToAward(string reference, Guid recordedByUserId, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Awarded)
            throw new InvalidAuctionTransitionException(Status, "credit the deposit to");
        var award = RequireOpenAward();
        award.RecordReceipt(
            AwardReceiptKind.DepositCredit,
            Math.Min(DepositMinorUnits, award.RemainingMinorUnits),
            now, reference, null, recordedByUserId, now);
        RaiseFollowUp(award, now);
    }

    public void UpdateTransfer(
        TransferStatus status, string? reference, Guid? documentId, DateTimeOffset now)
    {
        if (Status is not (AuctionStatus.Awarded or AuctionStatus.Settled))
            throw new InvalidAuctionTransitionException(Status, "track the transfer of");
        var award = FollowUpAward
            ?? throw new InvalidOperationException("There is no award to transfer.");
        award.UpdateTransfer(status, reference, documentId, now);
        RaiseFollowUp(award, now);
    }

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
        RaiseFollowUp(award, now);

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
    /// <summary>Why the committee refused the preliminary result, when it did.</summary>
    public string? ResultRejectionReason { get; private set; }

    /// <summary>
    /// The committee refuses the preliminary result, with its reason (الخاصية 08).
    ///
    /// The auction is left unawarded, and deliberately not passed down the ladder:
    /// the requirements say a refusal must not award the next bidder automatically,
    /// so what happens to the land next is a decision outside this button.
    /// </summary>
    public void RejectResult(string reason)
    {
        if (Status != AuctionStatus.PendingAward)
            throw new InvalidAuctionTransitionException(Status, "reject the result of");
        if (string.IsNullOrWhiteSpace(reason))
            throw new AuctionValidationException(new[] { "سبب رفض النتيجة مطلوب." });

        ResultRejectionReason = reason.Trim();
        MarkUnsold();
    }

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
        RaiseFollowUp(award, now);

        // The sale, as a fact on its own rather than something to be read out of
        // the deposit event below. Reporting counts this as revenue (§35), and a
        // report that inferred the sale from money moving would disagree with the
        // register the first time the two diverged.
        _events.Add(new AuctionSettled
        {
            AuctionId = Id,
            WinnerBidderId = award.BidderId,
            AmountMinorUnits = award.AmountMinorUnits,
            At = now
        });

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
