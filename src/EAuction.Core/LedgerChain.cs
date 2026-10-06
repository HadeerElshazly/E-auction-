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
    public const int HashLength = 32;

    private byte[] _head = new byte[HashLength];

    /// <summary>A fresh chain, starting from the all-zero head.</summary>
    public LedgerChain() { }

    /// <summary>
    /// Resumes a chain whose earlier records are already written down.
    ///
    /// The bid processor rebuilds its chain by replaying the topic from the start,
    /// so it never needs this. The audit service does: its records are rows in a
    /// table it has already committed, and on restart it continues from the last
    /// one's hash rather than recomputing a trail that may be years long.
    /// </summary>
    public LedgerChain(ReadOnlySpan<byte> head)
    {
        if (head.Length != HashLength)
            throw new ArgumentException(
                $"A chain head is {HashLength} bytes; got {head.Length}.", nameof(head));

        _head = head.ToArray();
    }

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
    /// <param name="from">
    /// The head the first frame builds on. Empty means the start of the chain,
    /// which is what a processor replaying a bid topic from offset 0 wants. The
    /// audit service passes the hash of the entry before the range it is checking,
    /// so a page of the trail can be verified without rereading all of it.
    /// </param>
    public static bool Verify(
        IEnumerable<ReadOnlyMemory<byte>> frames, ReadOnlySpan<byte> expectedHead,
        ReadOnlySpan<byte> from = default)
    {
        var chain = from.IsEmpty ? new LedgerChain() : new LedgerChain(from);
        foreach (var frame in frames) chain.Append(frame.Span);
        return CryptographicOperations.FixedTimeEquals(chain.Head, expectedHead);
    }
}
