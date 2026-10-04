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
var receiptKey = Convert.FromHexString(
    builder.Configuration["Catcher:ReceiptKeyHex"]
    ?? "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

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
builder.Services.AddSingleton<ControlPlane>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlPlane>());

builder.Services.AddEAuctionJwt(builder.Configuration, builder.Environment);

var app = builder.Build();

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
// Guarded by configuration so it cannot be reached in a real environment.
// ---------------------------------------------------------------------------
if (builder.Configuration.GetValue("Catcher:EnableDevSeed", false))
{
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
// Nothing below makes a network call to another service. JWT verification
// (D-19) belongs between the read and the screen, against cached JWKS —
// offline, no call to Keycloak. It is not wired in this slice; the HMAC
// signature (D-20) is, and is what binds the bid to the bidder.
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

        // The token says who is calling; the frame says who the bid is for.
        // They must be the same person, or a bidder could spend someone else's
        // deposit — the signature alone would not catch it, because an
        // eligible bidder's own key signs any frame they choose to build.
        var caller = http.User.SubjectId();
        if (caller is null || caller != BidFrame.BidderId(frame.Span))
            return Results.Json(
                new { reason = "BidderMismatch" }, statusCode: StatusCodes.Status403Forbidden);

        var screen = state.Screen(frame.Span, now);
        if (screen != RejectionReason.None)
            return Results.Json(new { reason = screen.ToString() }, statusCode: 409);

        BidFrame.AppendServerMetadata(
            frame.Span,
            now.ToUnixTimeMilliseconds(),
            podOrdinal,
            BidChannel.Online,
            Guid.Empty);

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
}).RequireAuthorization(Policies.Bidder);

app.Run();

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

static BidReceipt BuildReceipt(ReadOnlySpan<byte> frame, long offset, byte[] receiptKey)
{
    Span<byte> payload = stackalloc byte[BidFrame.ServerLength + 8];
    frame[..BidFrame.ServerLength].CopyTo(payload);
    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
        payload[BidFrame.ServerLength..], offset);

    Span<byte> signature = stackalloc byte[32];
    HMACSHA256.HashData(receiptKey, payload, signature);

    return new BidReceipt(
        BidFrame.AuctionId(frame),
        BidFrame.BidderId(frame),
        BidFrame.ClientBidId(frame),
        offset,
        BidFrame.ServerTimestamp(frame),
        Convert.ToHexString(signature));
}

public partial class Program;

internal sealed record SeedRequest(
    Guid AuctionId, Guid[] Bidders, string SecretHex, int DurationMinutes = 60);
