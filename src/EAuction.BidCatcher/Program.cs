using System.Buffers;
using System.Security.Cryptography;
using EAuction.BidCatcher;
using EAuction.Core;
using EAuction.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// Log backend. Kafka in deployment; the in-memory log mirrors the same
// ordering contract and is used where no broker is available (dev, tests).
var bootstrap = builder.Configuration["Kafka:BootstrapServers"];
IBidLog bidLog = string.IsNullOrWhiteSpace(bootstrap)
    ? new InMemoryBidLog()
    : new KafkaBidLog(new KafkaBidLogOptions { BootstrapServers = bootstrap });

// Shared with the participant service, from the cluster's secret store. Both
// derive the same per-bidder signing key from it, so no secret ever travels on
// a topic (see BidderKeys).
var bidderMasterKeyHex = builder.Configuration["Catcher:BidderMasterKeyHex"];
if (string.IsNullOrWhiteSpace(bidderMasterKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Catcher:BidderMasterKeyHex is required. Without the same master key the "
            + "participant service uses, no bidder's signature can be verified.");

    bidderMasterKeyHex = Convert.ToHexString(BidderKeys.NewMasterKey());
}

var state = new CatcherState(Convert.FromHexString(bidderMasterKeyHex))
{
    MaxBidsPerSecondPerBidder =
        builder.Configuration.GetValue("Catcher:MaxBidsPerSecondPerBidder", 20),
    CeilingGrace = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Catcher:CeilingGraceSeconds", 60))
};
var podOrdinal = builder.Configuration.GetValue("Catcher:PodOrdinal", 0);

// Signs the receipt handed back to the bidder, so they can later prove their
// bid was accepted at time T for amount X (D-21).
//
// Guarded exactly like the master key above, and for the same reason. A receipt
// is evidence; a receipt signed with a key that is in this file is evidence of
// nothing, because anyone reading the repository can forge one. A committed
// fallback would also fail silently — every receipt would verify, against the
// wrong key, and nobody would find out until a bidder disputed a bid.
var receiptKeyHex = builder.Configuration["Catcher:ReceiptKeyHex"];
if (string.IsNullOrWhiteSpace(receiptKeyHex))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Catcher:ReceiptKeyHex is required. Without it bid receipts would be signed "
            + "with a key nobody set, and could not be used as evidence.");

    receiptKeyHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

var receiptKey = Convert.FromHexString(receiptKeyHex);

builder.Services.AddSingleton(state);
builder.Services.AddSingleton(bidLog);

// Fills the state above from the compacted control topics. Without it the
// catcher starts empty and rejects every bid as an unknown auction.
IEventStream eventStream = string.IsNullOrWhiteSpace(bootstrap)
    ? new InMemoryEventStream()
    : new KafkaEventStream(new KafkaEventStreamOptions
    {
        BootstrapServers = bootstrap,
        ConsumerGroup = "bid-catcher"
    });

builder.Services.AddSingleton(eventStream);
builder.Services.AddSingleton(sp => new ControlPlane(
    sp.GetRequiredService<CatcherState>(),
    sp.GetRequiredService<IEventStream>(),
    sp.GetRequiredService<ILogger<ControlPlane>>())
{
    MinimumWarmUp = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Catcher:MinimumWarmUpSeconds", 8))
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlPlane>());

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);
builder.Services.AddEAuctionCors(builder.Configuration);

var app = builder.Build();

app.UseEAuctionCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok("ok")).AllowAnonymous();

// Not ready until the control topics have been replayed: a catcher that
// accepts traffic with empty state can only reject it.
app.MapGet("/health/ready", (ControlPlane control) =>
    control.Warm ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();

// ---------------------------------------------------------------------------
// Development only. In deployment this state arrives from the compacted topics
// auctions.upcoming and auctions.participants; there is no write API for it.
//
// Two locks, not one. The flag is off by default, and production refuses it even
// when set: this endpoint is anonymous and takes a signing secret as an argument,
// so anyone who could reach it could grant themselves the right to bid as anyone.
// A flag that can be turned on by a stray environment variable is not a lock.
// ---------------------------------------------------------------------------
if (builder.Configuration.GetValue("Catcher:EnableDevSeed", false))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Catcher:EnableDevSeed cannot be used in production: /dev/seed is anonymous "
            + "and accepts a bidder's signing secret as a parameter.");

    app.MapPost("/dev/seed", (SeedRequest request) =>
    {
        var now = DateTimeOffset.UtcNow;
        var auction = new AuctionDefinition
        {
            AuctionId = request.AuctionId,
            StartsAt = now.AddMinutes(-5),
            EndsAt = now.AddMinutes(request.DurationMinutes),
            OpeningPriceMinorUnits = 1_000_000_00,
            ReservePriceMinorUnits = 1_500_000_00,
            Increment = new IncrementPolicy.Fixed(50_000_00),
            QuietPeriod = TimeSpan.FromMinutes(2),
            MaxExtensions = 3
        };

        state.UpsertAuction(auction);
        foreach (var bidder in request.Bidders)
            state.GrantEligibilityWithSecret(
                request.AuctionId, bidder, Convert.FromHexString(request.SecretHex));

        return Results.Ok(new { seeded = request.Bidders.Length });
    }).AllowAnonymous();
}


// ---------------------------------------------------------------------------
// The hot path.
//
// Nothing below makes a network call to another service. JWT verification (D-19)
// happens in the authentication middleware above, offline against cached JWKS with
// a validated-token cache in front of it — no call to Keycloak, per request or ever,
// on this path. The HMAC signature (D-20) is what binds the bid to the bidder, and
// the token is what binds the caller to the bidder id in the frame.
// ---------------------------------------------------------------------------
app.MapPost("/bids", async (HttpContext http, CancellationToken ct) =>
{
    var buffer = ArrayPool<byte>.Shared.Rent(BidFrame.ServerLength);
    try
    {
        var read = await ReadExactAsync(http.Request.Body, buffer, BidFrame.ClientLength, ct);
        if (read != BidFrame.ClientLength)
            return Results.BadRequest(new { reason = nameof(RejectionReason.MalformedFrame) });

        var now = DateTimeOffset.UtcNow;
        var frame = buffer.AsMemory(0, BidFrame.ServerLength);

        var caller = http.User.SubjectId();
        if (caller is null)
            return Results.Json(
                new { reason = "BidderMismatch" }, statusCode: StatusCodes.Status403Forbidden);

        // Which of the two kinds of caller this is depends on the auction, not on
        // anything the caller said. An unknown auction is refused here rather than
        // guessed at as online, because guessing would let a frame for an auction
        // this pod has not replayed yet take the online path.
        var onsite = state.IsOnsite(BidFrame.AuctionId(frame.Span));
        if (onsite is null)
            return Results.Json(
                new { reason = nameof(RejectionReason.UnknownAuction) }, statusCode: 409);

        Guid? clerk = null;

        if (onsite.Value)
        {
            // In the hall the clerk types the bid, so the caller is not the bidder
            // and never can be. What binds the record is the pair: the frame says
            // whose bid it is, the token says who entered it, and both are written
            // into the ledger (§29).
            if (!http.User.IsInRole(Roles.Operator) || !state.IsClerkFor(
                    BidFrame.AuctionId(frame.Span), caller.Value))
                return Results.Json(
                    new { reason = nameof(RejectionReason.NotTheClerk) },
                    statusCode: StatusCodes.Status403Forbidden);

            clerk = caller;
        }
        else
        {
            // The token says who is calling; the frame says who the bid is for.
            // They must be the same person, or a bidder could spend someone else's
            // deposit — the signature alone would not catch it, because an
            // eligible bidder's own key signs any frame they choose to build.
            if (caller != BidFrame.BidderId(frame.Span))
                return Results.Json(
                    new { reason = "BidderMismatch" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var screen = state.Screen(frame.Span, now, clerk);
        if (screen != RejectionReason.None)
            return Results.Json(new { reason = screen.ToString() },
                statusCode: screen == RejectionReason.NotTheClerk
                    ? StatusCodes.Status403Forbidden
                    : 409);

        // Server-written, both of them. A client that could choose its own channel
        // could claim a hall bid was online, or stamp another clerk's id on its own.
        BidFrame.AppendServerMetadata(
            frame.Span,
            now.ToUnixTimeMilliseconds(),
            podOrdinal,
            clerk is null ? BidChannel.Online : BidChannel.Onsite,
            clerk ?? Guid.Empty);

        // acks=all: this does not return until the bid is durable (D-11).
        var offset = await bidLog.AppendAsync(BidFrame.AuctionId(frame.Span), frame, ct);

        var receipt = BuildReceipt(frame.Span, offset, receiptKey);

        // 202, not 200: recorded, not yet judged. The processor's verdict
        // arrives over the push channel (§7.2).
        return Results.Accepted(value: receipt);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}).RequireAuthorization(Policies.SubmitsBids);

// ---------------------------------------------------------------------------
// The certificate (شهادة مزايدة). Not the hot path.
//
// Deliberately not issued from what the caller presents. The frame is read back
// out of the append-only log at the offset given, and every field on the
// certificate comes from that record — so the certificate says "this bid is in
// the legal record at this position, for this amount", which is the claim a
// bidder actually needs. Recomputing an HMAC over a payload the caller supplied
// would prove only that the caller could supply a payload.
//
// Each call opens a consumer, seeks and reads one record. That is cheap but it is
// not free, and this service is the one with a 50ms budget on its other endpoint,
// so the read is rate-limited per caller. If this ever carries real traffic it
// belongs in its own service rather than beside the bid path.
//
// The read, and not the request. A bid at a given offset is a record in an
// append-only log: the frame can never change, so the second person to ask for it
// — or the same person asking twice — can be answered from memory without touching
// Kafka at all. Charging a token for a request that costs nothing would be
// charging for the wrong thing, and it showed: a bidder opening their own
// certificate got a 429, because a browser legitimately fetches twice (React's
// development double-render, a re-render, or simply a second click) and the bucket
// holds two.
//
// Note what is NOT cached: the certificate itself. That depends on the caller and
// on the signature they presented, so it is rebuilt per request from the cached
// frame — the access check below must never be served from a cache keyed on
// anything but the record.
// ---------------------------------------------------------------------------
// Two meters, because a hit and a miss cost different things.
//
// The read is what is expensive: a miss opens a consumer, seeks and reads one
// record, on the service that owes /bids a 50ms budget. That stays at two a second.
//
// But a hit is not free either, and the first version of this cache let it be
// unlimited: every cached request still recomputes an HMAC over the frame, decodes
// a presented signature and serialises a certificate, so one caller in a loop could
// still take the budget away from the bid path — and could do it against the
// `?signature=` comparison without ever being slowed down. The floor applies to
// every request, cached or not, and is loose enough that no person browsing their
// own certificates will ever meet it.
var certificateFloor = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TokenBucket>();
var certificateLimits = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TokenBucket>();
var certificateFrames = new FrameCache(capacity: 2048);

app.MapGet("/auctions/{auctionId:guid}/bids/{offset:long}/certificate", async (
    Guid auctionId, long offset, string? signature,
    HttpContext http, IBidLog log, CancellationToken ct) =>
{
    var caller = http.User.SubjectId();
    if (caller is null) return Results.Forbid();

    if (offset < 0) return Results.NotFound();

    // Every request, whether or not it reaches Kafka.
    var floor = certificateFloor.GetOrAdd(caller.Value, _ => new TokenBucket(20));
    if (!floor.TryTake(DateTimeOffset.UtcNow))
        return Results.Json(
            new { reason = nameof(RejectionReason.RateLimited) }, statusCode: 429);

    if (!certificateFrames.TryGet(auctionId, offset, out var frame))
    {
        // Only a miss reaches Kafka, so only a miss costs a token.
        var bucket = certificateLimits.GetOrAdd(caller.Value, _ => new TokenBucket(2));
        if (!bucket.TryTake(DateTimeOffset.UtcNow))
            return Results.Json(
                new { reason = nameof(RejectionReason.RateLimited) }, statusCode: 429);

        var read = await ReadFrameAsync(log, auctionId, offset, ct);
        if (read is null) return Results.NotFound();

        frame = read.Value;
        certificateFrames.Put(auctionId, offset, frame);
    }

    // Built before the access check, because the check needs the recorded bidder
    // and the recomputed signature — both of which come out of the frame.
    var certificate = BuildCertificate(frame, offset, signature, receiptKey);

    // A bidder may read their own. Staff may check one that has been handed to
    // them, which is what a dispute looks like — but only by presenting the
    // signature from the receipt, so this is not a tool for browsing who bid what.
    var isOwner = caller == certificate.BidderId;
    var isStaffWithReceipt =
        http.User.IsInAnyRole(Roles.AwardCommittee, Roles.AuctionAdmin)
        && certificate.PresentedSignatureMatched == true;

    return isOwner || isStaffWithReceipt
        ? Results.Ok(certificate)
        : Results.Forbid();
}).RequireAuthorization();

app.Run();

/// <summary>
/// The one record at <paramref name="offset"/>, or null if the log does not have
/// one there. Bounded by a short timeout: ReadAsync follows the tail once it has
/// caught up, so a request for an offset that does not exist yet would otherwise
/// wait for a bid that may never come.
/// </summary>
static async Task<ReadOnlyMemory<byte>?> ReadFrameAsync(
    IBidLog log, Guid auctionId, long offset, CancellationToken ct)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));

    try
    {
        await foreach (var record in log.ReadAsync(auctionId, offset, timeout.Token))
            if (record.Offset == offset)
                return record.Frame;
    }
    catch (OperationCanceledException)
    {
        // No record at that offset within the window.
    }

    return null;
}

static async ValueTask<int> ReadExactAsync(
    Stream body, byte[] buffer, int count, CancellationToken ct)
{
    var total = 0;
    while (total < count)
    {
        var n = await body.ReadAsync(buffer.AsMemory(total, count - total), ct);
        if (n == 0) break;
        total += n;
    }
    return total;
}

static BidReceipt BuildReceipt(ReadOnlySpan<byte> frame, long offset, byte[] receiptKey) =>
    new(BidFrame.AuctionId(frame),
        BidFrame.BidderId(frame),
        BidFrame.ClientBidId(frame),
        offset,
        BidFrame.ServerTimestamp(frame),
        SignatureFor(frame, offset, receiptKey));

/// <summary>
/// What a receipt signs: the recorded frame and where it landed.
///
/// One definition, used both when the receipt is handed over and when a
/// certificate is issued months later. Two copies that drifted would make every
/// certificate disagree with the receipt it is supposed to confirm.
/// </summary>
static BidCertificate BuildCertificate(
    ReadOnlyMemory<byte> frame, long offset, string? presented, byte[] receiptKey)
{
    // Takes Memory rather than Span: the caller is an async handler, and a ref
    // struct cannot be a local there.
    var span = frame.Span;
    var expected = SignatureFor(span, offset, receiptKey);
    var enteredBy = BidFrame.EnteredBy(span);
    var auctionId = BidFrame.AuctionId(span);

    return new BidCertificate
    {
        Reference = $"EA-{auctionId.ToString()[..8].ToUpperInvariant()}-{offset}",
        AuctionId = auctionId,
        BidderId = BidFrame.BidderId(span),
        ClientBidId = BidFrame.ClientBidId(span),
        Offset = offset,
        AmountMinorUnits = BidFrame.Amount(span),
        ClientTimestampMs = BidFrame.ClientTimestamp(span),
        ServerTimestampMs = BidFrame.ServerTimestamp(span),
        Channel = BidFrame.Channel(span).ToString(),
        EnteredByUserId = enteredBy == Guid.Empty ? null : enteredBy,
        Signature = expected,
        PresentedSignatureMatched = presented is null
            ? null
            : CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected), SafeHex(presented)),
        IssuedAt = DateTimeOffset.UtcNow,
    };
}

static string SignatureFor(ReadOnlySpan<byte> frame, long offset, byte[] receiptKey)
{
    Span<byte> payload = stackalloc byte[BidFrame.ServerLength + 8];
    frame[..BidFrame.ServerLength].CopyTo(payload);
    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
        payload[BidFrame.ServerLength..], offset);

    Span<byte> signature = stackalloc byte[32];
    HMACSHA256.HashData(receiptKey, payload, signature);
    return Convert.ToHexString(signature);
}

/// <summary>
/// Hex that may not be hex. A malformed signature is a mismatch, not a 500.
/// </summary>
static byte[] SafeHex(string value)
{
    try
    {
        return Convert.FromHexString(value);
    }
    catch (FormatException)
    {
        return [];
    }
}

public partial class Program;

internal sealed record SeedRequest(
    Guid AuctionId, Guid[] Bidders, string SecretHex, int DurationMinutes = 60);
