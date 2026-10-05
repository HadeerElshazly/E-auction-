using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace EAuction.Security;

/// <summary>
/// Remembers tokens that have already been validated, so an RS256 signature
/// is verified once per token rather than once per request.
///
/// Measured on the bid path, RS256 verification cost more than everything
/// else in the request combined: p99 went from 19 ms to 90 ms and throughput
/// nearly halved. A bidder sends many bids under one token, so almost every
/// verification after the first was re-doing identical work.
///
/// What this does not change:
///
/// - **Lifetime.** An entry is kept only until the token's own `exp` and
///   never a second longer, so the cache cannot extend a token's life.
/// - **Integrity.** The key is a hash of the complete token, so altering any
///   byte produces a different key and the token is validated in full.
/// - **Revocation.** Offline validation already means a revoked session works
///   until its token expires; the cache adds nothing to that window. For
///   bidding specifically, revocation does not rely on tokens at all —
///   clearing eligibility on `auctions.participants` stops the bidder
///   immediately, which is both faster and stronger.
/// </summary>
public sealed class ValidatedTokenCache(int capacity = 20_000)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private long _lastPrunedTicks = DateTimeOffset.UtcNow.UtcTicks;

    private readonly record struct Entry(ClaimsPrincipal Principal, DateTimeOffset ExpiresAt);

    public int Count => _entries.Count;

    public bool TryGet(string token, DateTimeOffset now, out ClaimsPrincipal? principal)
    {
        principal = null;
        if (!_entries.TryGetValue(KeyFor(token), out var entry)) return false;

        if (entry.ExpiresAt <= now)
        {
            _entries.TryRemove(KeyFor(token), out _);
            return false;
        }

        principal = entry.Principal;
        return true;
    }

    public void Set(string token, ClaimsPrincipal principal, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt <= now) return;

        Prune(now);

        // A hard cap rather than an eviction policy: the working set is one
        // entry per active bidder, so overflowing it means something is wrong
        // and quietly growing without limit would be worse than missing.
        if (_entries.Count >= capacity && !_entries.ContainsKey(KeyFor(token))) return;

        _entries[KeyFor(token)] = new Entry(principal, expiresAt);
    }

    private void Prune(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastPrunedTicks);
        if (now.UtcTicks - last < TimeSpan.TicksPerMinute) return;
        if (Interlocked.CompareExchange(ref _lastPrunedTicks, now.UtcTicks, last) != last) return;

        foreach (var (key, entry) in _entries)
            if (entry.ExpiresAt <= now)
                _entries.TryRemove(key, out _);
    }

    /// <summary>
    /// Hashed rather than stored verbatim: the key covers the whole token, so
    /// any alteration misses the cache and is validated properly, and the
    /// cache does not hold a pile of usable bearer credentials in plain form.
    /// </summary>
    private static string KeyFor(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
