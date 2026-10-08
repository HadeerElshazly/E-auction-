using System.Text.Json;
using EAuction.Core;

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

// A figure is null when it is hidden from this caller — a visitor, under
// «إعدادات العرض للزوار» — and Hidden names the groups that were, so the page can
// say what signing in shows rather than show a blank.
public sealed record AuctionSummary(
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    /// <summary>"Masked" or "Named". A bidder is entitled to know before registering.</summary>
    string BidderVisibility,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    long? OpeningPriceMinorUnits, long? PriceMinorUnits, long? MinimumNextBidMinorUnits,
    long DepositMinorUnits, long? BookletPriceMinorUnits,
    int PlotCount, decimal TotalAreaSqm, Guid? CoverImageDocumentId,
    IReadOnlyList<string> Hidden)
{
    /// <param name="visitor">The visitor's policy, or null for a signed-in caller.</param>
    public static AuctionSummary From(AuctionEntry a, PublicVisibilityPolicy? visitor = null)
    {
        bool Shows(string key) => visitor?.Shows(key) ?? true;
        return new(
            a.AuctionId, a.Status, a.NameAr, a.NameEn, a.Channel, a.BidderVisibility,
            Shows(PublicFields.Schedule) ? a.StartsAt : null,
            Shows(PublicFields.Schedule) ? a.EndsAt : null,
            Shows(PublicFields.OpeningPrice) ? a.OpeningPriceMinorUnits : null,
            Shows(PublicFields.LivePrice) ? a.PriceMinorUnits : null,
            Shows(PublicFields.LivePrice) ? a.MinimumNextBidMinorUnits : null,
            a.DepositMinorUnits,
            Booklet(a, Shows(PublicFields.Fees)),
            a.Plots.Count, a.TotalAreaSqm, a.CoverImageDocumentId,
            visitor?.Hidden() ?? []);
    }

    /// <summary>A free booklet is always said to be free; a price, only with the fees.</summary>
    internal static long? Booklet(AuctionEntry a, bool fees) =>
        a.BookletPriceMinorUnits == 0 || fees ? a.BookletPriceMinorUnits : null;
}

public sealed record AuctionDetail(
    Guid Id, string Status, string NameAr, string NameEn, string Channel,
    /// <summary>
    /// "Masked" or "Named". Published because a bidder about to pay a deposit is
    /// entitled to know whether their name will be shown to the other bidders — the
    /// administrator's choice is not one they should discover after the fact.
    /// </summary>
    string BidderVisibility,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, DateTimeOffset? EffectiveEndsAt,
    long? OpeningPriceMinorUnits, long? MinIncrementMinorUnits,
    long? PriceMinorUnits, long? MinimumNextBidMinorUnits,
    long DepositMinorUnits, long? BookletPriceMinorUnits,
    int? QuietPeriodSeconds, int? MaxExtensions, int? ExtensionsUsed,
    decimal TotalAreaSqm, IReadOnlyList<PlotEntry> Plots,
    Guid? CoverImageDocumentId, IReadOnlyList<PublicDocumentEntry> Attachments,
    decimal? BrokerageFeePercent, string? CancellationReason,
    IReadOnlyList<string> Hidden,
    /// <summary>On a cancelled auction, whether the bidders' money goes back.</summary>
    bool? CancellationRefunds = null)
{
    /// <param name="visitor">The visitor's policy, or null for a signed-in caller.</param>
    public static AuctionDetail From(AuctionEntry a, PublicVisibilityPolicy? visitor = null)
    {
        bool Shows(string key) => visitor?.Shows(key) ?? true;
        var schedule = Shows(PublicFields.Schedule);
        var live = Shows(PublicFields.LivePrice);
        var terms = Shows(PublicFields.ExtensionTerms);
        return new(
            a.AuctionId, a.Status, a.NameAr, a.NameEn, a.Channel, a.BidderVisibility,
            schedule ? a.StartsAt : null, schedule ? a.EndsAt : null, schedule ? a.EffectiveEndsAt : null,
            Shows(PublicFields.OpeningPrice) ? a.OpeningPriceMinorUnits : null,
            Shows(PublicFields.MinIncrement) ? a.MinIncrementMinorUnits : null,
            live ? a.PriceMinorUnits : null, live ? a.MinimumNextBidMinorUnits : null,
            a.DepositMinorUnits, AuctionSummary.Booklet(a, Shows(PublicFields.Fees)),
            terms ? a.QuietPeriodSeconds : null, terms ? a.MaxExtensions : null, live ? a.ExtensionsUsed : null,
            a.TotalAreaSqm, a.Plots, a.CoverImageDocumentId,
            VisibleAttachments(a, Shows(PublicFields.Photos), Shows(PublicFields.Documents)),
            Shows(PublicFields.Fees) ? a.BrokerageFeePercent : null, a.CancellationReason,
            visitor?.Hidden() ?? [], a.CancellationRefunds);
    }

    /// <summary>
    /// The photos and the documents a caller may see. One added before the two were
    /// told apart is either, so it shows only when both are open.
    /// </summary>
    private static IReadOnlyList<PublicDocumentEntry> VisibleAttachments(AuctionEntry a, bool photos, bool documents) =>
        a.Attachments.Where(x => x.Kind switch
        {
            "Photo" => photos,
            "Document" => documents,
            _ => photos && documents,
        }).ToArray();
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

    /// <summary>
    /// The view for somebody who has not signed in: the price and the clock, and
    /// nothing whatever about who is bidding.
    ///
    /// Not even the pseudonym. A masked label («مزايد #2») is already an
    /// anonymising device, but it still tells a passer-by how many distinct people
    /// are in the room and when a new one arrives, and on a Named auction the same
    /// field is a citizen's actual name. A visitor browsing public land listings has
    /// no business with either, so the field is dropped rather than softened.
    /// </summary>
    public static LivePrice ForAnonymous(AuctionEntry a, DateTimeOffset now) => new(
        a.AuctionId, a.Status, a.PriceMinorUnits, a.MinimumNextBidMinorUnits,
        LeaderLabel: null, LeaderIsYou: false, YourWinningBidId: null,
        a.EffectiveEndsAt ?? a.EndsAt, a.ExtensionsUsed, a.MaxExtensions, now);

    /// <summary>The view for one specific caller, for the request/response endpoint.</summary>
    public static LivePrice For(AuctionEntry a, Guid? caller, string? label, DateTimeOffset now) =>
        caller is null
            ? ForAnonymous(a, now)
            : a.LeaderBidderId == caller
                ? ForLeader(a, label, now)
                : ForOthers(a, label, now);

    public static string Serialise<T>(T view) => JsonSerializer.Serialize(view, Json);
}
