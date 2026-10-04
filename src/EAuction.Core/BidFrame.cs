using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EAuction.Core;

/// <summary>
/// Fixed-width binary bid frame. The catcher reads fields at fixed offsets,
/// appends server metadata in place, and produces the same buffer to the log —
/// no object materialisation, no allocation on the hot path.
///
/// Layout (see docs/ARCHITECTURE.md §5):
///   0   16  auctionId         16  16  bidderId
///   32   8  amountMinorUnits  40   8  clientTimestamp
///   48  16  clientBidId       64   8  nonce
///   72  32  hmac over [0,72)
///   --- server appends ---
///   104  8  serverTimestamp  112   4  catcherPodOrdinal
///   116  1  channel          117  16  enteredByUserId
/// </summary>
public static class BidFrame
{
    public const int AuctionIdOffset = 0;
    public const int BidderIdOffset = 16;
    public const int AmountOffset = 32;
    public const int ClientTimestampOffset = 40;
    public const int ClientBidIdOffset = 48;
    public const int NonceOffset = 64;
    public const int HmacOffset = 72;

    /// <summary>Bytes covered by the HMAC.</summary>
    public const int SignedLength = 72;

    /// <summary>Frame size as sent by the client.</summary>
    public const int ClientLength = 104;

    public const int ServerTimestampOffset = 104;
    public const int PodOrdinalOffset = 112;
    public const int ChannelOffset = 116;
    public const int EnteredByOffset = 117;

    /// <summary>Frame size after the catcher appends server metadata.</summary>
    public const int ServerLength = 133;

    public static Guid AuctionId(ReadOnlySpan<byte> f) => new(f.Slice(AuctionIdOffset, 16));
    public static Guid BidderId(ReadOnlySpan<byte> f) => new(f.Slice(BidderIdOffset, 16));
    public static Guid ClientBidId(ReadOnlySpan<byte> f) => new(f.Slice(ClientBidIdOffset, 16));

    public static long Amount(ReadOnlySpan<byte> f) =>
        BinaryPrimitives.ReadInt64LittleEndian(f.Slice(AmountOffset, 8));

    public static long ClientTimestamp(ReadOnlySpan<byte> f) =>
        BinaryPrimitives.ReadInt64LittleEndian(f.Slice(ClientTimestampOffset, 8));

    public static long Nonce(ReadOnlySpan<byte> f) =>
        BinaryPrimitives.ReadInt64LittleEndian(f.Slice(NonceOffset, 8));

    public static ReadOnlySpan<byte> Hmac(ReadOnlySpan<byte> f) => f.Slice(HmacOffset, 32);

    public static long ServerTimestamp(ReadOnlySpan<byte> f) =>
        BinaryPrimitives.ReadInt64LittleEndian(f.Slice(ServerTimestampOffset, 8));

    public static BidChannel Channel(ReadOnlySpan<byte> f) => (BidChannel)f[ChannelOffset];

    public static Guid EnteredBy(ReadOnlySpan<byte> f) => new(f.Slice(EnteredByOffset, 16));

    /// <summary>
    /// Appends server metadata to a client frame already copied into
    /// <paramref name="buffer"/>. The buffer must be at least ServerLength.
    /// </summary>
    public static void AppendServerMetadata(
        Span<byte> buffer, long serverTimestamp, int podOrdinal,
        BidChannel channel, Guid enteredBy)
    {
        BinaryPrimitives.WriteInt64LittleEndian(
            buffer.Slice(ServerTimestampOffset, 8), serverTimestamp);
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(PodOrdinalOffset, 4), podOrdinal);
        buffer[ChannelOffset] = (byte)channel;
        enteredBy.TryWriteBytes(buffer.Slice(EnteredByOffset, 16));
    }

    /// <summary>Builds a client frame. Test and client-SDK helper, not hot path.</summary>
    public static byte[] BuildClientFrame(
        Guid auctionId, Guid bidderId, long amount, long clientTimestamp,
        Guid clientBidId, long nonce, ReadOnlySpan<byte> signingSecret)
    {
        var f = new byte[ClientLength];
        auctionId.TryWriteBytes(f.AsSpan(AuctionIdOffset, 16));
        bidderId.TryWriteBytes(f.AsSpan(BidderIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(AmountOffset, 8), amount);
        BinaryPrimitives.WriteInt64LittleEndian(
            f.AsSpan(ClientTimestampOffset, 8), clientTimestamp);
        clientBidId.TryWriteBytes(f.AsSpan(ClientBidIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(NonceOffset, 8), nonce);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(signingSecret, f.AsSpan(0, SignedLength), mac);
        mac.CopyTo(f.AsSpan(HmacOffset, 32));
        return f;
    }

    /// <summary>
    /// Verifies the frame signature in constant time. ~200ns, no allocation.
    /// </summary>
    public static bool VerifySignature(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> signingSecret)
    {
        if (frame.Length < ClientLength) return false;
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(signingSecret, frame[..SignedLength], expected);
        return CryptographicOperations.FixedTimeEquals(expected, frame.Slice(HmacOffset, 32));
    }
}

public enum BidChannel : byte
{
    Online = 0,
    Onsite = 1
}
