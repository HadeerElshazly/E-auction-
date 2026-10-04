using EAuction.BidCatcher;
using Xunit;

namespace EAuction.Tests;

public class RateLimitTests
{
    [Fact]
    public void A_bidder_is_limited_to_their_allowance_each_second()
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = new TokenBucket(5);

        for (var i = 0; i < 5; i++)
            Assert.True(bucket.TryTake(now), $"token {i} should have been available");

        Assert.False(bucket.TryTake(now));
    }

    [Fact]
    public void The_allowance_refills_as_time_passes()
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = new TokenBucket(10);

        for (var i = 0; i < 10; i++) bucket.TryTake(now);
        Assert.False(bucket.TryTake(now));

        Assert.True(bucket.TryTake(now.AddSeconds(1)));
    }

    [Fact]
    public void A_clock_that_steps_backwards_does_not_lock_the_bidder_out()
    {
        // Time moves backwards in practice — an NTP correction, a VM clock
        // adjustment, or two requests whose timestamps are taken out of order.
        // Unclamped, the refill subtracts instead of adding and the bidder is
        // shut out of their own auction.
        var now = DateTimeOffset.UtcNow;
        var bucket = new TokenBucket(20);

        Assert.True(bucket.TryTake(now));

        // A full minute backwards: enough to drain any unclamped bucket.
        Assert.True(bucket.TryTake(now.AddMinutes(-1)));
        Assert.True(bucket.TryTake(now.AddMinutes(-1)));

        // And the bidder is still fine once the clock recovers.
        Assert.True(bucket.TryTake(now));
    }

    [Fact]
    public void A_backwards_step_does_not_rewind_the_refill_clock()
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = new TokenBucket(4);

        for (var i = 0; i < 4; i++) bucket.TryTake(now);
        Assert.False(bucket.TryTake(now));

        // An out-of-order timestamp must not look like a long wait.
        bucket.TryTake(now.AddSeconds(-30));
        Assert.False(bucket.TryTake(now));
    }
}
