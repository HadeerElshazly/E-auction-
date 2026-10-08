using System.Security.Cryptography;
using EAuction.Audit.Domain;
using EAuction.Audit.Integration;
using EAuction.Audit.Persistence;
using EAuction.Core;
using EAuction.Security;
using Microsoft.EntityFrameworkCore;

// ---------------------------------------------------------------------------
// The staff audit trail (D-44).
//
// A service of its own, because the service that performs an action must not own
// the only record of it. An administrator who can approve an auction and also
// amend the record of having approved it has, between the two, no audit trail at
// all — only a story that happens to be in a database.
//
// It has no endpoint that writes. Not "no endpoint an ordinary caller can reach":
// none at all. Entries arrive from Kafka and nowhere else, which is what makes
// "nobody can edit the trail through the API" a property of the code rather than
// of the authorization configuration.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("Audit")
    ?? "Host=localhost;Database=eauction_audit;Username=eauction;Password=eauction";

builder.Services.AddDbContextFactory<AuditDbContext>(o => o.UseNpgsql(connectionString));

var bootstrap = builder.Configuration["Kafka:BootstrapServers"];

builder.Services.AddSingleton<IEventStream>(
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryEventStream()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "audit"
        }));

builder.Services.AddSingleton<AuditConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditConsumer>());

// The system's own actions — eligibility, payments, refused bids — on a consumer
// group of their own, so they never share offsets with the staff trail above.
builder.Services.AddHostedService(sp => new SystemEventsConsumer(
    sp.GetRequiredService<IDbContextFactory<AuditDbContext>>(),
    string.IsNullOrWhiteSpace(bootstrap)
        ? sp.GetRequiredService<IEventStream>()
        : new KafkaEventStream(new KafkaEventStreamOptions
        {
            BootstrapServers = bootstrap,
            ConsumerGroup = "audit-system"
        }),
    sp.GetRequiredService<ILogger<SystemEventsConsumer>>()));

// Read-only: the bid history is read from each auction's bid log on request.
builder.Services.AddSingleton<IBidLog>(_ =>
    string.IsNullOrWhiteSpace(bootstrap)
        ? new InMemoryBidLog()
        : new KafkaBidLog(new KafkaBidLogOptions { BootstrapServers = bootstrap, ConsumerGroup = "audit-bids" }));

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

// Ready means the database answers AND the chain has been picked up. A replica
// that started serving before RestoreAsync finished would answer a query from an
// empty head, and the next record it wrote would chain onto nothing.
app.MapGet("/health/ready", async (
    IDbContextFactory<AuditDbContext> f, AuditConsumer consumer, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    return consumer.Ready && await db.Database.CanConnectAsync(ct)
        ? Results.Ok("ok")
        : Results.StatusCode(503);
}).AllowAnonymous();

// --- the trail -------------------------------------------------------------

// A page of the trail, newest first.
app.MapGet("/audit", async (
    Guid? actor, string? action, string? subject,
    DateTimeOffset? from, DateTimeOffset? to, int? skip, int? take,
    IDbContextFactory<AuditDbContext> f, CancellationToken ct) =>
{
    var page = Math.Clamp(take ?? 50, 1, 500);
    var offset = Math.Max(0, skip ?? 0);

    await using var db = await f.CreateDbContextAsync(ct);

    var query = db.Entries.AsNoTracking();

    if (actor is not null) query = query.Where(x => x.ActorSubject == actor);
    if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);

    // A prefix, because a subject is "type/id" and an auditor asking about
    // auctions in general has only the type.
    if (!string.IsNullOrWhiteSpace(subject))
        query = query.Where(x => x.Subject != null && x.Subject.StartsWith(subject));

    if (from is not null) query = query.Where(x => x.At >= from);
    if (to is not null) query = query.Where(x => x.At <= to);

    var total = await query.CountAsync(ct);

    // By offset, not by At. The timestamps come from four services' clocks and a
    // trail ordered by them can read as though an approval preceded the submission
    // it approved. The offset is the one total order there is (D-03).
    var rows = await query
        .OrderByDescending(x => x.Offset)
        .Skip(offset).Take(page)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        total,
        skip = offset,
        take = page,
        items = rows.Select(AuditEntryResponse.From)
    });
}).RequireAuthorization(Policies.Auditor);

// «سجل المزايدات والإجراءات» (الخاصية 14) — what the platform did by itself:
// eligibility gained or lost, money charged, refused, refunded or forfeited.
app.MapGet("/audit/system", async (
    Guid? auctionId, string? kind, int? skip, int? take,
    IDbContextFactory<AuditDbContext> f, CancellationToken ct) =>
{
    var page = Math.Clamp(take ?? 25, 1, 200);
    var offset = Math.Max(0, skip ?? 0);
    await using var db = await f.CreateDbContextAsync(ct);

    // Refused bids belong to the bid history, where they sit beside the bid.
    var query = db.SystemEvents.AsNoTracking().Where(e => e.Kind != SystemEventKinds.BidRejected);
    if (auctionId is not null) query = query.Where(e => e.AuctionId == auctionId);
    if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(e => e.Kind == kind);

    var total = await query.CountAsync(ct);
    var items = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
        .Skip(offset).Take(page).ToListAsync(ct);
    return Results.Ok(new { total, skip = offset, take = page, items });
}).RequireAuthorization(Policies.Auditor);

// The bid sequence of one auction, rebuilt from the record itself: every bid the
// catcher recorded, in the order it was recorded, with the processor's verdict
// beside it — refused with its reason, or accepted. Nothing here can change a bid:
// the log is append-only and this only reads it.
//
// Staff who decide or check the result read it: the auditor, administrators and
// the award committee.
app.MapGet("/audit/auctions/{auctionId:guid}/bids", async (
    Guid auctionId, int? skip, int? take, IBidLog log, IDbContextFactory<AuditDbContext> f, CancellationToken ct) =>
{
    var slice = Slice.From(skip, take, Slice.MaxTake);
    await using var db = await f.CreateDbContextAsync(ct);
    var refusals = await db.SystemEvents.AsNoTracking()
        .Where(e => e.AuctionId == auctionId && e.Kind == SystemEventKinds.BidRejected && e.ClientBidId != null)
        .OrderBy(e => e.Id)
        .Select(e => new { Id = e.ClientBidId!.Value, e.Reason })
        .ToListAsync(ct);
    var refused = new Dictionary<Guid, string?>();
    foreach (var r in refusals) refused[r.Id] = r.Reason;

    var bids = new List<BidHistoryEntry>();
    long end;
    try { end = await log.GetEndOffsetAsync(auctionId, ct); }
    catch (Exception) { end = 0; }

    if (end > 0)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await foreach (var bid in log.ReadAsync(auctionId, 0, timeout.Token))
            {
                if (BidHistoryEntry.Decode(bid, refused) is { } entry) bids.Add(entry);
                if (bid.Offset >= end - 1) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Partial rather than nothing; the count below says how much was read.
        }
    }

    var accepted = bids.Where(b => b.Accepted).ToList();
    return Results.Ok(new
    {
        auctionId,
        recorded = end,
        read = bids.Count,
        acceptedCount = accepted.Count,
        rejectedCount = bids.Count - accepted.Count,
        leader = accepted.Count == 0 ? null : accepted[^1],
        // The counts above are over every bid; the rows are one page of them, the
        // latest first — what a reviewer looks at before scrolling back.
        total = bids.Count,
        skip = slice.Skip,
        take = slice.Take,
        items = slice.Of(Enumerable.Reverse(bids)),
    });
}).RequireAuthorization(p => p.RequireRole(Roles.Auditor, Roles.AuctionAdmin, Roles.AwardCommittee));

app.MapGet("/audit/{offset:long}", async (
    long offset, IDbContextFactory<AuditDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);
    var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Offset == offset, ct);
    return entry is null ? Results.NotFound() : Results.Ok(AuditEntryResponse.From(entry));
}).RequireAuthorization(Policies.Auditor);

// The vocabulary, so an auditor can see what kinds of thing are recorded without
// knowing the services. Cheap: the action column is indexed and the cardinality is
// the number of things staff can do.
app.MapGet("/audit/actions", async (
    IDbContextFactory<AuditDbContext> f, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);

    var rows = await db.Entries.AsNoTracking()
        .Where(x => x.Action != null)
        .GroupBy(x => x.Action!)
        .Select(g => new { action = g.Key, count = g.LongCount(), last = g.Max(x => x.At) })
        .OrderByDescending(x => x.count)
        .ToListAsync(ct);

    return Results.Ok(new { items = rows });
}).RequireAuthorization(Policies.Auditor);

// --- verification ----------------------------------------------------------

// Recomputes the chain from the table and says whether it holds.
//
// This is the endpoint the whole design is for. Everything else here is a
// convenient view of rows in a database, which is to say something a sufficiently
// determined administrator could have written. This recomputes every hash from
// the payload beside it and reports the first link that does not follow — so an
// altered entry, or a deleted one, is a fact rather than a suspicion.
app.MapGet("/audit/verify", async (
    long? from, long? to,
    IDbContextFactory<AuditDbContext> f, IEventStream events, CancellationToken ct) =>
{
    await using var db = await f.CreateDbContextAsync(ct);

    var scope = db.Entries.AsNoTracking().AsQueryable();
    if (from is not null) scope = scope.Where(x => x.Offset >= from);
    if (to is not null) scope = scope.Where(x => x.Offset <= to);

    long? brokeAt = null;
    string? broke = null;
    var gaps = new List<object>();
    var count = 0L;
    long? firstOffset = null;
    long? previousOffset = null;
    var head = new byte[LedgerChain.HashLength];

    var first = await scope.OrderBy(x => x.Offset).FirstOrDefaultAsync(ct);

    // An empty range is not an early return with a different answer, and that is
    // not tidiness.
    //
    // The first version returned a shorter object here, which meant the one state
    // worth shouting about — the table empty while the topic holds four hundred
    // records — reported `intact: true` and left out the two numbers that would
    // have shown it. The shape is the same whether there is anything to check or
    // not, so the comparison below always happens.
    if (first is not null)
    {
        firstOffset = first.Offset;

        // Where the chain stood before the range begins. For a verify over the
        // whole table that is the zero head; for a bounded one it is the previous
        // entry's hash, so a page can be checked without rereading the trail from
        // the start.
        var predecessor = await db.Entries.AsNoTracking()
            .Where(x => x.Offset < first.Offset)
            .OrderByDescending(x => x.Offset)
            .FirstOrDefaultAsync(ct);

        var chain = predecessor is null ? new LedgerChain() : new LedgerChain(predecessor.Hash);
        previousOffset = predecessor?.Offset;

        // Paged by offset rather than Skip/Take: the table only grows at the end, so
        // a keyset walk cannot miss or repeat a row, and it does not degrade over a
        // trail with years in it.
        var cursor = first.Offset - 1;
        const int BatchSize = 1000;

        while (brokeAt is null)
        {
            var batch = await scope
                .Where(x => x.Offset > cursor)
                .OrderBy(x => x.Offset)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var entry in batch)
            {
                if (previousOffset is not null && entry.Offset != previousOffset + 1)
                    gaps.Add(new { after = previousOffset, before = entry.Offset });

                previousOffset = entry.Offset;
                count++;

                // The link: this entry says what it was built on, and the chain says
                // what it should have been built on.
                if (!CryptographicOperations.FixedTimeEquals(entry.PreviousHash, chain.Head))
                {
                    brokeAt = entry.Offset;
                    broke = "previous-hash";
                    break;
                }

                // The content: the hash recomputed from the stored payload.
                if (!CryptographicOperations.FixedTimeEquals(
                        chain.Append(entry.Frame()), entry.Hash))
                {
                    brokeAt = entry.Offset;
                    broke = "hash";
                    break;
                }

                // The projection: the columns an auditor reads, against the payload
                // the chain protects. The hash does not cover them — it does not
                // need to, since they are derived from it — so this is the check
                // that closes the one alteration the chain alone would let through:
                // the Details of an approval rewritten while the evidence beside it
                // still says otherwise.
                if (!entry.ProjectionMatchesPayload())
                {
                    brokeAt = entry.Offset;
                    broke = "projection";
                    break;
                }
            }

            cursor = batch[^1].Offset;
        }

        head = chain.Head.ToArray();
    }

    // The one tampering a hash chain cannot see on its own: entries removed from
    // the end leave a shorter chain that verifies perfectly. The broker can,
    // because it still holds the records. Where retention has already passed, this
    // is -1 or short and says nothing — which is honest, and is the reason the
    // topic's retention is a compliance decision rather than a tuning one.
    long topicEnd;
    try
    {
        topicEnd = await events.LatestOffsetAsync(Topics.StaffActions, ct);
    }
    catch (Exception)
    {
        topicEnd = -1;
    }

    var stored = await db.Entries.AsNoTracking().MaxAsync(x => (long?)x.Offset, ct) ?? -1;

    return Results.Ok(new
    {
        intact = brokeAt is null && gaps.Count == 0,
        checkedEntries = count,
        firstOffset,
        lastOffset = previousOffset,
        head = Convert.ToHexString(head),
        brokeAt,
        broke,
        gaps,

        // Not folded into `intact`: a trail that is intact as far as it goes, with
        // records on the topic it has not yet written, is the ordinary state of a
        // service a second behind. A caller that cares about the difference between
        // "behind" and "truncated" has both numbers. `storedThrough` is the whole
        // table, not the range, because a bounded verify says nothing about the end
        // of the trail.
        storedThrough = stored,
        topicEnd,
        missingTail = topicEnd > stored ? topicEnd - stored : 0,
    });
}).RequireAuthorization(Policies.Auditor);

app.Run();

// ---------------------------------------------------------------------------

/// <summary>
/// One entry as an auditor reads it.
///
/// The payload goes out beside the projected fields, not instead of them. It is
/// what the hash covers, so a reader who wants to check an entry rather than trust
/// it needs the bytes — and a reader who only wants to know who did what should not
/// have to parse JSON to find out.
/// </summary>
/// <summary>One bid as recorded, and what the processor made of it.</summary>
public sealed record BidHistoryEntry(
    long Offset, DateTimeOffset At, Guid BidderId, long AmountMinorUnits,
    string Channel, Guid? EnteredBy, Guid ClientBidId, bool Accepted, string? RejectionReason)
{
    /// <summary>Null for a frame too short to carry the server's metadata.</summary>
    public static BidHistoryEntry? Decode(LoggedBid bid, IReadOnlyDictionary<Guid, string?> refused)
    {
        var frame = bid.Frame.Span;
        if (frame.Length < BidFrame.ServerLength) return null;

        var clientBidId = BidFrame.ClientBidId(frame);
        var channel = BidFrame.Channel(frame);
        refused.TryGetValue(clientBidId, out var reason);
        var wasRefused = refused.ContainsKey(clientBidId);
        return new BidHistoryEntry(
            bid.Offset,
            DateTimeOffset.FromUnixTimeMilliseconds(BidFrame.ServerTimestamp(frame)),
            BidFrame.BidderId(frame),
            BidFrame.Amount(frame),
            channel.ToString(),
            channel == BidChannel.Onsite ? BidFrame.EnteredBy(frame) : null,
            clientBidId,
            !wasRefused,
            wasRefused ? reason ?? "Rejected" : null);
    }
}

public sealed record AuditEntryResponse(
    long Offset, string EventType, string Key,
    Guid? ActorSubject, string? ActorRoles, string? Action, string? Subject,
    string? Details, string? SourceAddress, DateTimeOffset? At,
    bool Malformed, DateTimeOffset RecordedAt,
    string Hash, string PreviousHash, string Payload)
{
    public static AuditEntryResponse From(AuditEntry e) => new(
        e.Offset, e.EventType, e.Key,
        e.ActorSubject, e.ActorRoles, e.Action, e.Subject,
        e.Details, e.SourceAddress, e.At,
        e.Malformed, e.RecordedAt,
        Convert.ToHexString(e.Hash), Convert.ToHexString(e.PreviousHash), e.Payload);
}
