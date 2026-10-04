using EAuction.Core;

namespace EAuction.Tests;

internal static class TestAuction
{
    public static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    /// <summary>Plot #12 from the worked example in the design discussion.</summary>
    public static AuctionDefinition Build(
        DateTimeOffset start, DateTimeOffset end,
        TimeSpan? quiet = null, int maxExtensions = 3) => new()
    {
        AuctionId = Guid.NewGuid(),
        StartsAt = start,
        EndsAt = end,
        OpeningPriceMinorUnits = 1_000_000_00,
        ReservePriceMinorUnits = 1_500_000_00,
        Increment = new IncrementPolicy.Fixed(50_000_00),
        QuietPeriod = quiet,
        MaxExtensions = maxExtensions
    };

    public static byte[] Frame(
        Guid auctionId, Guid bidderId, long amount, DateTimeOffset at,
        Guid? clientBidId = null, byte[]? secret = null) =>
        BidFrame.BuildClientFrame(
            auctionId, bidderId, amount, at.ToUnixTimeMilliseconds(),
            clientBidId ?? Guid.NewGuid(), Random.Shared.NextInt64(),
            secret ?? Secret);

    /// <summary>Stamps server metadata as the catcher would, for engine tests.</summary>
    public static byte[] AsServerFrame(byte[] clientFrame, DateTimeOffset serverTime)
    {
        var f = new byte[BidFrame.ServerLength];
        clientFrame.CopyTo(f, 0);
        BidFrame.AppendServerMetadata(
            f, serverTime.ToUnixTimeMilliseconds(), 0, BidChannel.Online, Guid.Empty);
        return f;
    }
}
