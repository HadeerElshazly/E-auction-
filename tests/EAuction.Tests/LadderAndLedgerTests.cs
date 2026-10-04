using EAuction.Core;
using Xunit;

namespace EAuction.Tests;

public class LadderAndLedgerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);

    private static BidVerdict Bid(
        AuctionEngine engine, AuctionDefinition auction, long offset,
        Guid bidder, long amount, DateTimeOffset at)
    {
        var client = TestAuction.Frame(auction.AuctionId, bidder, amount, at);
        return engine.Apply(offset, TestAuction.AsServerFrame(client, at));
    }

    [Fact]
    public void Cascade_walks_down_the_ladder_and_stops_below_reserve()
    {
        // Reserve is 1,500,000. Ahmad's 1,500,000 sits exactly on it and must
        // qualify: the client's rule is "مش أقل من الاحتياطي", so >=.
        var auction = TestAuction.Build(Start, End);
        var engine = new AuctionEngine(auction);

        var khalid = Guid.NewGuid();
        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        Bid(engine, auction, 0, khalid, 1_100_000_00, Start.AddMinutes(1));
        Bid(engine, auction, 1, ahmad, 1_500_000_00, Start.AddMinutes(2));
        Bid(engine, auction, 2, sara, 1_800_000_00, Start.AddMinutes(3));

        var order = engine.CascadeCandidates(new HashSet<Guid>()).ToList();

        Assert.Equal(2, order.Count);
        Assert.Equal(sara, order[0].BidderId);
        Assert.Equal(ahmad, order[1].BidderId);   // exactly at reserve, qualifies
        Assert.DoesNotContain(order, e => e.BidderId == khalid);  // below reserve
    }

    [Fact]
    public void Cascade_skips_a_disqualified_winner()
    {
        var auction = TestAuction.Build(Start, End);
        var engine = new AuctionEngine(auction);

        var ahmad = Guid.NewGuid();
        var sara = Guid.NewGuid();

        Bid(engine, auction, 0, ahmad, 1_600_000_00, Start.AddMinutes(1));
        Bid(engine, auction, 1, sara, 1_800_000_00, Start.AddMinutes(2));

        var afterSaraFails = engine.CascadeCandidates(new HashSet<Guid> { sara }).ToList();

        Assert.Single(afterSaraFails);
        Assert.Equal(ahmad, afterSaraFails[0].BidderId);
    }

    [Fact]
    public void Cascade_is_empty_when_nothing_reaches_the_reserve()
    {
        var auction = TestAuction.Build(Start, End);
        var engine = new AuctionEngine(auction);

        Bid(engine, auction, 0, Guid.NewGuid(), 1_100_000_00, Start.AddMinutes(1));
        Bid(engine, auction, 1, Guid.NewGuid(), 1_200_000_00, Start.AddMinutes(2));

        Assert.False(engine.ReserveMet);
        Assert.Empty(engine.CascadeCandidates(new HashSet<Guid>()));
    }

    [Fact]
    public void A_bidder_appears_once_in_the_cascade_at_their_best_bid()
    {
        var auction = TestAuction.Build(Start, End);
        var engine = new AuctionEngine(auction);

        var sara = Guid.NewGuid();
        var ahmad = Guid.NewGuid();

        Bid(engine, auction, 0, sara, 1_550_000_00, Start.AddMinutes(1));
        Bid(engine, auction, 1, ahmad, 1_600_000_00, Start.AddMinutes(2));
        Bid(engine, auction, 2, sara, 1_700_000_00, Start.AddMinutes(3));

        var order = engine.CascadeCandidates(new HashSet<Guid>()).ToList();

        Assert.Equal(2, order.Count);
        Assert.Equal(sara, order[0].BidderId);
        Assert.Equal(1_700_000_00, order[0].AmountMinorUnits);
        Assert.Equal(ahmad, order[1].BidderId);
    }

    [Fact]
    public void Ledger_head_changes_with_every_frame_and_recomputes_identically()
    {
        var frames = Enumerable.Range(0, 20)
            .Select(i => (ReadOnlyMemory<byte>)TestAuction.Frame(
                Guid.NewGuid(), Guid.NewGuid(), 1000 + i, DateTimeOffset.UtcNow))
            .ToList();

        var chain = new LedgerChain();
        var heads = new List<string>();
        foreach (var f in frames)
        {
            chain.Append(f.Span);
            heads.Add(Convert.ToHexString(chain.Head));
        }

        Assert.Equal(heads.Count, heads.Distinct().Count());
        Assert.True(LedgerChain.Verify(frames, chain.Head));
    }

    [Fact]
    public void Altering_one_record_breaks_the_chain()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(i => TestAuction.Frame(
                Guid.NewGuid(), Guid.NewGuid(), 1000 + i, DateTimeOffset.UtcNow))
            .ToList();

        var chain = new LedgerChain();
        foreach (var f in frames) chain.Append(f);
        var genuineHead = chain.Head.ToArray();

        // Rewrite a bid amount in the middle of the record set.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            frames[4].AsSpan(BidFrame.AmountOffset, 8), 9_999_999_00);

        Assert.False(LedgerChain.Verify(
            frames.Select(f => (ReadOnlyMemory<byte>)f), genuineHead));
    }

    [Fact]
    public void Removing_a_record_breaks_the_chain()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(i => (ReadOnlyMemory<byte>)TestAuction.Frame(
                Guid.NewGuid(), Guid.NewGuid(), 1000 + i, DateTimeOffset.UtcNow))
            .ToList();

        var chain = new LedgerChain();
        foreach (var f in frames) chain.Append(f.Span);

        var withoutOne = frames.Where((_, i) => i != 3).ToList();
        Assert.False(LedgerChain.Verify(withoutOne, chain.Head));
    }

    [Fact]
    public void Rejected_bids_are_chained_too()
    {
        // The ledger records what was received, not only what won — otherwise
        // a rejected bid could be denied after the fact.
        var auction = TestAuction.Build(Start, End);
        var engine = new AuctionEngine(auction);

        Bid(engine, auction, 0, Guid.NewGuid(), 1_200_000_00, Start.AddMinutes(1));
        var headAfterAccepted = engine.LedgerHead.ToArray();

        var rejected = Bid(engine, auction, 1, Guid.NewGuid(), 900_000_00, Start.AddMinutes(2));

        Assert.False(rejected.Accepted);
        Assert.False(headAfterAccepted.AsSpan().SequenceEqual(engine.LedgerHead));
    }
}
