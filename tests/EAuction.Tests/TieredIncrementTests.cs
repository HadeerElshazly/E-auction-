using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

public class TieredIncrementTests
{
    private static readonly IncrementPolicy.Tiered Policy = new(new[]
    {
        (0L,             10_000_00L),
        (1_000_000_00L,  50_000_00L),
        (5_000_000_00L, 100_000_00L)
    });

    [Theory]
    [InlineData(500_000_00,    10_000_00)]
    [InlineData(1_000_000_00,  50_000_00)]
    [InlineData(4_999_999_00,  50_000_00)]
    [InlineData(5_000_000_00, 100_000_00)]
    [InlineData(9_000_000_00, 100_000_00)]
    public void Raise_follows_the_band_the_price_has_reached(long price, long expected) =>
        Assert.Equal(expected, Policy.MinimumRaise(price));
}
