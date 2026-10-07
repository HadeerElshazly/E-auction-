using System.Globalization;
using EAuction.Core;

namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// «يحفظ تصحيح السجل الإداري القيمة السابقة وتاريخ التغيير» (المرحلة الأولى،
/// الخاصية 14): an edit to an auction is recorded field by field, the value before
/// and the value after, so the trail answers "what was it" and not only "who
/// touched it". The date is the entry's own.
///
/// One exception, kept on purpose: the reserve price. Its figure is the one number
/// kept off every topic but the processor's (D-23); the trail says that it changed
/// and who changed it, never from what or to what.
/// </summary>
public static class AuctionChanges
{
    public sealed record Snapshot(
        string NameAr, string NameEn, BidChannel Channel, BidderVisibility Visibility,
        DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
        long Opening, long Reserve, long Increment, long Deposit, decimal Brokerage,
        long Booklet, int? QuietPeriodSeconds, int MaxExtensions, string? Phase);

    public static Snapshot Of(Auction a) => new(
        a.NameAr, a.NameEn, a.Channel, a.BidderVisibility, a.StartsAt, a.EndsAt,
        a.OpeningPriceMinorUnits, a.ReservePriceMinorUnits, a.MinIncrementMinorUnits,
        a.DepositMinorUnits, a.BrokerageFeePercent, a.BookletPriceMinorUnits,
        a.QuietPeriodSeconds, a.MaxExtensions, a.Phase);

    /// <summary>Each changed field as «الحقل: قبل ← بعد», joined; null when nothing changed.</summary>
    public static string? Describe(Snapshot before, Snapshot after)
    {
        var lines = new List<string>();
        void Field<T>(string label, T old, T now, Func<T, string> show)
        {
            if (!EqualityComparer<T>.Default.Equals(old, now))
                lines.Add($"{label}: {show(old)} ← {show(now)}");
        }

        Field("الاسم", before.NameAr, after.NameAr, Text);
        Field("الاسم بالإنجليزية", before.NameEn, after.NameEn, Text);
        Field("نوع المزاد", before.Channel, after.Channel, c => c == BidChannel.Onsite ? "حضوري" : "إلكتروني");
        Field("هوية المزايدين", before.Visibility, after.Visibility, v => v == BidderVisibility.Named ? "ظاهرة" : "مخفية");
        Field("البدء", before.StartsAt, after.StartsAt, Date);
        Field("الانتهاء", before.EndsAt, after.EndsAt, Date);
        Field("سعر الافتتاح", before.Opening, after.Opening, Money);
        Field("الحد الأدنى للزيادة", before.Increment, after.Increment, Money);
        Field("التأمين", before.Deposit, after.Deposit, Money);
        Field("نسبة السعي", before.Brokerage, after.Brokerage, p => $"{p.ToString(CultureInfo.InvariantCulture)}%");
        Field("سعر الكراسة", before.Booklet, after.Booklet, Money);
        Field("مدة التمديد", before.QuietPeriodSeconds, after.QuietPeriodSeconds,
            s => s is null ? "دون تمديد" : $"{s} ثانية");
        Field("أقصى عدد للتمديد", before.MaxExtensions, after.MaxExtensions, n => n.ToString(CultureInfo.InvariantCulture));
        Field("المرحلة", before.Phase, after.Phase, Text);

        if (before.Reserve != after.Reserve)
            // "reserve price was changed" stays in English as well: it is what the
            // tests and any existing search of the trail look for.
            lines.Add("الحد الأدنى للبيع: تغيّر (لا تُسجَّل القيمة) — The reserve price was changed (the figure is deliberately not recorded here).");

        return lines.Count == 0 ? null : string.Join("؛ ", lines);
    }

    public static string Money(long minorUnits) =>
        (minorUnits / 100m).ToString("#,0.00", CultureInfo.InvariantCulture) + " ر.س";

    private static string Text(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : $"«{s}»";

    private static readonly string[] HijriMonths =
    [
        "محرم", "صفر", "ربيع الأول", "ربيع الآخر", "جمادى الأولى", "جمادى الآخرة",
        "رجب", "شعبان", "رمضان", "شوال", "ذو القعدة", "ذو الحجة",
    ];

    private static readonly UmAlQuraCalendar Hijri = new();

    /// <summary>Hijri, Riyadh time — the calendar and clock the portals show.</summary>
    public static string Date(DateTimeOffset? at)
    {
        if (at is null) return "—";
        var riyadh = at.Value.ToOffset(TimeSpan.FromHours(3)).DateTime;
        try
        {
            return $"{Hijri.GetDayOfMonth(riyadh)} {HijriMonths[Hijri.GetMonth(riyadh) - 1]} "
                 + $"{Hijri.GetYear(riyadh)} هـ {riyadh:HH:mm}";
        }
        catch (ArgumentOutOfRangeException)
        {
            return riyadh.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
