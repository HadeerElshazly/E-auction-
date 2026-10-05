using System.Text.Json;

namespace EAuction.QueryBff;

// ---------------------------------------------------------------------------
// What a caller is allowed to see.
//
// None of these types has a field for the leading bidder's id, the winning bid's
// client id, or the reserve price. That is the point of them being separate types
// from AuctionEntry: masking (D-22) and the sealed reserve (D-23) are properties of
// the shape, not of someone remembering to leave a field out. MaskingTests asserts
// it by reflection.
// ---------------------------------------------------------------------------

public sealed record AuctionSummary(
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    long OpeningPriceMinorUnits, long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    long DepositMinorUnits, long BookletPriceMinorUnits,
    int PlotCount, decimal TotalAreaSqm)
{
    public static AuctionSummary From(AuctionEntry a) => new(
        a.AuctionId, a.Status, a.NameAr, a.NameEn, a.Channel,
        a.StartsAt, a.EndsAt,
        a.OpeningPriceMinorUnits, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        a.DepositMinorUnits, a.BookletPriceMinorUnits,
        a.Plots.Count, a.TotalAreaSqm);
}

public sealed record AuctionDetail(
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset? EffectiveEndsAt,
    long OpeningPriceMinorUnits, long MinIncrementMinorUnits,
    long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    long DepositMinorUnits, long BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int MaxExtensions, int ExtensionsUsed,
    decimal TotalAreaSqm, IReadOnlyList<PlotEntry> Plots)
{
    public static AuctionDetail From(AuctionEntry a) => new(
        a.AuctionId, a.Status, a.NameAr, a.NameEn, a.Channel,
        a.StartsAt, a.EndsAt, a.EffectiveEndsAt,
        a.OpeningPriceMinorUnits, a.MinIncrementMinorUnits,
        a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        a.DepositMinorUnits, a.BookletPriceMinorUnits,
        a.QuietPeriodSeconds, a.MaxExtensions, a.ExtensionsUsed,
        a.TotalAreaSqm, a.Plots);
}

public sealed record LivePrice(
    Guid AuctionId, string Status,
    long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    string? LeaderAlias, bool LeaderIsYou,
    /// <summary>Set only for the leader, so they know which of their bids won.</summary>
    Guid? YourWinningBidId,
    DateTimeOffset EffectiveEndsAt, int ExtensionsUsed, int MaxExtensions,
    DateTimeOffset AsOf);

/// <summary>A bidder's own bid outcome. Sent to that bidder and to nobody else.</summary>
public sealed record BidVerdictView(
    Guid AuctionId, Guid ClientBidId, bool Accepted, string? Reason,
    long CurrentPriceMinorUnits, long MinimumNextBidMinorUnits, DateTimeOffset AsOf);

/// <summary>
/// Builds the two variants of a price update.
///
/// Two, not one per subscriber: the payload differs only in whether the recipient is
/// the leader. At ten thousand watchers that is two serialisations per price change
/// instead of ten thousand, and it is also the only place the leader-only fields can
/// leak from, so having exactly one of them is worth more than the speed.
/// </summary>
public static class LiveViews
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LivePrice ForOthers(AuctionEntry a, string? alias, DateTimeOffset now) => new(
        a.AuctionId, a.Status, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        alias, LeaderIsYou: false, YourWinningBidId: null,
        a.EffectiveEndsAt ?? a.EndsAt, a.ExtensionsUsed, a.MaxExtensions, now);

    public static LivePrice ForLeader(AuctionEntry a, string? alias, DateTimeOffset now) => new(
        a.AuctionId, a.Status, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        alias, LeaderIsYou: true, a.LeaderClientBidId,
        a.EffectiveEndsAt ?? a.EndsAt, a.ExtensionsUsed, a.MaxExtensions, now);

    /// <summary>The view for one specific caller, for the request/response endpoint.</summary>
    public static LivePrice For(AuctionEntry a, Guid? caller, string? alias, DateTimeOffset now) =>
        caller is not null && a.LeaderBidderId == caller
            ? ForLeader(a, alias, now)
            : ForOthers(a, alias, now);

    public static string Serialise<T>(T view) => JsonSerializer.Serialize(view, Json);
}
