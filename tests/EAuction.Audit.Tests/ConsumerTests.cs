using EAuction.Audit.Integration;
using EAuction.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Audit.Tests;

/// <summary>
/// The consumer against a real Postgres: the offset as a primary key, the replay
/// that must cost nothing, and the restart that has to pick the chain back up.
/// </summary>
[Collection("audit")]
public class ConsumerTests : IAsyncLifetime
{
    private AuditDatabase _db = null!;
    private InMemoryEventStream _events = null!;

    public async Task InitializeAsync()
    {
        _db = await AuditDatabase.CreateAsync();
        _events = new InMemoryEventStream();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private AuditConsumer Consumer() =>
        new(_db.Factory, _events, NullLogger<AuditConsumer>.Instance);

    /// <summary>
    /// Runs a consumer until it has written <paramref name="expected"/> entries.
    ///
    /// Started and stopped per call, because that is also what a restart looks
    /// like: the tests below use it twice in a row on the same database to prove
    /// the second run adds nothing.
    /// </summary>
    private async Task<AuditConsumer> DrainAsync(int expected, TimeSpan? within = null)
    {
        var consumer = Consumer();
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));

        await consumer.StartAsync(cts.Token);

        try
        {
            while (await CountAsync() < expected)
                await Task.Delay(25, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Fall through: the assertion on the count says more than a timeout.
        }

        await consumer.StopAsync(CancellationToken.None);
        return consumer;
    }

    private async Task<int> CountAsync()
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Entries.CountAsync();
    }

    private async Task<List<Domain.AuditEntry>> EntriesAsync()
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Entries.AsNoTracking().OrderBy(x => x.Offset).ToListAsync();
    }

    [Fact]
    public async Task Each_record_becomes_an_entry_keyed_on_its_offset()
    {
        var admin = Guid.NewGuid();

        await StaffActions.PublishAsync(_events, admin, "CreateAuctionDraft", "auction/a");
        await StaffActions.PublishAsync(_events, admin, "ApproveAuction", "auction/a");

        await DrainAsync(2);

        var entries = await EntriesAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new long[] { 0, 1 }, entries.Select(e => e.Offset));
        Assert.Equal(["CreateAuctionDraft", "ApproveAuction"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(admin, e.ActorSubject));
    }

    [Fact]
    public async Task The_entries_are_chained()
    {
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "ApproveAuction", "auction/a");
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "NotifyWinner", "auction/a");
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "SettleAuction", "auction/a");

        await DrainAsync(3);

        var entries = await EntriesAsync();

        Assert.Equal(new byte[32], entries[0].PreviousHash);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(entries[1].Hash, entries[2].PreviousHash);
    }

    [Fact]
    public async Task A_restart_replays_the_topic_and_writes_nothing_twice()
    {
        // The property the offset-as-key exists for. IEventStream gives every
        // consumer a unique group and reads from the beginning (D-12), so a
        // restarted service sees the whole trail again — and must add none of it.
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "ApproveAuction", "auction/a");
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "NotifyWinner", "auction/a");

        await DrainAsync(2);
        var before = await EntriesAsync();

        var second = await DrainAsync(2);

        var after = await EntriesAsync();
        Assert.Equal(2, after.Count);
        Assert.Equal(0, second.Recorded);

        // Not merely the same number of rows: the same rows, hashes included.
        Assert.Equal(
            before.Select(e => Convert.ToHexString(e.Hash)),
            after.Select(e => Convert.ToHexString(e.Hash)));
    }

    [Fact]
    public async Task A_restart_continues_the_chain_from_the_stored_head()
    {
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "ApproveAuction", "auction/a");
        await DrainAsync(1);

        var first = (await EntriesAsync()).Single();

        // Published while the service was down, which is the ordinary case.
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "DisqualifyWinner", "auction/a");
        await DrainAsync(2);

        var entries = await EntriesAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(first.Hash, entries[1].PreviousHash);
    }

    [Fact]
    public async Task An_unreadable_payload_is_written_down_rather_than_skipped()
    {
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "ApproveAuction", "auction/a");
        await StaffActions.PublishGarbageAsync(_events, "{ this is not");
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "SettleAuction", "auction/a");

        await DrainAsync(3);

        var entries = await EntriesAsync();
        Assert.Equal(3, entries.Count);

        Assert.True(entries[1].Malformed);
        Assert.Null(entries[1].Action);
        Assert.Equal("{ this is not", entries[1].Payload);

        // And the records on either side are still linked through it, which is the
        // whole reason it is not skipped.
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(entries[1].Hash, entries[2].PreviousHash);
    }

    [Fact]
    public async Task Two_consumers_on_one_database_do_not_fight()
    {
        // Two replicas, both reading from the start, both inserting. The offset key
        // makes the loser's insert a unique violation it swallows — and because
        // nothing hashed comes from a clock, the row it loses to is byte-identical
        // to the one it was about to write.
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "ApproveAuction", "auction/a");
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "NotifyWinner", "auction/a");
        await StaffActions.PublishAsync(_events, Guid.NewGuid(), "SettleAuction", "auction/a");

        var one = Consumer();
        var two = Consumer();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await one.StartAsync(cts.Token);
        await two.StartAsync(cts.Token);

        try
        {
            while (await CountAsync() < 3) await Task.Delay(25, cts.Token);
        }
        catch (OperationCanceledException) { }

        await one.StopAsync(CancellationToken.None);
        await two.StopAsync(CancellationToken.None);

        var entries = await EntriesAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(entries[1].Hash, entries[2].PreviousHash);
    }

    [Fact]
    public async Task An_offset_jump_becomes_a_visible_gap_rather_than_a_dead_service()
    {
        // What happens when records existed that this service will never see — the
        // topic's retention passed while it was down, most likely.
        //
        // The first version threw here, which under the host's exception behaviour
        // takes the process down; since the cause is permanent, that meant the
        // trail never recorded anything again. Anyone who could arrange a retention
        // lapse could switch auditing off. So it carries on, on the same chain, and
        // the hole in the offsets is what shows the loss.
        var consumer = Consumer();

        await consumer.RecordAsync(
            StaffActions.AsRecord(0, Guid.NewGuid(), "ApproveAuction", "auction/a"), default);

        // Offsets 1 and 2 never arrive.
        await consumer.RecordAsync(
            StaffActions.AsRecord(3, Guid.NewGuid(), "SettleAuction", "auction/a"), default);

        var entries = await EntriesAsync();
        Assert.Equal(new long[] { 0, 3 }, entries.Select(e => e.Offset));

        // The chain is unbroken over what was written — nothing untrue is claimed —
        // and the gap is what an auditor sees.
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
    }

    [Fact]
    public async Task The_trail_cannot_be_edited_through_the_database()
    {
        await StaffActions.PublishAsync(
            _events, Guid.NewGuid(), "ApproveAuction", "auction/a", "Approved.");
        await DrainAsync(1);

        await using var db = await _db.Factory.CreateDbContextAsync();

        // The service's own credentials, the ordinary way of changing a row. The
        // trigger in the InitialSchema migration refuses it: altering the trail
        // takes a privileged, separately auditable act, not an UPDATE.
        var update = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_entry SET \"Details\" = 'Rejected.';"));

        Assert.Contains("append-only", update.MessageText);

        var delete = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM audit_entry;"));

        Assert.Contains("append-only", delete.MessageText);
    }
}
