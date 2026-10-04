using EAuction.Core;
using Xunit;

namespace EAuction.Kafka.Tests;

[Collection("kafka")]
public class BidLogTests(KafkaFixture kafka)
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private KafkaBidLog Log() => new(new KafkaBidLogOptions
    {
        BootstrapServers = kafka.Bootstrap!,
        ConsumerGroup = "bidlog-test-" + Guid.NewGuid().ToString("N")[..8]
    });

    private static byte[] Frame(Guid auctionId, Guid bidderId, long amount)
    {
        var client = BidFrame.BuildClientFrame(
            auctionId, bidderId, amount, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Guid.NewGuid(), Random.Shared.NextInt64(), Secret);

        var server = new byte[BidFrame.ServerLength];
        client.CopyTo(server, 0);
        BidFrame.AppendServerMetadata(
            server, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, BidChannel.Online, Guid.Empty);
        return server;
    }

    private async Task<Guid> NewAuctionTopicAsync(int partitions = 1)
    {
        var auctionId = Guid.NewGuid();
        await kafka.CreateTopicAsync(Topics.BidTopicFor(auctionId), partitions);
        return auctionId;
    }

    [RequiresKafkaFact]
    public async Task Append_returns_a_durable_offset_and_offsets_increase()
    {
        var auctionId = await NewAuctionTopicAsync();
        await using var log = Log();

        var offsets = new List<long>();
        for (var i = 0; i < 10; i++)
            offsets.Add(await log.AppendAsync(
                auctionId, Frame(auctionId, Guid.NewGuid(), 1000 + i), CancellationToken.None));

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), offsets);
    }

    [RequiresKafkaFact]
    public async Task The_end_offset_tracks_what_has_been_appended()
    {
        // Resume depends on this to know when a replay has caught up.
        var auctionId = await NewAuctionTopicAsync();
        await using var log = Log();

        Assert.Equal(0, await log.GetEndOffsetAsync(auctionId, CancellationToken.None));

        for (var i = 0; i < 5; i++)
            await log.AppendAsync(
                auctionId, Frame(auctionId, Guid.NewGuid(), 1000 + i), CancellationToken.None);

        Assert.Equal(5, await log.GetEndOffsetAsync(auctionId, CancellationToken.None));
    }

    [RequiresKafkaFact]
    public async Task A_reader_replays_every_record_in_append_order()
    {
        var auctionId = await NewAuctionTopicAsync();
        await using var log = Log();

        var amounts = Enumerable.Range(0, 25).Select(i => 1_000_000L + i * 1000).ToArray();
        foreach (var amount in amounts)
            await log.AppendAsync(
                auctionId, Frame(auctionId, Guid.NewGuid(), amount), CancellationToken.None);

        var read = new List<(long Offset, long Amount)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await foreach (var record in log.ReadAsync(auctionId, 0, cts.Token))
        {
            read.Add((record.Offset, BidFrame.Amount(record.Frame.Span)));
            if (read.Count == amounts.Length) break;
        }

        Assert.Equal(amounts, read.Select(r => r.Amount));
        Assert.Equal(Enumerable.Range(0, 25).Select(i => (long)i), read.Select(r => r.Offset));
    }

    [RequiresKafkaFact]
    public async Task Concurrent_appends_still_produce_one_total_order()
    {
        // The keystone claim (D-03): offset order is the auction's total order,
        // whatever the clients did.
        var auctionId = await NewAuctionTopicAsync();
        await using var log = Log();

        var appends = Enumerable.Range(0, 60).Select(i =>
            log.AppendAsync(auctionId, Frame(auctionId, Guid.NewGuid(), 1_000_000 + i),
                CancellationToken.None).AsTask());

        var offsets = await Task.WhenAll(appends);

        Assert.Equal(60, offsets.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 60).Select(i => (long)i).OrderBy(x => x),
                     offsets.OrderBy(x => x));

        var seen = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var previous = -1L;

        await foreach (var record in log.ReadAsync(auctionId, 0, cts.Token))
        {
            Assert.True(record.Offset > previous, "offsets must strictly increase on read");
            previous = record.Offset;
            if (++seen == 60) break;
        }

        Assert.Equal(60, seen);
    }

    [RequiresKafkaFact]
    public async Task A_reader_can_resume_from_a_later_offset()
    {
        var auctionId = await NewAuctionTopicAsync();
        await using var log = Log();

        for (var i = 0; i < 10; i++)
            await log.AppendAsync(
                auctionId, Frame(auctionId, Guid.NewGuid(), 1000 + i), CancellationToken.None);

        var read = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await foreach (var record in log.ReadAsync(auctionId, 7, cts.Token))
        {
            read.Add(record.Offset);
            if (read.Count == 3) break;
        }

        Assert.Equal(new long[] { 7, 8, 9 }, read);
    }

    [RequiresKafkaFact]
    public async Task A_multi_partition_bid_topic_is_refused_rather_than_silently_half_read()
    {
        // Topic-per-auction means the topic IS the ordering domain, so it must
        // have exactly one partition. With more, the producer's key sends every
        // record to one of them and a reader assigned to another sees nothing —
        // an auction that looks empty rather than one that fails.
        var auctionId = Guid.NewGuid();
        await kafka.CreateTopicAsync(Topics.BidTopicFor(auctionId), partitions: 6);

        await using var log = Log();
        await log.AppendAsync(
            auctionId, Frame(auctionId, Guid.NewGuid(), 1_000_000), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in log.ReadAsync(auctionId, 0, cts.Token)) break;
        });
    }
}
