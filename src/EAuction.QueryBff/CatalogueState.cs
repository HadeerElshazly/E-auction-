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

    public void Upsert(AuctionEntry entry)
    {
        _auctions.AddOrUpdate(entry.AuctionId, entry, (_, existing) => entry with
        {
            // The definition is replaced; the live state is not, because
            // auctions.upcoming can replay after a price has already arrived.
            Status = existing.Status,
            PriceMinorUnits = existing.PriceMinorUnits,
            LeaderBidderId = existing.LeaderBidderId,
            LeaderClientBidId = existing.LeaderClientBidId,
            EffectiveEndsAt = existing.EffectiveEndsAt,
            ExtensionsUsed = existing.ExtensionsUsed
        });

        // A price that replayed before its definition.
        if (_pendingPrices.TryRemove(entry.AuctionId, out var p))
            SetPrice(entry.AuctionId, p.Price, p.Leader, p.LeaderClientBidId, p.EffectiveEndsAt, p.Extensions);

        // Lifecycle that replayed before its definition, applied now and in order.
        if (_deferred.TryRemove(entry.AuctionId, out var events))
            lock (events)
                foreach (var eventType in events)
                    ApplyLifecycle(entry.AuctionId, eventType);

        // A cancellation that replayed before its definition.
        if (_cancelled.ContainsKey(entry.AuctionId)) ApplyCancellation(entry.AuctionId);
    }

    /// <summary>
    /// Lifecycle events for auctions whose definition has not replayed yet. The two
    /// topics are followed concurrently, so after a restart a stage can arrive before
    /// the auction it belongs to; dropping it left the catalogue showing an awarded
    /// auction as upcoming until the next event happened to come along.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, List<string>> _deferred = new();

    /// <summary>The latest price for each auction whose definition has not replayed yet.</summary>
    private readonly ConcurrentDictionary<Guid, PendingPrice> _pendingPrices = new();

    private sealed record PendingPrice(
        long Price, Guid? Leader, Guid? LeaderClientBidId, DateTimeOffset EffectiveEndsAt, int Extensions);

    /// <summary>
    /// Moves an auction's stage by one lifecycle event, through the one rule both
    /// portals share (<see cref="LifecycleStatus"/>). Returns the entry when it
    /// changed, null when the event did not move it or was deferred.
    /// </summary>
    public AuctionEntry? ApplyLifecycle(Guid auctionId, string eventType, DateTimeOffset? effectiveEndsAt = null)
    {
        if (!_auctions.TryGetValue(auctionId, out var entry))
        {
            var list = _deferred.GetOrAdd(auctionId, _ => new List<string>());
            lock (list) list.Add(eventType);
            return null;
        }

        var status = LifecycleStatus.After(entry.Status, eventType);
        return status is null ? null : SetStatus(auctionId, status, effectiveEndsAt);
    }

    public const string Cancelled = "Cancelled";

    /// <summary>
    /// Withdrawn auctions, remembered apart from the entries: the lifecycle and the
    /// definitions replay concurrently, and a cancellation that arrived first would
    /// otherwise be dropped and the auction shown as upcoming after a restart.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _cancelled = new();

    public AuctionEntry? MarkCancelled(Guid auctionId, string reason)
    {
        _cancelled[auctionId] = reason;
        return ApplyCancellation(auctionId);
    }

    private AuctionEntry? ApplyCancellation(Guid auctionId)
    {
        if (!_auctions.TryGetValue(auctionId, out var entry)) return null;
        if (!_cancelled.TryGetValue(auctionId, out var reason)) return entry;
        var updated = entry with { Status = Cancelled, CancellationReason = reason };
        _auctions[auctionId] = updated;
        return updated;
    }

    /// <summary>Returns the updated entry, or null if the auction is not known yet.</summary>
    public AuctionEntry? SetPrice(
        Guid auctionId, long price, Guid? leader, Guid? leaderClientBidId,
        DateTimeOffset effectiveEndsAt, int extensions)
    {
        if (!_auctions.TryGetValue(auctionId, out var entry))
        {
            // Held, not dropped, and not shown: the auction stays out of the
            // catalogue until its definition arrives (no shell entries), but its
            // price is applied then. Dropping it lost every running auction's price
            // on each restart — the topics replay concurrently, prices often first —
            // and the catalogue said «لا مزايدات بعد» over an auction at 640,000
            // until somebody happened to bid again.
            _pendingPrices[auctionId] = new PendingPrice(price, leader, leaderClientBidId, effectiveEndsAt, extensions);
            return null;
        }

        var updated = entry with
        {
            PriceMinorUnits = price,
            LeaderBidderId = leader,
            LeaderClientBidId = leaderClientBidId,
            EffectiveEndsAt = effectiveEndsAt,
            ExtensionsUsed = extensions
        };

        _auctions[auctionId] = updated;
        return updated;
    }

    /// <summary>Returns the updated entry, or null if the auction is not known yet.</summary>
    public AuctionEntry? SetStatus(
        Guid auctionId, string status, DateTimeOffset? effectiveEndsAt = null)
    {
        if (!_auctions.TryGetValue(auctionId, out var entry)) return null;

        var updated = entry with
        {
            Status = status,
            EffectiveEndsAt = effectiveEndsAt ?? entry.EffectiveEndsAt
        };

        _auctions[auctionId] = updated;
        return updated;
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

    /// <summary>
    /// "Masked" or "Named", from the auction's own definition (D-22). A string
    /// rather than an enum because this service reads the topic as a stranger: a
    /// value it has never heard of must mean masked, not throw.
    /// </summary>
    public string BidderVisibility { get; init; } = "Masked";

    /// <summary>
    /// Whether this auction may show a bidder's name. The comparison lives here so
    /// there is one of it, and so "anything unrecognised is masked" is a property of
    /// the model rather than of each caller.
    /// </summary>
    public bool NamesBidders =>
        string.Equals(BidderVisibility, "Named", StringComparison.OrdinalIgnoreCase);

    public required DateTimeOffset StartsAt { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public required long OpeningPriceMinorUnits { get; init; }
    public required long MinIncrementMinorUnits { get; init; }
    public required long DepositMinorUnits { get; init; }
    public required long BookletPriceMinorUnits { get; init; }

    /// <summary>السعي — charged to the winner on the price won. Public: a bidder weighs it before paying a deposit.</summary>
    public decimal BrokerageFeePercent { get; init; }
    public int? QuietPeriodSeconds { get; init; }
    public required int MaxExtensions { get; init; }
    public required decimal TotalAreaSqm { get; init; }
    public required IReadOnlyList<PlotEntry> Plots { get; init; }

    /// <summary>Public in the document service; shown on the catalogue card and the plot page.</summary>
    public Guid? CoverImageDocumentId { get; init; }

    /// <summary>Plans, photographs — documents anyone may read. Never the booklet.</summary>
    public IReadOnlyList<PublicDocumentEntry> Attachments { get; init; } = [];

    /// <summary>Why an administrator withdrew it — public, as the cancellation is.</summary>
    public string? CancellationReason { get; init; }

    /// <summary>From auctions.lifecycle. "Scheduled" until the processor says otherwise.</summary>
    public string Status { get; init; } = "Scheduled";

    /// <summary>Null until the processor has judged a bid: no bid yet, not a price of zero.</summary>
    public long? PriceMinorUnits { get; init; }

    public Guid? LeaderBidderId { get; init; }

    /// <summary>
    /// The leading bid as its own bidder's client identified it. Held so the fan-out
    /// can tell that one bidder which of their bids won; never served to anyone else,
    /// for the same reason as <see cref="LeaderBidderId"/> (D-22).
    /// </summary>
    public Guid? LeaderClientBidId { get; init; }

    public DateTimeOffset? EffectiveEndsAt { get; init; }
    public int ExtensionsUsed { get; init; }

    /// <summary>What the next bid must be at least. The floor the catcher applies.</summary>
    public long MinimumNextBidMinorUnits =>
        PriceMinorUnits is null
            ? OpeningPriceMinorUnits
            : PriceMinorUnits.Value + MinIncrementMinorUnits;
}

public sealed record PublicDocumentEntry(Guid DocumentId, string TitleAr);

public sealed record PlotEntry(
    Guid Id, string PlotNumber, decimal AreaSqm,
    string? Latitude, string? Longitude,
    string? DescriptionAr, string? DescriptionEn,
    decimal? StreetWidthMeters, decimal? FrontageMeters);
