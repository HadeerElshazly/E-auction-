using System.Globalization;
using EAuction.Notifications.Domain;

namespace EAuction.Notifications.Integration;

/// <summary>
/// What each notification says, in Arabic.
///
/// Its own class, and every message a pure function of its inputs, so the wording
/// can be reviewed and tested without a Kafka broker. The portal renders these
/// verbatim: there is no second copy of the text in the front end to drift from
/// this one, and a bidder who later disputes what they were told is shown the row
/// that was stored rather than a template re-rendered by a newer build.
/// </summary>
public static class Messages
{
    /// <summary>
    /// Riyals, as a Saudi reader expects them.
    ///
    /// ar-SA rather than the invariant culture, which is why this project does not
    /// set InvariantGlobalization: ١٢٠٠٠٠٠٫٠٠ ر.س is the number, and 1200000.00 is
    /// a different document.
    /// </summary>
    public static string Riyals(long minorUnits) =>
        (minorUnits / 100m).ToString("C2", CultureInfo.GetCultureInfo("ar-SA"));

    /// <summary>
    /// A date in the Hijri calendar (Umm al-Qura, the official Saudi one), in Riyadh
    /// time, as the portals show it: «30 ربيع الآخر 1448 هـ».
    ///
    /// The month names are spelled out here rather than taken from the culture: the
    /// same ar-SA culture names the fourth month «ربيع الثاني» on Windows and «ربيع
    /// الآخر» under ICU, and a notice that disagrees with the screen it points to is
    /// a support call. These are the browser's names, so both say the same thing.
    /// </summary>
    private static string Date(DateTimeOffset at)
    {
        var riyadh = at.ToOffset(RiyadhOffset).DateTime;
        return $"{UmAlQura.GetDayOfMonth(riyadh)} {HijriMonths[UmAlQura.GetMonth(riyadh) - 1]} "
               + $"{UmAlQura.GetYear(riyadh)} هـ";
    }

    private static readonly TimeSpan RiyadhOffset = TimeSpan.FromHours(3);
    private static readonly UmAlQuraCalendar UmAlQura = new();

    private static readonly string[] HijriMonths =
    [
        "محرم", "صفر", "ربيع الأول", "ربيع الآخر", "جمادى الأولى", "جمادى الآخرة",
        "رجب", "شعبان", "رمضان", "شوال", "ذو القعدة", "ذو الحجة",
    ];

    public static (string Title, string Body) Eligible(string auction) =>
        ("مؤهّل للمزايدة",
         $"سُدّد التأمين وأصبحت مؤهّلاً للمزايدة في «{auction}».");

    public static (string Title, string Body) Revoked(string auction) =>
        ("أُلغي اشتراكك",
         $"أُلغي اشتراكك في «{auction}». لن تُقبل مزايداتك حتى يُعاد تأهيلك.");

    public static (string Title, string Body) PaymentRefused(
        string auction, string purpose, string? reason) =>
        ("تعذّر إتمام الدفع",
         $"لم تتم عملية الدفع {Purpose(purpose)} في «{auction}»"
         + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}.")
         + " يمكنك المحاولة مرة أخرى.");

    public static (string Title, string Body) AuctionStarted(string auction) =>
        ("بدأ المزاد",
         $"فُتح باب المزايدة في «{auction}».");

    public static (string Title, string Body) Outbid(string auction, long priceMinorUnits) =>
        ("تمت المزايدة عليك",
         $"لم تعد صاحب أعلى مزايدة في «{auction}». السعر الحالي {Riyals(priceMinorUnits)}.");

    public static (string Title, string Body) GuaranteeRejected(string auction, string reason) =>
        ("رُفض الضمان البنكي",
         $"رُفض الضمان البنكي المقدَّم لمزاد «{auction}». السبب: {reason}. يمكنك رفع ضمان آخر من صفحة المزاد.");

    public static (string Title, string Body) AuctionCancelled(string auction, string reason) =>
        ("أُلغي المزاد",
         $"أُلغي مزاد «{auction}» قبل بدئه. السبب: {reason}. يُرد التأمين المدفوع أو يُحرَّر الضمان البنكي.");

    public static (string Title, string Body) AuctionClosed(string auction) =>
        ("أُغلق المزاد",
         $"أُغلق باب المزايدة في «{auction}». ستُعلن النتيجة بعد اعتماد لجنة الترسية.");

    public static (string Title, string Body) Awarded(
        string auction, long amountMinorUnits, DateTimeOffset complianceDeadline) =>
        ("تمت الترسية لك",
         $"رُسي عليك «{auction}» بمبلغ {Riyals(amountMinorUnits)}. "
         + $"يجب إكمال الإجراءات قبل {Date(complianceDeadline)}.");

    public static (string Title, string Body) Disqualified(
        string auction, string reason, bool depositForfeited) =>
        ("أُلغيت الترسية",
         $"أُلغيت الترسية في «{auction}»: {reason}."
         + (depositForfeited ? " وقد حُجز مبلغ التأمين." : " وسيُعاد مبلغ التأمين."));

    public static (string Title, string Body) DepositReturned(string auction) =>
        ("أُعيد مبلغ التأمين",
         $"أُعيد مبلغ التأمين الخاص بـ«{auction}» إلى وسيلة الدفع التي سدّدت منها.");

    public static (string Title, string Body) DepositForfeited(string auction) =>
        ("حُجز مبلغ التأمين",
         $"حُجز مبلغ التأمين الخاص بـ«{auction}» لعدم إكمال إجراءات الترسية.");

    public static (string Title, string Body) DepositApplied(string auction) =>
        ("خُصم التأمين من الثمن",
         $"خُصم مبلغ التأمين الخاص بـ«{auction}» من ثمن الشراء.");

    /// <summary>
    /// An auction nobody told this service the name of.
    ///
    /// It happens: auctions.upcoming and the lifecycle topics are followed
    /// concurrently and a notification must not wait for a name. A message that
    /// says "a land auction" is worse than one that names it and far better than
    /// no message.
    /// </summary>
    public const string UnnamedAuction = "أحد المزادات";

    private static string Purpose(string purpose) => purpose switch
    {
        "Booklet" => "لرسوم كراسة الشروط",
        "Deposit" => "لمبلغ التأمين",
        "Brokerage" => "لمبلغ السعي",
        _ => "",
    };

    /// <summary>Whether opening the auction is still worth the bidder's time.</summary>
    public static bool Actionable(NotificationKind kind) => kind switch
    {
        NotificationKind.AuctionClosed => false,
        NotificationKind.DepositResolved => false,
        _ => true,
    };
}
