namespace EAuction.Notifications.Domain;

/// <summary>
/// What a bidder is being told. The kind drives the icon and the ordering in the
/// portal, and it is a string on the wire rather than an ordinal so a log line is
/// readable and a new kind cannot shift the meaning of an old row.
/// </summary>
public enum NotificationKind
{
    /// <summary>مؤهّل للمزايدة — the deposit settled and the catcher will now take your bids.</summary>
    Eligible = 0,

    /// <summary>أُلغي الاشتراك — eligibility was revoked.</summary>
    Revoked = 1,

    /// <summary>تعذّر الدفع — the gateway refused the booklet fee or the deposit.</summary>
    PaymentRefused = 2,

    /// <summary>بدأ المزاد — the auction you registered for is open.</summary>
    AuctionStarted = 3,

    /// <summary>
    /// تمت المزايدة عليك — someone beat your bid.
    ///
    /// The most time-critical thing this service sends, and the one most worth
    /// having: the live stream already tells a bidder who is watching, so this is
    /// for the one who closed the tab.
    /// </summary>
    Outbid = 4,

    /// <summary>أُغلق المزاد — bidding has ended.</summary>
    AuctionClosed = 5,

    /// <summary>تمت الترسية لك — you won, and here is your compliance deadline.</summary>
    Awarded = 6,

    /// <summary>أُلغيت الترسية — you were disqualified.</summary>
    Disqualified = 7,

    /// <summary>أُعيد التأمين / حُجز التأمين — the deposit was returned, kept, or applied.</summary>
    DepositResolved = 8,

    /// <summary>رُفض الضمان البنكي — with the reason staff gave; the bidder may submit another.</summary>
    GuaranteeRejected = 9,

    /// <summary>أُلغي المزاد — withdrawn before it opened; deposits go back.</summary>
    AuctionCancelled = 10,

    /// <summary>
    /// لم يُرسَ المزاد — it ended without an award: nobody met the reserve, the
    /// committee refused the result, or ended it after a disqualification. Without it
    /// a bidder's last word was «ستُعلن النتيجة» and no result ever came.
    /// </summary>
    AuctionUnsold = 11,
}

/// <summary>
/// One thing a bidder is told, once.
///
/// "Once" is the whole difficulty. Every topic this service reads is at-least-once,
/// and the compacted ones are replayed in full on every start — so without a
/// natural key, a restart would tell every bidder in the country again that they
/// are eligible, that they were outbid, and that they won. <see cref="Dedup"/> plus
/// the unique index in <c>NotificationsDbContext</c> is what makes that impossible
/// rather than unlikely.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid BidderId { get; private set; }
    public Guid AuctionId { get; private set; }
    public NotificationKind Kind { get; private set; }

    /// <summary>
    /// What makes this one distinct from another of the same kind for the same
    /// bidder and auction.
    ///
    /// Empty for the things that happen once — eligibility, the close, the award.
    /// For <see cref="NotificationKind.Outbid"/> it is the price that beat them, so
    /// being outbid four times produces four notices and a replay produces none.
    /// For a refusal it is the settlement's own timestamp, which is in the payload
    /// and therefore stable across replays.
    /// </summary>
    public string Dedup { get; private set; } = "";

    public string TitleAr { get; private set; } = "";
    public string BodyAr { get; private set; } = "";

    /// <summary>The auction, for a link. Null when the auction is not worth opening any more.</summary>
    public bool Actionable { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; private set; }

    /// <summary>When an outbound channel accepted it. Null while only the inbox has it.</summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    private Notification() { }

    public static Notification For(
        Guid bidderId, Guid auctionId, NotificationKind kind,
        string titleAr, string bodyAr, DateTimeOffset now,
        string dedup = "", bool actionable = true) =>
        new()
        {
            BidderId = bidderId,
            AuctionId = auctionId,
            Kind = kind,
            Dedup = dedup,
            TitleAr = titleAr,
            BodyAr = bodyAr,
            Actionable = actionable,
            CreatedAt = now,
        };

    /// <summary>Idempotent: a bidder pressing a notification twice is ordinary.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    public void MarkDispatched(DateTimeOffset now) => DispatchedAt ??= now;
}
