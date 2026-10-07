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
    /// <summary>"Masked" or "Named". A bidder is entitled to know before registering.</summary>
    string BidderVisibility,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    long OpeningPriceMinorUnits, long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    long DepositMinorUnits, long BookletPriceMinorUnits,
    int PlotCount, decimal TotalAreaSqm, Guid? CoverImageDocumentId)
{
    public static AuctionSummary From(AuctionEntry a) => new(
        a.AuctionId, a.Status, a.NameAr, a.NameEn, a.Channel, a.BidderVisibility,
        a.StartsAt, a.EndsAt,
        a.OpeningPriceMinorUnits, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        a.DepositMinorUnits, a.BookletPriceMinorUnits,
        a.Plots.Count, a.TotalAreaSqm, a.CoverImageDocumentId);
}

public sealed record AuctionDetail(
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    /// <summary>
    /// "Masked" or "Named". Published because a bidder about to pay a deposit is
    /// entitled to know whether their name will be shown to the other bidders — the
    /// administrator's choice is not one they should discover after the fact.
    /// </summary>
    string BidderVisibility,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset? EffectiveEndsAt,
    long OpeningPriceMinorUnits, long MinIncrementMinorUnits,
    long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    long DepositMinorUnits, long BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int MaxExtensions, int ExtensionsUsed,
    decimal TotalAreaSqm, IReadOnlyList<PlotEntry> Plots,
    Guid? CoverImageDocumentId, IReadOnlyList<PublicDocumentEntry> Attachments,
    decimal BrokerageFeePercent, string? CancellationReason)
{
    public static AuctionDetail From(AuctionEntry a) => new(
        a.AuctionId, a.Status, a.NameAr, a.NameEn, a.Channel, a.BidderVisibility,
        a.StartsAt, a.EndsAt, a.EffectiveEndsAt,
        a.OpeningPriceMinorUnits, a.MinIncrementMinorUnits,
        a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        a.DepositMinorUnits, a.BookletPriceMinorUnits,
        a.QuietPeriodSeconds, a.MaxExtensions, a.ExtensionsUsed,
        a.TotalAreaSqm, a.Plots, a.CoverImageDocumentId, a.Attachments,
        a.BrokerageFeePercent, a.CancellationReason);
}

public sealed record LivePrice(
    Guid AuctionId, string Status,
    long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    /// <summary>
    /// What this viewer may be told about the leader: a pseudonym on a masked
    /// auction, the bidder's name on a named one (D-22). One slot, filled in one
    /// place (<c>LeaderLabels</c>), so there is a single thing to get wrong.
    /// </summary>
    string? LeaderLabel, bool LeaderIsYou,
    /// <summary>Set only for the leader, so they know which of their bids won.</summary>
    Guid? YourWinningBidId,
    DateTimeOffset EffectiveEndsAt, int ExtensionsUsed, int MaxExtensions,
    DateTimeOffset AsOf);

/// <summary>
/// One auction on the live monitor: what it is, plus where its bidding has got to.
///
/// The name and the channel ride along because a monitor showing six auctions has
/// to say which is which, and the alternative — the portal joining this against the
/// catalogue list by id on every tick — is a second request and a second thing to
/// get out of step.
///
/// The leader label comes through <see cref="LeaderLabels"/> like every other
/// caller's, so a masked auction stays masked here too (D-22). Staff watching the
/// room are not an exception to that: the masking is the auction's own setting, and
/// a screen that quietly unmasked it would make the setting a lie.
/// </summary>
public sealed record MonitorRow(
    Guid AuctionId, string NameAr, string Channel,
    string Status, long? PriceMinorUnits, long MinimumNextBidMinorUnits,
    long OpeningPriceMinorUnits,
    string? LeaderLabel,
    DateTimeOffset StartsAt, DateTimeOffset EffectiveEndsAt,
    int ExtensionsUsed, int MaxExtensions)
{
    public static MonitorRow From(AuctionEntry a, string? label) => new(
        a.AuctionId, a.NameAr, a.Channel,
        a.Status, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        a.OpeningPriceMinorUnits,
        label,
        a.StartsAt, a.EffectiveEndsAt ?? a.EndsAt,
        a.ExtensionsUsed, a.MaxExtensions);
}

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

    public static LivePrice ForOthers(AuctionEntry a, string? label, DateTimeOffset now) => new(
        a.AuctionId, a.Status, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        label, LeaderIsYou: false, YourWinningBidId: null,
        a.EffectiveEndsAt ?? a.EndsAt, a.ExtensionsUsed, a.MaxExtensions, now);

    public static LivePrice ForLeader(AuctionEntry a, string? label, DateTimeOffset now) => new(
        a.AuctionId, a.Status, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        label, LeaderIsYou: true, a.LeaderClientBidId,
        a.EffectiveEndsAt ?? a.EndsAt, a.ExtensionsUsed, a.MaxExtensions, now);

    /// <summary>The view for one specific caller, for the request/response endpoint.</summary>
    public static LivePrice For(AuctionEntry a, Guid? caller, string? label, DateTimeOffset now) =>
        caller is not null && a.LeaderBidderId == caller
            ? ForLeader(a, label, now)
            : ForOthers(a, label, now);

    public static string Serialise<T>(T view) => JsonSerializer.Serialize(view, Json);
}
