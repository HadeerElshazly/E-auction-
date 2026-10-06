using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The certificate endpoint's frame cache.
///
/// Three properties, and the first one is the reason this file exists. The cache
/// is what lets a repeat read of a certificate skip the Kafka seek — and skip the
/// rate limit with it — so a lookup that cannot say "I do not have this" sends
/// every caller down the hit path with nothing in hand.
/// </summary>
public class FrameCacheTests
{
    [Fact]
    public void A_miss_says_so()
    {
        // This is not a tautology, it is a regression.
        //
        // The first version of this type returned `ReadOnlyMemory<byte>?` and wrote
        // `found ? frame : null`. That signature cannot express a miss:
        // ReadOnlyMemory<byte> has an implicit conversion from byte[], the null
        // literal converts to byte[], so the conditional's natural type is the
        // non-nullable ReadOnlyMemory<byte> and the miss comes back as a present
        // nullable holding an empty frame. It compiled, `is null` was false for
        // every lookup on an empty cache, and the endpoint went on to sign zero
        // bytes — eight certificate tests turned into ArgumentOutOfRangeException.
        var cache = new FrameCache(capacity: 8);

        Assert.False(cache.TryGet(Guid.NewGuid(), 0, out var frame));
        Assert.True(frame.IsEmpty);
    }

    [Fact]
    public void A_hit_returns_the_same_bytes()
    {
        // A certificate is signed over the frame, so a cache that changed a byte
        // would produce a signature that disagrees with the bidder's receipt.
        var cache = new FrameCache(capacity: 8);
        var auction = Guid.NewGuid();
        var stored = Enumerable.Range(0, 133).Select(i => (byte)i).ToArray();

        cache.Put(auction, 7, stored);

        Assert.True(cache.TryGet(auction, 7, out var frame));
        Assert.Equal(stored, frame.ToArray());
    }

    [Fact]
    public void The_offset_and_the_auction_are_both_part_of_the_key()
    {
        // Offsets restart at zero for every auction, so a cache keyed on the offset
        // alone would hand one auction's opening bid to another's.
        var cache = new FrameCache(capacity: 8);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        cache.Put(first, 0, new byte[] { 1 });

        Assert.False(cache.TryGet(second, 0, out _));
        Assert.False(cache.TryGet(first, 1, out _));
    }

    [Fact]
    public void It_does_not_grow_without_bound()
    {
        // The bid catcher is the service with a 50ms budget. An unbounded dictionary
        // of frames on the same process is a slow leak, and the thing it would run
        // out of is the memory the hot path needs.
        var cache = new FrameCache(capacity: 2);
        var auction = Guid.NewGuid();

        for (var offset = 0; offset < 5; offset++)
            cache.Put(auction, offset, new byte[] { (byte)offset });

        // Insertion order, so the newest two survive.
        Assert.True(cache.TryGet(auction, 4, out _));
        Assert.True(cache.TryGet(auction, 3, out _));
        Assert.False(cache.TryGet(auction, 0, out _));
    }

    [Fact]
    public void Storing_the_same_offset_twice_does_not_cost_two_slots()
    {
        // Two requests for the same certificate can both miss and both store. If
        // each one queued an eviction, a cache of capacity N would throw away live
        // entries after N/2 distinct frames.
        var cache = new FrameCache(capacity: 2);
        var auction = Guid.NewGuid();

        cache.Put(auction, 0, new byte[] { 0 });
        cache.Put(auction, 0, new byte[] { 0 });
        cache.Put(auction, 1, new byte[] { 1 });

        Assert.True(cache.TryGet(auction, 0, out _));
        Assert.True(cache.TryGet(auction, 1, out _));
    }
}
