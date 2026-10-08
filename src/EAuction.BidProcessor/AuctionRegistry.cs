using System.Collections.Concurrent;
using System.Text.Json;
using EAuction.Core;

namespace EAuction.BidProcessor;

/// <summary>
/// Assembles a complete <see cref="AuctionDefinition"/> from the two control
/// topics: the public definition on <c>auctions.upcoming</c> and the reserve
/// price on <c>auctions.sealed</c>.
///
/// They arrive separately because the reserve is ACL-restricted (D-23), so
/// either can land first and the registry holds a partial auction until both
/// are present. The two are written in one transaction by auction-admin and
/// relayed in order, so the gap is normally microseconds — a persistent gap
/// means the sealed topic is misconfigured, which the registry reports rather
/// than papering over by running an auction with no reserve.
/// </summary>
public sealed class AuctionRegistry(ILogger<AuctionRegistry> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, AuctionApprovedPayload> _approved = new();
    private readonly ConcurrentDictionary<Guid, long> _reserves = new();
    private readonly ConcurrentDictionary<Guid, AuctionDefinition> _ready = new();

    /// <summary>Raised once, when an auction has both halves and can be run.</summary>
    public event Action<AuctionDefinition>? AuctionReady;

    /// <summary>
    /// Raised when an auction that is already assembled gets a different definition:
    /// the committee approved an amendment to it (§6.5) and auction-admin republished
    /// it whole, on the same two topics. Not raised for a replay of the same record —
    /// the long-running consumers re-read every topic from the beginning, and an
    /// unchanged definition is not news.
    /// </summary>
    public event Action<AuctionDefinition>? AuctionRedefined;

    public IReadOnlyCollection<AuctionDefinition> Ready => _ready.Values.ToList();

    public bool TryGet(Guid auctionId, out AuctionDefinition definition) =>
        _ready.TryGetValue(auctionId, out definition!);

    /// <summary>Auctions still missing one half — a diagnostic, and a health signal.</summary>
    public IReadOnlyCollection<Guid> AwaitingReserve =>
        _approved.Keys.Where(id => !_reserves.ContainsKey(id)).ToList();

    public IReadOnlyCollection<Guid> AwaitingDefinition =>
        _reserves.Keys.Where(id => !_approved.ContainsKey(id)).ToList();

    public void Apply(StreamEvent record)
    {
        switch (record.Topic)
        {
            case Topics.Upcoming when record.EventType == InboundEvents.AuctionApproved:
            {
                var payload = JsonSerializer.Deserialize<AuctionApprovedPayload>(record.Payload, Json);
                if (payload is null) return;
                _approved[payload.AuctionId] = payload;
                TryComplete(payload.AuctionId);
                break;
            }

            case Topics.Sealed when record.EventType == InboundEvents.AuctionReserveSet:
            {
                var payload = JsonSerializer.Deserialize<AuctionReserveSetPayload>(record.Payload, Json);
                if (payload is null) return;
                _reserves[payload.AuctionId] = payload.ReservePriceMinorUnits;
                TryComplete(payload.AuctionId);
                break;
            }
        }
    }

    private void TryComplete(Guid auctionId)
    {
        if (!_approved.TryGetValue(auctionId, out var approved)) return;
        if (!_reserves.TryGetValue(auctionId, out var reserve)) return;

        var definition = Assemble(approved, reserve);

        // Already assembled: either the same record replayed, which changes nothing,
        // or an amendment, which replaces the definition and tells the supervisor.
        if (_ready.TryGetValue(auctionId, out var current))
        {
            if (current == definition) return;
            _ready[auctionId] = definition;
            logger.LogInformation(
                "Auction {AuctionId} redefined: {Start:o} to {End:o}, opening {Opening}.",
                auctionId, definition.StartsAt, definition.EndsAt, definition.OpeningPriceMinorUnits);
            AuctionRedefined?.Invoke(definition);
            return;
        }

        if (!_ready.TryAdd(auctionId, definition)) return;

        logger.LogInformation(
            "Auction {AuctionId} is ready: {Start:o} to {End:o}, quiet period {Quiet}.",
            auctionId, definition.StartsAt, definition.EndsAt,
            definition.QuietPeriod?.ToString() ?? "disabled");

        AuctionReady?.Invoke(definition);
    }

    private static AuctionDefinition Assemble(AuctionApprovedPayload approved, long reserve) =>
        new()
        {
            AuctionId = approved.AuctionId,
            StartsAt = approved.StartsAt,
            EndsAt = approved.EndsAt,
            OpeningPriceMinorUnits = approved.OpeningPriceMinorUnits,
            ReservePriceMinorUnits = reserve,
            Increment = new IncrementPolicy.Fixed(approved.MinIncrementMinorUnits),
            QuietPeriod = approved.QuietPeriodSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : null,
            MaxExtensions = approved.MaxExtensions,
            Channel = Enum.TryParse<BidChannel>(approved.Channel, out var channel)
                ? channel
                : BidChannel.Online
        };
}
