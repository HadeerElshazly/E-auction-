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
