using System.Text.Json;
using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// A cross-language contract test for the bid frame.
///
/// The bidder portal builds and signs the frame in the browser, because the signing
/// key is the bidder's and signing it server-side would destroy the evidential value
/// of a signed bid. That means a second implementation of the wire format exists, in
/// TypeScript, and the two must agree byte for byte or every browser bid is rejected
/// as <c>InvalidSignature</c> — with nothing in either codebase looking wrong.
///
/// Both sides assert against one committed vector:
/// <c>web/shared/src/__fixtures__/bid-frame-vector.json</c>. This test pins the C#
/// side; <c>web/shared/src/bidFrame.test.ts</c> pins the TypeScript side.
///
/// The trap the vector exists to catch: <see cref="Guid.TryWriteBytes"/> writes
/// .NET's mixed-endian layout — the first three groups little-endian, the last eight
/// bytes in order — which is NOT RFC 4122 order. The GUIDs in the vector are chosen
/// so no group equals its own reverse, so getting that wrong changes the frame.
/// </summary>
public class FrameVectorTests
{
    private static Vector Load()
    {
        // Walk up to the repository root: the fixture is shared with the web
        // workspace, so it cannot live beside the test assembly.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EAuction.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(
            dir!.FullName, "web", "shared", "src", "__fixtures__", "bid-frame-vector.json");

        Assert.True(File.Exists(path), $"The shared frame vector is missing at {path}.");

        return JsonSerializer.Deserialize<Vector>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public void BuildClientFrame_matches_the_shared_vector()
    {
        var v = Load();

        var frame = BidFrame.BuildClientFrame(
            Guid.Parse(v.AuctionId),
            Guid.Parse(v.BidderId),
            v.AmountMinorUnits,
            v.ClientTimestampMs,
            Guid.Parse(v.ClientBidId),
            long.Parse(v.Nonce),
            Convert.FromHexString(v.SigningSecretHex));

        Assert.Equal(v.FrameHex, Convert.ToHexString(frame));
    }

    [Fact]
    public void The_vector_round_trips_through_the_readers()
    {
        // If the accessors disagree with the builder, the catcher would screen a
        // different bid from the one that was signed.
        var v = Load();
        var frame = Convert.FromHexString(v.FrameHex);

        Assert.Equal(Guid.Parse(v.AuctionId), BidFrame.AuctionId(frame));
        Assert.Equal(Guid.Parse(v.BidderId), BidFrame.BidderId(frame));
        Assert.Equal(Guid.Parse(v.ClientBidId), BidFrame.ClientBidId(frame));
        Assert.Equal(v.AmountMinorUnits, BidFrame.Amount(frame));
        Assert.Equal(v.ClientTimestampMs, BidFrame.ClientTimestamp(frame));
        Assert.Equal(long.Parse(v.Nonce), BidFrame.Nonce(frame));
        Assert.True(BidFrame.VerifySignature(frame, Convert.FromHexString(v.SigningSecretHex)));
    }

    [Fact]
    public void The_vector_would_catch_a_byte_order_mistake()
    {
        // Guards the vector itself. If someone replaced these GUIDs with palindromic
        // ones, both implementations could disagree about endianness and still pass.
        var v = Load();

        foreach (var id in new[] { v.AuctionId, v.BidderId, v.ClientBidId })
        {
            var guid = Guid.Parse(id);
            var dotnetOrder = guid.ToByteArray();

            // RFC 4122 order: the same bytes with the first three groups reversed.
            var rfcOrder = (byte[])dotnetOrder.Clone();
            Array.Reverse(rfcOrder, 0, 4);
            Array.Reverse(rfcOrder, 4, 2);
            Array.Reverse(rfcOrder, 6, 2);

            Assert.False(dotnetOrder.SequenceEqual(rfcOrder),
                $"{id} serialises identically in both byte orders, so it cannot catch "
                + "the mistake this vector exists to catch. Choose a GUID whose first "
                + "three groups each differ from their own reverse.");
        }
    }

    private sealed record Vector
    {
        public string AuctionId { get; init; } = "";
        public string BidderId { get; init; } = "";
        public string ClientBidId { get; init; } = "";
        public long AmountMinorUnits { get; init; }
        public long ClientTimestampMs { get; init; }
        public string Nonce { get; init; } = "";
        public string SigningSecretHex { get; init; } = "";
        public string FrameHex { get; init; } = "";
    }
}
