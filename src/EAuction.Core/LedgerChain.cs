using System.Security.Cryptography;

namespace EAuction.Core;

/// <summary>
/// Hash-chained append-only bid ledger (D-21).
///
///   record_n.hash = SHA256(record_n.frame || record_{n-1}.hash)
///
/// Any alteration or removal of a record breaks every hash after it, so
/// tampering with the bid record is detectable rather than merely discouraged.
/// This is what slide 5's منع التلاعب بسجلات العروض والمزايدات asks for.
/// </summary>
public sealed class LedgerChain
{
    private byte[] _head = new byte[32];

    public ReadOnlySpan<byte> Head => _head;

    public byte[] Append(ReadOnlySpan<byte> frame)
    {
        Span<byte> buffer = stackalloc byte[frame.Length + 32];
        frame.CopyTo(buffer);
        _head.CopyTo(buffer[frame.Length..]);

        var hash = SHA256.HashData(buffer);
        _head = hash;
        return hash;
    }

    /// <summary>
    /// Recomputes the chain over <paramref name="frames"/> in order and checks
    /// it lands on <paramref name="expectedHead"/>. Used by audit and by the
    /// processor on restart.
    /// </summary>
    public static bool Verify(IEnumerable<ReadOnlyMemory<byte>> frames, ReadOnlySpan<byte> expectedHead)
    {
        var chain = new LedgerChain();
        foreach (var frame in frames) chain.Append(frame.Span);
        return CryptographicOperations.FixedTimeEquals(chain.Head, expectedHead);
    }
}
