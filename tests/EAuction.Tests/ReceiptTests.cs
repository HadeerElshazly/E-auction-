using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EAuction.BidCatcher;
using EAuction.Core;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The receipt a bidder gets back, and the key that signs it (D-21).
///
/// A receipt is evidence: it is how a bidder later shows that a bid of this amount
/// was accepted at this time and landed at this offset in the ledger. That holds
/// only while the key signing it is a secret. The catcher used to fall back to a
/// constant written in its own source file when none was configured — so every
/// deployment that forgot to set one signed with a key anybody reading the
/// repository could forge, and nothing anywhere would have said so.
///
/// These tests pin both halves: the key comes from configuration, and the bytes it
/// covers are the ones a verifier would have to reconstruct.
/// </summary>
public class ReceiptTests
{
    private const string ReceiptKeyHex =
        "a1a2a3a4a5a6a7a8a9aaabacadaeaf b0b1b2b3b4b5b6b7b8b9babbbcbdbebf";

    private static byte[] Key => Convert.FromHexString(ReceiptKeyHex.Replace(" ", ""));

    private static AuthenticatedFactory<Program> Catcher() => new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["Catcher:ReceiptKeyHex"] = ReceiptKeyHex.Replace(" ", ""),
        },
    };

    private static HttpContent Body(byte[] frame)
    {
        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    [Fact]
    public async Task The_receipt_is_signed_with_the_configured_key()
    {
        using var factory = Catcher();

        var bidder = Guid.NewGuid();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);
        var state = factory.Services.GetRequiredService<CatcherState>();

        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);

        var frame = TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now);
        var response = await client.PostAsync("/bids", Body(frame));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<BidReceipt>();

        // Reconstructed exactly as a verifier would have to: the client frame as
        // sent, the server metadata the catcher stamped on it, then the offset. If
        // the catcher's own layout drifts from what a holder of a receipt can
        // rebuild, the receipt stops being checkable by anyone but the catcher —
        // which defeats the point of signing it.
        var payload = new byte[BidFrame.ServerLength + 8];
        frame.CopyTo(payload, 0);
        BidFrame.AppendServerMetadata(
            payload, receipt.ServerTimestampMs, podOrdinal: 0, BidChannel.Online, Guid.Empty);
        BinaryPrimitives.WriteInt64LittleEndian(
            payload.AsSpan(BidFrame.ServerLength), receipt.Offset);

        var expected = Convert.ToHexString(HMACSHA256.HashData(Key, payload));

        Assert.Equal(expected, receipt.Signature);
    }

    [Fact]
    public async Task A_receipt_does_not_verify_under_a_different_key()
    {
        // The assertion above would pass against a hard-coded key too. This is the
        // half that says the configured one is actually the one in use.
        using var factory = Catcher();

        var bidder = Guid.NewGuid();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);
        var state = factory.Services.GetRequiredService<CatcherState>();

        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);

        var frame = TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now);
        var response = await client.PostAsync("/bids", Body(frame));
        var receipt = await response.Content.ReadFromJsonAsync<BidReceipt>();

        var payload = new byte[BidFrame.ServerLength + 8];
        frame.CopyTo(payload, 0);
        BidFrame.AppendServerMetadata(
            payload, receipt.ServerTimestampMs, podOrdinal: 0, BidChannel.Online, Guid.Empty);
        BinaryPrimitives.WriteInt64LittleEndian(
            payload.AsSpan(BidFrame.ServerLength), receipt.Offset);

        // The constant the catcher used to fall back to, which is still in the
        // repository's history and must no longer verify anything.
        var retired = Convert.FromHexString(
            "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

        Assert.NotEqual(
            Convert.ToHexString(HMACSHA256.HashData(retired, payload)),
            receipt.Signature);
    }

    [Fact]
    public async Task Two_bids_get_different_signatures()
    {
        // A signature that did not actually cover the frame would be constant, and
        // every assertion above would still pass.
        using var factory = Catcher();

        var bidder = Guid.NewGuid();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);
        var state = factory.Services.GetRequiredService<CatcherState>();

        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);

        var first = await (await client.PostAsync(
            "/bids", Body(TestAuction.Frame(auction.AuctionId, bidder, 1_200_000_00, now))))
            .Content.ReadFromJsonAsync<BidReceipt>();

        var second = await (await client.PostAsync(
            "/bids", Body(TestAuction.Frame(auction.AuctionId, bidder, 1_300_000_00, now))))
            .Content.ReadFromJsonAsync<BidReceipt>();

        Assert.NotEqual(first.Signature, second.Signature);
    }
}

/// <summary>
/// The certificate a bidder can show afterwards (شهادة مزايدة).
///
/// The receipt handed over at the gavel is not checkable by the person holding it:
/// the signature covers the frame, the frame is not in the receipt, and the key
/// that would verify it is secret by design. So the certificate is read back out of
/// the append-only log rather than recomputed from what the caller presents — which
/// also makes it a stronger claim, because it says the bid is in the record at that
/// position and not merely that someone could compute an HMAC.
/// </summary>
public class BidCertificateTests
{
    private const string ReceiptKeyHex =
        "a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf00";

    private static AuthenticatedFactory<Program> Catcher() => new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["Catcher:ReceiptKeyHex"] = ReceiptKeyHex,
        },
    };

    private static HttpContent Body(byte[] frame)
    {
        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    /// <summary>Places one bid and returns what the bidder was handed for it.</summary>
    private static async Task<(BidReceipt Receipt, Guid Auction, Guid Bidder, long Amount)>
        PlaceBidAsync(AuthenticatedFactory<Program> factory, long amount = 1_200_000_00)
    {
        var bidder = Guid.NewGuid();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);
        var state = factory.Services.GetRequiredService<CatcherState>();

        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);

        var response = await client.PostAsync(
            "/bids", Body(TestAuction.Frame(auction.AuctionId, bidder, amount, now)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<BidReceipt>();
        return (receipt, auction.AuctionId, bidder, amount);
    }

    private static string Path(Guid auction, long offset, string? signature = null) =>
        $"/auctions/{auction}/bids/{offset}/certificate"
        + (signature is null ? "" : $"?signature={signature}");

    [Fact]
    public async Task A_bidder_can_get_a_certificate_for_their_own_bid()
    {
        using var factory = Catcher();
        var (receipt, auction, bidder, amount) = await PlaceBidAsync(factory);

        var certificate = await factory.CreateClient().As(bidder, Roles.Bidder)
            .GetFromJsonAsync<BidCertificate>(Path(auction, receipt.Offset));

        Assert.NotNull(certificate);
        Assert.Equal(auction, certificate.AuctionId);
        Assert.Equal(bidder, certificate.BidderId);
        Assert.Equal(amount, certificate.AmountMinorUnits);
        Assert.Equal(receipt.ClientBidId, certificate.ClientBidId);
        Assert.Equal(receipt.Offset, certificate.Offset);
        Assert.Equal("Online", certificate.Channel);
        Assert.Null(certificate.EnteredByUserId);

        // The whole point: the certificate and the receipt agree, which is what
        // makes a receipt saved at the time worth anything later.
        Assert.Equal(receipt.Signature, certificate.Signature);
    }

    [Fact]
    public async Task The_certificate_quotes_a_reference_a_person_can_read_out()
    {
        using var factory = Catcher();
        var (receipt, auction, bidder, _) = await PlaceBidAsync(factory);

        var certificate = await factory.CreateClient().As(bidder, Roles.Bidder)
            .GetFromJsonAsync<BidCertificate>(Path(auction, receipt.Offset));

        Assert.StartsWith("EA-", certificate!.Reference);
        Assert.EndsWith($"-{receipt.Offset}", certificate.Reference);
    }

    [Fact]
    public async Task Another_bidder_cannot_read_it()
    {
        // It carries an amount and a bidder id. A rival reading it would learn both.
        using var factory = Catcher();
        var (receipt, auction, _, _) = await PlaceBidAsync(factory);

        var response = await factory.CreateClient().As(Guid.NewGuid(), Roles.Bidder)
            .GetAsync(Path(auction, receipt.Offset));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_browse_certificates_without_a_receipt()
    {
        // The committee has a legitimate reason to check a receipt someone hands
        // them. That is not the same as reading the ladder off the log one offset at
        // a time, which is what an unconditional staff permission would allow.
        using var factory = Catcher();
        var (receipt, auction, _, _) = await PlaceBidAsync(factory);

        var response = await factory.CreateClient()
            .As(Guid.NewGuid(), Roles.AwardCommittee, Roles.AuctionAdmin)
            .GetAsync(Path(auction, receipt.Offset));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Staff_can_check_a_receipt_that_was_handed_to_them()
    {
        using var factory = Catcher();
        var (receipt, auction, bidder, _) = await PlaceBidAsync(factory);

        var certificate = await factory.CreateClient().As(Guid.NewGuid(), Roles.AwardCommittee)
            .GetFromJsonAsync<BidCertificate>(Path(auction, receipt.Offset, receipt.Signature));

        Assert.NotNull(certificate);
        Assert.True(certificate.PresentedSignatureMatched);
        Assert.Equal(bidder, certificate.BidderId);
    }

    [Fact]
    public async Task A_forged_receipt_does_not_open_the_door()
    {
        using var factory = Catcher();
        var (receipt, auction, _, _) = await PlaceBidAsync(factory);

        foreach (var forged in new[]
                 {
                     new string('A', 64),
                     receipt.Signature[..62] + "FF",
                     "not-hex-at-all",
                     "",
                 })
        {
            var response = await factory.CreateClient().As(Guid.NewGuid(), Roles.AwardCommittee)
                .GetAsync(Path(auction, receipt.Offset, forged));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_bidder_reading_their_own_is_told_nothing_was_presented()
    {
        // Null, not false. "You did not show me a receipt" and "the receipt you
        // showed me is wrong" are different answers, and a certificate that said
        // false to the first would look like a failed verification.
        using var factory = Catcher();
        var (receipt, auction, bidder, _) = await PlaceBidAsync(factory);

        var certificate = await factory.CreateClient().As(bidder, Roles.Bidder)
            .GetFromJsonAsync<BidCertificate>(Path(auction, receipt.Offset));

        Assert.Null(certificate!.PresentedSignatureMatched);
    }

    [Fact]
    public async Task An_offset_with_no_bid_is_not_found()
    {
        using var factory = Catcher();
        var (receipt, auction, bidder, _) = await PlaceBidAsync(factory);

        foreach (var offset in new[] { receipt.Offset + 50, -1L })
        {
            var response = await factory.CreateClient().As(bidder, Roles.Bidder)
                .GetAsync(Path(auction, offset));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_anonymous_caller_gets_nothing()
    {
        using var factory = Catcher();
        var (receipt, auction, _, _) = await PlaceBidAsync(factory);

        var response = await factory.CreateClient().Anonymous()
            .GetAsync(Path(auction, receipt.Offset));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_the_same_certificate_again_is_not_refused()
    {
        // A browser asks twice for ordinary reasons — a re-render, a second click, a
        // reload — and the bucket holds two. The record at an offset cannot change,
        // so the repeat is answered from memory and costs nothing to serve; metering
        // it turned a bidder opening their own certificate into a 429.
        using var factory = Catcher();
        var (receipt, auction, bidder, _) = await PlaceBidAsync(factory);
        var client = factory.CreateClient().As(bidder, Roles.Bidder);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await client.GetAsync(Path(auction, receipt.Offset));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Reading_one_offset_after_another_is_still_refused()
    {
        // The cache answers a repeat, not a sweep. Every distinct offset is a seek
        // into the log, and walking the ladder one offset at a time is what the
        // limit exists to stop — so a miss still costs a token even though a hit
        // does not.
        using var factory = Catcher();
        var bidder = Guid.NewGuid();
        var client = factory.CreateClient().As(bidder, Roles.Bidder);
        var state = factory.Services.GetRequiredService<CatcherState>();

        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30));
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);

        var offsets = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var placed = await client.PostAsync(
                "/bids",
                Body(TestAuction.Frame(
                    auction.AuctionId, bidder, 1_200_000_00 + i * 100, now)));
            Assert.Equal(HttpStatusCode.Accepted, placed.StatusCode);
            offsets.Add((await placed.Content.ReadFromJsonAsync<BidReceipt>())!.Offset);
        }

        var answers = new List<HttpStatusCode>();
        foreach (var offset in offsets)
            answers.Add((await client.GetAsync(Path(auction.AuctionId, offset))).StatusCode);

        // Two tokens, four fresh offsets: the first two are served and a later one
        // is turned away. Which one depends on how fast the bucket refilled, so the
        // assertion is on there being a refusal rather than on its position.
        Assert.Equal(HttpStatusCode.OK, answers[0]);
        Assert.Equal(HttpStatusCode.OK, answers[1]);
        Assert.Contains(HttpStatusCode.TooManyRequests, answers);
    }
}
