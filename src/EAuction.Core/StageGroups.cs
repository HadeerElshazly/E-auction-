namespace EAuction.Core;

/// <summary>
/// Which filter chip an auction falls under — «القادمة», «الجارية», «المنتهية» — decided
/// once, here, for every list that offers those chips: the public catalogue and the
/// administrators' list. Two services grouping the same stage differently is how the
/// same auction ends up "upcoming" for a citizen and "finished" for the clerk.
///
/// Takes the stage's name rather than an enum, because the catalogue knows stages as
/// the strings it reads off the topics and the administration service as its own
/// enum; both spell them the same.
/// </summary>
public static class StageGroups
{
    public const string All = "all";

    /// <summary>Not yet published: still being prepared or reviewed. Staff only.</summary>
    public const string Preparing = "preparing";

    public const string Upcoming = "upcoming";
    public const string Live = "live";
    public const string Closed = "closed";

    /// <summary>The chips the public catalogue offers, in order.</summary>
    public static readonly string[] Public = [All, Upcoming, Live, Closed];

    /// <summary>The administrators' chips: the public ones plus drafts.</summary>
    public static readonly string[] Staff = [All, Preparing, Upcoming, Live, Closed];

    public static string Of(string status) => status switch
    {
        "Draft" or "PendingReview" or "Rejected" => Preparing,
        // Approved and waiting to open — whether or not its start time has passed:
        // until it opens, it has not started.
        "Approved" or "Scheduled" => Upcoming,
        "Live" or "Closing" => Live,
        _ => Closed,
    };

    /// <summary>Whether <paramref name="status"/> belongs under <paramref name="group"/>.</summary>
    public static bool In(string status, string? group) =>
        string.IsNullOrWhiteSpace(group) || group.Equals(All, StringComparison.OrdinalIgnoreCase)
        || Of(status).Equals(group, StringComparison.OrdinalIgnoreCase);
}
