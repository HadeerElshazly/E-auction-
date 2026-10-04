using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

public class BidFrameTests
{
    [Fact]
    public void Round_trips_every_field()
    {
        var auctionId = Guid.NewGuid();
        var bidderId = Guid.NewGuid();
        var clientBidId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        var frame = BidFrame.BuildClientFrame(
            auctionId, bidderId, 1_450_000_00, at.ToUnixTimeMilliseconds(),
            clientBidId, 42, TestAuction.Secret);

        Assert.Equal(BidFrame.ClientLength, frame.Length);
        Assert.Equal(auctionId, BidFrame.AuctionId(frame));
        Assert.Equal(bidderId, BidFrame.BidderId(frame));
        Assert.Equal(clientBidId, BidFrame.ClientBidId(frame));
        Assert.Equal(1_450_000_00, BidFrame.Amount(frame));
        Assert.Equal(at.ToUnixTimeMilliseconds(), BidFrame.ClientTimestamp(frame));
        Assert.Equal(42, BidFrame.Nonce(frame));
    }

    [Fact]
    public void Accepts_a_signature_made_with_the_right_secret()
    {
        var frame = TestAuction.Frame(Guid.NewGuid(), Guid.NewGuid(), 100, DateTimeOffset.UtcNow);
        Assert.True(BidFrame.VerifySignature(frame, TestAuction.Secret));
    }

    [Fact]
    public void Rejects_a_signature_made_with_a_different_secret()
    {
        var frame = TestAuction.Frame(Guid.NewGuid(), Guid.NewGuid(), 100, DateTimeOffset.UtcNow);
        var otherSecret = new byte[32];
        Assert.False(BidFrame.VerifySignature(frame, otherSecret));
    }

    [Fact]
    public void Rejects_a_frame_whose_amount_was_altered_after_signing()
    {
        var frame = TestAuction.Frame(Guid.NewGuid(), Guid.NewGuid(), 100_00, DateTimeOffset.UtcNow);

        // Someone intercepts and raises the bid.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(BidFrame.AmountOffset, 8), 9_999_999_00);

        Assert.False(BidFrame.VerifySignature(frame, TestAuction.Secret));
    }

    [Fact]
    public void Server_metadata_does_not_disturb_the_client_signature()
    {
        var client = TestAuction.Frame(Guid.NewGuid(), Guid.NewGuid(), 100, DateTimeOffset.UtcNow);
        var server = TestAuction.AsServerFrame(client, DateTimeOffset.UtcNow);

        // The HMAC covers bytes [0,72) only, so appending metadata must not
        // invalidate it — the catcher stamps the frame after verifying.
        Assert.True(BidFrame.VerifySignature(server, TestAuction.Secret));
        Assert.Equal(BidChannel.Online, BidFrame.Channel(server));
    }
}
