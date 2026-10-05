using System.Collections.Concurrent;

namespace EAuction.QueryBff;

/// <summary>
/// What a bidder is allowed to know about an auction, held in memory and rebuilt by
/// replaying the compacted control topics from offset 0 — the same shape as the bid
/// catcher's state (D-12), for the same reason: no database to keep in step, and an
/// auction approved while this service was down is still in the log when it returns.
///
/// Notice what is not here. There is no reserve price, because this service does not
/// consume <c>auctions.sealed</c> and could not learn it (D-23). The leading bidder's
/// id is held privately and has no field on any response type, so masking (D-22) is
/// a property of the shape rather than a line of code someone has to remember.
/// </summary>
public sealed class CatalogueState
{
    private readonly ConcurrentDictionary<Guid, AuctionEntry> _auctions = new();

    public int Count => _auctions.Count;

    public void Upsert(AuctionEntry entry) =>
        _auctions.AddOrUpdate(entry.AuctionId, entry, (_, existing) => entry with
        {
            // The definition is replaced; the live state is not, because
            // auctions.upcoming can replay after a price has already arrived.
            Status = existing.Status,
            PriceMinorUnits = existing.PriceMinorUnits,
            LeaderBidderId = existing.LeaderBidderId,
            EffectiveEndsAt = existing.EffectiveEndsAt,
            ExtensionsUsed = existing.ExtensionsUsed
        });

    public void SetPrice(
        Guid auctionId, long price, Guid? leader, DateTimeOffset effectiveEndsAt, int extensions)
    {
        if (!_auctions.TryGetValue(auctionId, out var entry)) return;

        _auctions[auctionId] = entry with
        {
            PriceMinorUnits = price,
            LeaderBidderId = leader,
            EffectiveEndsAt = effectiveEndsAt,
            ExtensionsUsed = extensions
        };
    }

    public void SetStatus(Guid auctionId, string status, DateTimeOffset? effectiveEndsAt = null)
    {
        if (!_auctions.TryGetValue(auctionId, out var entry)) return;

        _auctions[auctionId] = entry with
        {
            Status = status,
            EffectiveEndsAt = effectiveEndsAt ?? entry.EffectiveEndsAt
        };
    }

    public bool TryGet(Guid auctionId, out AuctionEntry entry) =>
        _auctions.TryGetValue(auctionId, out entry!);

    public IEnumerable<AuctionEntry> All() => _auctions.Values;
}

/// <summary>
/// One auction's public read model. <see cref="LeaderBidderId"/> is the only field
/// that must never be serialised, and no response type exposes it.
/// </summary>
public sealed record AuctionEntry
{
    public required Guid AuctionId { get; init; }
    public required string NameAr { get; init; }
    public required string NameEn { get; init; }
    public required string Channel { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public required long OpeningPriceMinorUnits { get; init; }
    public required long MinIncrementMinorUnits { get; init; }
    public required long DepositMinorUnits { get; init; }
    public required long BookletPriceMinorUnits { get; init; }
    public int? QuietPeriodSeconds { get; init; }
    public required int MaxExtensions { get; init; }
    public required decimal TotalAreaSqm { get; init; }
    public required IReadOnlyList<PlotEntry> Plots { get; init; }

    /// <summary>From auctions.lifecycle. "Scheduled" until the processor says otherwise.</summary>
    public string Status { get; init; } = "Scheduled";

    /// <summary>Null until the processor has judged a bid: no bid yet, not a price of zero.</summary>
    public long? PriceMinorUnits { get; init; }

    public Guid? LeaderBidderId { get; init; }
    public DateTimeOffset? EffectiveEndsAt { get; init; }
    public int ExtensionsUsed { get; init; }

    /// <summary>What the next bid must be at least. The floor the catcher applies.</summary>
    public long MinimumNextBidMinorUnits =>
        PriceMinorUnits is null
            ? OpeningPriceMinorUnits
            : PriceMinorUnits.Value + MinIncrementMinorUnits;
}

public sealed record PlotEntry(
    Guid Id, string DeedNumber, decimal AreaSqm,
    string? Latitude, string? Longitude,
    string? DescriptionAr, string? DescriptionEn);
