using System.Security.Cryptography;

namespace EAuction.Core;

/// <summary>
/// Derives a bidder's per-auction signing secret (D-20) instead of
/// distributing one.
///
/// The obvious design is for the participant service to mint a random secret
/// and publish it on <c>auctions.participants</c> for the catcher to read. That
/// puts a live credential on a Kafka topic, where it is readable by anything
/// with topic access and stays in the log until compaction gets to it — and
/// compaction is a background process with no deadline.
///
/// Deriving removes the problem rather than guarding it. Both services hold
/// the same master key, from the cluster's secret store and never from Kafka,
/// and each computes:
///
///     secret = HMAC-SHA256(masterKey, "eauction-bid-key-v1" || auctionId || bidderId || epoch)
///
/// The topic then carries only the eligibility fact and the epoch. Someone who
/// reads the whole topic learns who may bid, not how to bid as them.
///
/// The epoch exists so one bidder's key can be rotated — a lost phone, a
/// suspected leak — without touching the master key or anybody else's.
/// </summary>
public static class BidderKeys
{
    private static readonly byte[] Context = "eauction-bid-key-v1"u8.ToArray();

    public const int SecretLength = 32;

    /// <summary>Master keys are 32 bytes; a short one would weaken every derived secret.</summary>
    public const int MasterKeyLength = 32;

    public static byte[] Derive(
        ReadOnlySpan<byte> masterKey, Guid auctionId, Guid bidderId, int epoch)
    {
        if (masterKey.Length < MasterKeyLength)
            throw new ArgumentException(
                $"Master key must be at least {MasterKeyLength} bytes.", nameof(masterKey));

        Span<byte> message = stackalloc byte[Context.Length + 16 + 16 + 4];
        var at = 0;

        Context.CopyTo(message[at..]);
        at += Context.Length;

        auctionId.TryWriteBytes(message.Slice(at, 16));
        at += 16;

        bidderId.TryWriteBytes(message.Slice(at, 16));
        at += 16;

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            message.Slice(at, 4), epoch);

        var secret = new byte[SecretLength];
        HMACSHA256.HashData(masterKey, message, secret);
        return secret;
    }

    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(MasterKeyLength);
}
