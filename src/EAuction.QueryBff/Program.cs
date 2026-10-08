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
builder.Services.AddSingleton<BidderNames>();
builder.Services.AddSingleton<LeaderLabels>();
builder.Services.AddSingleton<FanOut>();
builder.Services.AddSingleton<Clarifications>();
builder.Services.AddSingleton<PublicVisibilityState>();
builder.Services.AddHostedService<PublicVisibilityConsumer>();
builder.Services.AddHostedService<ClarificationsConsumer>();
builder.Services.AddSingleton(sp => new CatalogueConsumer(
    sp.GetRequiredService<CatalogueState>(),
    sp.GetRequiredService<BidderNames>(),
    sp.GetRequiredService<LeaderLabels>(),
    sp.GetRequiredService<FanOut>(),
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

app.MapGet("/auctions", (string? state, string? q, int? skip, int? take, HttpContext http, CatalogueState catalogue, PublicVisibilityState visibility) =>
{
    // The chips' grouping is shared with the administrators' list (StageGroups), so
    // an auction is "upcoming" or "finished" on both portals alike.
    bool InState(AuctionEntry a, string? s) => StageGroups.In(a.Status, s);

    // Searched on the server, Arabic- and English-name, with the same folding of
    // أ/إ/آ, ة and ى that «طلباتي» uses (EAuction.Core.ArabicText).
    var needle = ArabicText.Normalise(q);
    bool Found(AuctionEntry a) =>
        needle.Length == 0
        || ArabicText.Normalise(a.NameAr).Contains(needle)
        || ArabicText.Normalise(a.NameEn).Contains(needle);

    var all = catalogue.All().ToArray();
    var visitor = visibility.For(http);
    var slice = Slice.From(skip, take, Slice.MaxTake);
    var matching = all
        .Where(a => InState(a, state) && Found(a))
        .OrderBy(a => a.StartsAt)
        .ToArray();
    var items = slice.Of(matching).Select(a => AuctionSummary.From(a, visitor)).ToArray();

    // Each status chip's count under the current search.
    var counts = StageGroups.Public
        .ToDictionary(k => k, k => all.Count(a => InState(a, k) && Found(a)));

    return Results.Ok(new { count = matching.Length, total = matching.Length, skip = slice.Skip, take = slice.Take, items, counts });
}).AllowAnonymous();

// ---------------------------------------------------------------------------
// Every auction that is open right now, in one request.
//
// For the operations screen (§7.2's fan-out serves one auction to one bidder; this
// serves every auction to one watcher). A monitor built on the per-auction endpoints
// would open a stream or a poll each, and six of those is where a browser's
// per-origin connection limit starts refusing — the screen would silently stop
// updating the seventh auction rather than fail in any visible way.
//
// Anonymous like its neighbours, and that is not an oversight: every field here is
// already served per auction by /auctions/{id}/price to anyone who asks, and the
// auctions themselves are listed publicly by /auctions?state=live. This is those
// calls folded into one, so it discloses nothing new. The leader label in
// particular goes through LeaderLabels, so a masked auction is masked here too.
// ---------------------------------------------------------------------------
app.MapGet("/auctions/live", (int? skip, int? take, HttpContext http, CatalogueState catalogue, LeaderLabels labels, PublicVisibilityState visibility) =>
{
    // The live prices, folded together: a visitor gets them only if the setting
    // lets a visitor see a live price at all.
    if (visibility.For(http) is { } visitor && !visitor.Shows(PublicFields.LivePrice))
        return Results.Json(new { signInRequired = true }, statusCode: StatusCodes.Status401Unauthorized);

    var slice = Slice.From(skip, take, Slice.MaxTake);
    var live = catalogue.All()
        .Where(a => a.Status == "Live")
        .OrderBy(a => a.EffectiveEndsAt ?? a.EndsAt)
        .ToArray();
    var rows = slice.Of(live).Select(a => MonitorRow.From(a, labels.For(a))).ToArray();

    return Results.Ok(new { count = live.Length, total = live.Length, skip = slice.Skip, take = slice.Take, items = rows, asOf = DateTimeOffset.UtcNow });
}).AllowAnonymous();

// ---------------------------------------------------------------------------
// Who leads, by id — staff only. The one endpoint here that reveals identity, and
// so the one that is not anonymous: the people running the auction need to know
// who is winning a running auction and who won a closed one, whatever the public
// view masks (D-22). The id only; the portal asks the participant service for the
// name, which is where names live.
// ---------------------------------------------------------------------------
app.MapGet("/staff/leaders", (CatalogueState catalogue) =>
{
    var rows = catalogue.All()
        .Where(a => a.LeaderBidderId is not null)
        .Select(a => new
        {
            auctionId = a.AuctionId,
            leaderBidderId = a.LeaderBidderId,
            priceMinorUnits = a.PriceMinorUnits,
            status = a.Status,
        })
        .ToArray();
    return Results.Ok(new { items = rows });
}).RequireAuthorization(Policies.StaffOnTheFloor);

// «التوضيحات العامة» (الخاصية 10): what staff published, after approval, for
// everyone reading the auction — anonymous, like the auction itself.
app.MapGet("/auctions/{id:guid}/clarifications", (Guid id, HttpContext http, Clarifications store, PublicVisibilityState visibility) =>
    visibility.For(http) is { } visitor && !visitor.Shows(PublicFields.Clarifications)
        ? Results.Ok(new { items = Array.Empty<Clarification>(), signInRequired = true })
        : Results.Ok(new { items = store.For(id), signInRequired = false })).AllowAnonymous();

app.MapGet("/auctions/{id:guid}", (Guid id, HttpContext http, CatalogueState catalogue, PublicVisibilityState visibility) =>
    catalogue.TryGet(id, out var a)
        ? Results.Ok(AuctionDetail.From(a, visibility.For(http)))
        : Results.NotFound()).AllowAnonymous();

// ---------------------------------------------------------------------------
// The live price. Polled by the portals today; this is the shape a push channel
// would deliver unchanged, so the portals do not care which it is.
// ---------------------------------------------------------------------------

app.MapGet("/auctions/{id:guid}/price", (
    Guid id, HttpContext http, CatalogueState catalogue, LeaderLabels labels, PublicVisibilityState visibility) =>
{
    if (!catalogue.TryGet(id, out var a)) return Results.NotFound();
    if (visibility.For(http) is { } visitor && !visitor.Shows(PublicFields.LivePrice))
        return Results.Json(new { signInRequired = true }, statusCode: StatusCodes.Status401Unauthorized);

    // The one thing a caller learns about identity: whether the leader is
    // themselves. Anonymous callers learn nothing, which is correct — "am I
    // winning" is not a question an anonymous caller can be asking.
    var caller = http.User.SubjectId();

    return Results.Ok(LiveViews.For(a, caller, labels.For(a), DateTimeOffset.UtcNow));
}).AllowAnonymous();

// ---------------------------------------------------------------------------
// The push channel (§7.2).
//
// Server-sent events rather than a WebSocket: nothing flows upwards on this
// channel — bids go to the catcher over its own POST — so half a WebSocket would be
// unused, and SSE survives a proxy that mangles upgrade handshakes.
//
// A snapshot first, then deltas. A reconnecting client is told the truth as of now
// rather than replayed from an offset, which is self-healing and is the only thing
// this service could honestly offer: its state is a compacted read model with no
// arbitrary history to replay.
// ---------------------------------------------------------------------------

app.MapGet("/auctions/{id:guid}/stream", async (
    Guid id, HttpContext http, CatalogueState catalogue, LeaderLabels labels,
    FanOut fanOut, PublicVisibilityState visibility, CancellationToken ct) =>
{
    if (visibility.For(http) is { } visitor && !visitor.Shows(PublicFields.LivePrice))
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    // Returns Task, not IResult, and sets its own status code.
    //
    // A minimal-API handler that writes to the response directly and then also
    // returns an IResult leaves the framework trying to execute a result over a
    // response that has already started — which surfaces as
    // NotSupportedException from the error-page middleware, long after the actual
    // mistake, with the real stack swallowed.
    if (!catalogue.TryGet(id, out _))
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var caller = http.User.SubjectId();

    http.Response.StatusCode = StatusCodes.Status200OK;
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache, no-transform";

    // Nginx buffers proxied responses by default, which turns a live stream into
    // one delivered when the auction is already over.
    http.Response.Headers["X-Accel-Buffering"] = "no";

    // Subscribe BEFORE reading the snapshot, so a price change in between is queued
    // rather than lost. It may then arrive as a delta repeating the snapshot, which
    // is harmless: a price update is idempotent.
    using var subscription = fanOut.Subscribe(id, caller);

    if (catalogue.TryGet(id, out var entry))
    {
        await WriteEventAsync(http.Response, "snapshot",
            LiveViews.Serialise(
                LiveViews.For(entry, caller, labels.For(entry), DateTimeOffset.UtcNow)), ct);
    }

    // Verdicts this bidder may not have seen. A verdict is an event, not state, so
    // it is absent from the snapshot and a reconnect would otherwise lose the one
    // message explaining why a bid failed.
    foreach (var verdict in fanOut.RecentVerdicts(id, caller))
        await WriteEventAsync(http.Response, "verdict", verdict, ct);

    try
    {
        // WaitToReadAsync/TryRead rather than ReadAllAsync, because this loop has to
        // race a read against the keep-alive timer. The enumerator ReadAllAsync
        // returns cannot be disposed from outside an await foreach — it throws
        // NotSupportedException and kills the connection, which is exactly what it
        // did here before.
        //
        // Converted to a Task once and held, then re-created only after it has
        // completed: a ValueTask may be consumed exactly once.
        var waiting = subscription.WaitToReadAsync(ct).AsTask();

        while (!ct.IsCancellationRequested)
        {
            // Task.Delay rather than PeriodicTimer. A PeriodicTimer permits exactly
            // one outstanding WaitForNextTickAsync, and this loop abandons the timer
            // arm every time the read arm wins the race — so the next iteration's
            // call threw InvalidOperationException and dropped the connection as
            // soon as any message arrived. A fresh Delay has no such restriction.
            var tick = Task.Delay(TimeSpan.FromSeconds(20), ct);

            if (await Task.WhenAny(waiting, tick) == tick)
            {
                // A comment, which SSE ignores. Idle connections get closed by
                // proxies and by mobile networks, and a quiet auction between bids
                // is the normal case rather than the exception.
                await http.Response.WriteAsync(": keep-alive\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                continue;
            }

            if (!await waiting) break;

            // Drains everything queued, so a burst at the close of an auction costs
            // one flush rather than one per message.
            while (subscription.TryRead(out var payload))
                await WriteEventAsync(http.Response, EventNameFor(payload), payload, ct);

            waiting = subscription.WaitToReadAsync(ct).AsTask();
        }
    }
    catch (OperationCanceledException)
    {
        // The client went away. Normal, and the usual way this loop ends.
    }
}).AllowAnonymous();

app.Run();

/// <summary>
/// Which SSE event a queued payload is.
///
/// Read off the payload rather than carried alongside it, so the fan-out's queue
/// stays a queue of strings — one allocation per change rather than a wrapper per
/// subscriber per change.
/// </summary>
static string EventNameFor(string payload) =>
    payload.Contains("\"accepted\":", StringComparison.Ordinal) ? "verdict" : "price";

static async Task WriteEventAsync(
    HttpResponse response, string eventName, string data, CancellationToken ct)
{
    await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
    await response.Body.FlushAsync(ct);
}
