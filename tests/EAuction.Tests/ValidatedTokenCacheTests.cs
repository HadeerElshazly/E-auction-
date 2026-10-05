using System.Security.Claims;
using EAuction.Security;
using Xunit;

namespace EAuction.Tests;

public class ValidatedTokenCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ClaimsPrincipal Principal(string sub) =>
        new(new ClaimsIdentity(new[] { new Claim("sub", sub) }, "test"));

    [Fact]
    public void A_validated_token_is_returned_without_revalidating()
    {
        var cache = new ValidatedTokenCache();
        cache.Set("token-a", Principal("sara"), Now.AddMinutes(10), Now);

        Assert.True(cache.TryGet("token-a", Now, out var principal));
        Assert.Equal("sara", principal!.FindFirst("sub")!.Value);
    }

    [Fact]
    public void An_unknown_token_misses()
    {
        var cache = new ValidatedTokenCache();
        Assert.False(cache.TryGet("never-seen", Now, out _));
    }

    [Fact]
    public void The_cache_cannot_outlive_the_token_it_cached()
    {
        // The one property that would make caching unsafe: an entry must
        // never keep a token working past its own expiry.
        var cache = new ValidatedTokenCache();
        cache.Set("token-b", Principal("sara"), Now.AddMinutes(5), Now);

        Assert.True(cache.TryGet("token-b", Now.AddMinutes(4), out _));
        Assert.False(cache.TryGet("token-b", Now.AddMinutes(6), out _));
    }

    [Fact]
    public void An_already_expired_token_is_not_cached_at_all()
    {
        var cache = new ValidatedTokenCache();
        cache.Set("token-c", Principal("sara"), Now.AddMinutes(-1), Now);

        Assert.False(cache.TryGet("token-c", Now, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Altering_a_single_byte_misses_the_cache()
    {
        // The key covers the whole token, so a tampered one is validated in
        // full rather than matching a neighbour's entry.
        var cache = new ValidatedTokenCache();
        cache.Set("header.payload.signature", Principal("sara"), Now.AddMinutes(10), Now);

        Assert.False(cache.TryGet("header.payload.signaturE", Now, out _));
        Assert.False(cache.TryGet("header.payloae.signature", Now, out _));
        Assert.True(cache.TryGet("header.payload.signature", Now, out _));
    }

    [Fact]
    public void Two_bidders_tokens_do_not_collide()
    {
        var cache = new ValidatedTokenCache();
        cache.Set("sara-token", Principal("sara"), Now.AddMinutes(10), Now);
        cache.Set("khalid-token", Principal("khalid"), Now.AddMinutes(10), Now);

        Assert.True(cache.TryGet("sara-token", Now, out var sara));
        Assert.True(cache.TryGet("khalid-token", Now, out var khalid));
        Assert.Equal("sara", sara!.FindFirst("sub")!.Value);
        Assert.Equal("khalid", khalid!.FindFirst("sub")!.Value);
    }

    [Fact]
    public void The_cache_stops_growing_at_its_cap()
    {
        // The working set is one entry per active bidder. Overflowing it means
        // something is wrong, and growing without limit would be worse.
        var cache = new ValidatedTokenCache(capacity: 10);

        for (var i = 0; i < 50; i++)
            cache.Set($"token-{i}", Principal($"b{i}"), Now.AddMinutes(10), Now);

        Assert.Equal(10, cache.Count);
    }
}
