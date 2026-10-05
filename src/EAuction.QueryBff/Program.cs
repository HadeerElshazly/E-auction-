using EAuction.Core;
using EAuction.QueryBff;
using EAuction.Security;

// ---------------------------------------------------------------------------
// Public read model.
//
// Separate from the participant service on purpose. That service holds national
// IDs and deposit records — personal data under PDPL — and giving it a public read
// path would put one careless `Include` between a bidder's file and the internet.
// This service has no database and can only answer from three control topics.
//
// Separate from the bid catcher for the opposite reason: the catcher is the hot
// path, and a read API that shares its process competes with bidding for CPU in
// the last thirty seconds of a hot lot, which is precisely when both matter most.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

builder.Services.AddSingleton<IEventStream>(
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "query-bff"
        }));

builder.Services.AddSingleton<CatalogueState>();
builder.Services.AddSingleton<BidderAliases>();
builder.Services.AddSingleton(sp => new CatalogueConsumer(
    sp.GetRequiredService<CatalogueState>(),
    sp.GetRequiredService<IEventStream>(),
    sp.GetRequiredService<ILogger<CatalogueConsumer>>())
{
    MinimumWarmUp = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("QueryBff:MinimumWarmUpSeconds", 8))
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogueConsumer>());

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);
builder.Services.AddEAuctionCors(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseEAuctionCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok("ok")).AllowAnonymous();

// Not ready until all three topics have been replayed. A BFF answering with an
// empty catalogue reads as "there are no auctions", which is worse than a 503.
app.MapGet("/health/ready", (CatalogueConsumer c) =>
    c.Warm ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();

// ---------------------------------------------------------------------------
// The catalogue is anonymous.
//
// A government land auction is published before anyone registers — that is how a
// citizen decides whether to buy the booklet at all. The auction's economics are
// public for the same reason it is an open auction rather than a sealed one. What
// is NOT public is who is bidding (D-22) and the reserve price (D-23), and neither
// is reachable from here.
// ---------------------------------------------------------------------------

app.MapGet("/auctions", (string? state, CatalogueState catalogue) =>
{
    var now = DateTimeOffset.UtcNow;

    var items = catalogue.All()
        .Where(a => state?.ToLowerInvariant() switch
        {
            null or "" or "all" => true,
            "upcoming" => a.Status == "Scheduled" && a.StartsAt > now,
            "live" => a.Status == "Live",
            "closed" => a.Status is "Closed" or "PendingAward" or "Unsold",
            _ => true
        })
        .OrderBy(a => a.StartsAt)
        .Select(AuctionSummary.From)
        .ToArray();

    return Results.Ok(new { count = items.Length, items });
}).AllowAnonymous();

app.MapGet("/auctions/{id:guid}", (Guid id, CatalogueState catalogue) =>
    catalogue.TryGet(id, out var a)
        ? Results.Ok(AuctionDetail.From(a))
        : Results.NotFound()).AllowAnonymous();

// ---------------------------------------------------------------------------
// The live price. Polled by the portals today; this is the shape a push channel
// would deliver unchanged, so the portals do not care which it is.
// ---------------------------------------------------------------------------

app.MapGet("/auctions/{id:guid}/price", (
    Guid id, HttpContext http, CatalogueState catalogue, BidderAliases aliases) =>
{
    if (!catalogue.TryGet(id, out var a)) return Results.NotFound();

    // The one thing a caller learns about identity: whether the leader is
    // themselves. Anonymous callers learn nothing, which is correct — "am I
    // winning" is not a question an anonymous caller can be asking.
    var caller = http.User.SubjectId();
    var leaderIsYou = caller is not null && a.LeaderBidderId == caller;

    return Results.Ok(new LivePrice(
        a.AuctionId,
        a.Status,
        a.PriceMinorUnits,
        a.MinimumNextBidMinorUnits,
        a.LeaderBidderId is null ? null : aliases.For(a.AuctionId, a.LeaderBidderId.Value),
        leaderIsYou,
        a.EffectiveEndsAt ?? a.EndsAt,
        a.ExtensionsUsed,
        a.MaxExtensions,
        DateTimeOffset.UtcNow));
}).AllowAnonymous();

app.Run();

// ---------------------------------------------------------------------------
// Response shapes. None of them has a field for the leading bidder's id or the
// reserve price — that is the point of them being separate types.
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
    DateTimeOffset EffectiveEndsAt, int ExtensionsUsed, int MaxExtensions,
    DateTimeOffset AsOf);
