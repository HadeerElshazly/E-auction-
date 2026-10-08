namespace EAuction.Core;

/// <summary>
/// What a visitor who has not signed in may see of an auction — «إعدادات العرض للزوار».
///
/// A visitor always sees the published auctions and opens any of them: its name and
/// status, the land itself (plots, areas, use, location), the deposit it takes to
/// take part, the cover photo, and whether the booklet is free. Those are not
/// settings. Everything else is a group an administrator puts in front of the
/// sign-in or leaves open, and the query service removes a hidden group from what
/// it answers an anonymous caller — the page is not trusted to hide it.
///
/// Set in the admin service, carried on <see cref="Topics.Settings"/>, applied by the
/// query service. One list here so both sides name the groups alike.
/// </summary>
public static class PublicFields
{
    /// <summary>مواعيد المزاد — when it opens and closes, and the countdowns.</summary>
    public const string Schedule = "schedule";

    /// <summary>سعر الافتتاح وزيادة المزايدة.</summary>
    public const string OpeningPrice = "openingPrice";

    /// <summary>السعر الحالي — the live price, the next minimum bid, the leader and the bid count.</summary>
    public const string LivePrice = "livePrice";

    /// <summary>الرسوم — the brokerage percentage and a booklet's price when it is not free.</summary>
    public const string Fees = "fees";

    /// <summary>شروط التمديد — the late-bid extension rule.</summary>
    public const string ExtensionTerms = "extensionTerms";

    /// <summary>الصور والمستندات المرفقة — the gallery and the public attachments.</summary>
    public const string Attachments = "attachments";

    /// <summary>التوضيحات العامة — the published answers to bidders' questions.</summary>
    public const string Clarifications = "clarifications";

    /// <summary>
    /// Every group an administrator decides on, in the order the settings page lists
    /// them, with what a visitor gets before anyone has chosen. The defaults are the
    /// visitor's row of the requirements — «استعراض المزادات المنشورة وبيانات القطع
    /// ومواعيدها وشروط المشاركة العامة» — so only the schedule starts open.
    /// </summary>
    public static readonly IReadOnlyList<PublicField> Configurable =
    [
        new(Schedule, "مواعيد المزاد", "موعد البدء والإغلاق والعدّ التنازلي.", PublicByDefault: true),
        new(OpeningPrice, "سعر الافتتاح وزيادة المزايدة", "سعر البداية ومقدار الزيادة في كل مزايدة.", PublicByDefault: false),
        new(LivePrice, "السعر الحالي وعدد المزايدات", "أعلى مزايدة أثناء المزاد وبعده، والحد الأدنى للمزايدة التالية.", PublicByDefault: false),
        new(Fees, "الرسوم", "نسبة السعي، وقيمة كراسة الشروط إن لم تكن مجانية.", PublicByDefault: false),
        new(ExtensionTerms, "شروط التمديد", "التمديد عند المزايدة المتأخرة وعدد مراته.", PublicByDefault: false),
        new(Attachments, "الصور والمستندات المرفقة", "معرض صور القطعة والمخططات المرفقة بالمزاد.", PublicByDefault: false),
        new(Clarifications, "التوضيحات العامة", "إجابات الاستفسارات المعتمدة للنشر.", PublicByDefault: false),
    ];

    /// <summary>What a visitor always sees, listed on the settings page as fixed.</summary>
    public static readonly IReadOnlyList<string> AlwaysPublicAr =
    [
        "قائمة المزادات المنشورة وحالة كل مزاد",
        "بيانات القطعة: الرقم والمساحة والاستخدام والموقع والشارع والواجهة",
        "مبلغ التأمين للمشاركة في المزاد",
        "كراسة الشروط إن كانت مجانية",
        "صورة الغلاف",
    ];

    public static bool IsKnown(string key) => Configurable.Any(f => f.Key == key);
}

public sealed record PublicField(string Key, string LabelAr, string HintAr, bool PublicByDefault);

/// <summary>Which groups a visitor sees. Unknown or missing keys fall back to their default.</summary>
public sealed record PublicVisibilityPolicy(IReadOnlyDictionary<string, bool> Public)
{
    public static PublicVisibilityPolicy Default { get; } = new(
        PublicFields.Configurable.ToDictionary(f => f.Key, f => f.PublicByDefault));

    public bool Shows(string key) =>
        Public.TryGetValue(key, out var shown)
            ? shown
            : PublicFields.Configurable.FirstOrDefault(f => f.Key == key)?.PublicByDefault ?? false;

    /// <summary>The groups a visitor does not see, for the page to say what signing in shows.</summary>
    public IReadOnlyList<string> Hidden() =>
        PublicFields.Configurable.Where(f => !Shows(f.Key)).Select(f => f.Key).ToArray();

    /// <summary>Only the known groups, every one of them present.</summary>
    public static PublicVisibilityPolicy From(IReadOnlyDictionary<string, bool>? chosen) => new(
        PublicFields.Configurable.ToDictionary(
            f => f.Key,
            f => chosen is not null && chosen.TryGetValue(f.Key, out var v) ? v : f.PublicByDefault));
}
