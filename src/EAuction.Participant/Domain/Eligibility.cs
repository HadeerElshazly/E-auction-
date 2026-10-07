namespace EAuction.Participant.Domain;

/// <summary>
/// A bidder's standing in one auction as the requirements name it (المرحلة الأولى،
/// البند 03): the application is still being completed, is under review, has been
/// accepted, or has been rejected with a reason.
/// </summary>
public enum Eligibility
{
    /// <summary>قيد الاستكمال — steps remain for the bidder.</summary>
    Incomplete,

    /// <summary>قيد المراجعة — a guarantee awaits staff, or a deposit awaits settlement.</summary>
    UnderReview,

    /// <summary>مقبولة — eligible to bid.</summary>
    Accepted,

    /// <summary>مرفوضة — with the reason staff gave.</summary>
    Rejected,
}

/// <summary>What is left to do with a bidder's deposit after the auction (الخاصية 11).</summary>
public enum DepositSettlement
{
    /// <summary>No deposit was paid or guaranteed.</summary>
    None,

    /// <summary>محتجز — the award is not final yet.</summary>
    Held,

    /// <summary>للرد — a paid deposit to refund.</summary>
    ToRefund,

    /// <summary>للتحرير — a bank guarantee to release.</summary>
    ToRelease,

    /// <summary>للمصادرة — forfeited by a defaulting bidder; the disposal still needs recording.</summary>
    ToForfeit,

    /// <summary>احتُسب من الثمن — the winner's, applied to the price. Nothing to return.</summary>
    AppliedToPurchase,

    /// <summary>مُغلق — dealt with, against a reference.</summary>
    Closed,
}
