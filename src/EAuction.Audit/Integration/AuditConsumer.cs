using EAuction.Audit.Domain;
using EAuction.Audit.Persistence;
using EAuction.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EAuction.Audit.Integration;

/// <summary>
/// Reads <c>staff.actions</c> and writes each record down, hash-chained.
///
/// The service exists because the service that performs an action must not own the
/// only record of it (D-44). Everything here follows from that: it consumes, it
/// never produces, and the only writer of the table is this loop.
/// </summary>
public sealed class AuditConsumer(
    IDbContextFactory<AuditDbContext> dbFactory,
    IEventStream events,
    ILogger<AuditConsumer> logger) : BackgroundService
{
    /// <summary>
    /// The head of the chain, held in memory and rebuilt from the table on start.
    ///
    /// It is this service's only mutable state and the reason a single consumer
    /// writes: appending to a chain is a read-modify-write of the head, and two
    /// threads doing it concurrently would produce two entries claiming the same
    /// predecessor.
    /// </summary>
    private LedgerChain _chain = new();

    /// <summary>
    /// The highest offset already in the table, so a replay can be skipped without
    /// a round trip per record.
    ///
    /// The unique key is still the thing that makes a replay safe — this only
    /// makes it cheap. A restart re-reads the topic from offset 0 (D-12), and
    /// without this every one of those records would be an insert and a caught
    /// violation.
    /// </summary>
    private long _highest = -1;

    /// <summary>True once the chain has been restored and the topic is being followed.</summary>
    public bool Ready { get; private set; }

    /// <summary>How many records this instance has chained past. For tests and logs.</summary>
    public long Recorded { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RestoreAsync(stoppingToken);
        Ready = true;

        await foreach (var record in events.ReadAsync(Topics.StaffActions, stoppingToken))
        {
            try
            {
                await RecordAsync(record, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Loud, and then stop reading.
                //
                // Every other consumer here logs and carries on, because a notice
                // not sent or a payment not matched is a loss confined to that
                // record. Here the next record's hash is built on this one, so
                // carrying on would write a chain with a hole in it that verifies
                // as tampering for ever. A stalled audit consumer is recoverable;
                // a corrupted trail is not.
                // Rethrown, which under the host's default
                // BackgroundServiceExceptionBehavior.StopHost takes the process down
                // with it. That is the intended behaviour: the realistic failures
                // here are transient (the database is unreachable), a restart
                // re-reads the topic from the beginning and skips what is already
                // written, and a crash-looping pod is noticed where a silently
                // stalled consumer is not.
                logger.LogCritical(
                    e,
                    "Audit: failed to record offset {Offset} of {Topic}. The service is "
                    + "stopping rather than writing a record out of order — the trail is "
                    + "hash-chained and a gap cannot be repaired. Records are still on the "
                    + "topic and will be read when this is fixed and the service restarts.",
                    record.Offset, Topics.StaffActions);

                throw;
            }
        }
    }

    /// <summary>
    /// Picks the chain back up from the last entry written.
    ///
    /// Note what this does not do: recompute. The table is append-only and the
    /// stored hash is the head by definition, so a restart is O(1) rather than a
    /// re-read of a trail that in a few years is the record of every auction the
    /// municipality has held. Whether the stored chain actually holds is a
    /// question for <c>GET /audit/verify</c>, asked when an auditor asks it and
    /// not on every pod start.
    /// </summary>
    private async Task RestoreAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var last = await db.Entries
            .AsNoTracking()
            .OrderByDescending(x => x.Offset)
            .FirstOrDefaultAsync(ct);

        if (last is null)
        {
            logger.LogInformation("Audit: no entries yet; starting a new chain.");
            return;
        }

        _chain = new LedgerChain(last.Hash);
        _highest = last.Offset;

        logger.LogInformation(
            "Audit: resuming at offset {Offset}, head {Head}.",
            last.Offset, Convert.ToHexString(last.Hash));
    }

    internal async Task RecordAsync(StreamEvent record, CancellationToken ct)
    {
        // A replay of what is already written. Skipped before the chain is
        // touched, because appending would advance the head past entries that
        // already claim it.
        if (record.Offset <= _highest) return;

        // The offsets jumped: records existed between the last one written and this
        // one, and this service will never see them.
        //
        // Not an exception, and that is a deliberate reversal. Throwing takes the
        // process down (see the handler in ExecuteAsync), and the cause of a jump is
        // usually permanent — the topic's retention passed while the service was
        // down, so the records are gone from the broker too. A crash loop would mean
        // the trail never records anything again, which hands anyone who can arrange
        // a retention lapse a way to switch auditing off.
        //
        // So it carries on, on the same chain. Nothing untrue is claimed by that:
        // the hashes still follow over everything that *was* written, and the hole
        // in the offsets is what shows the loss — GET /audit/verify lists it as a
        // gap and reports the trail as not intact. A permanent, visible, bounded gap
        // beats a working chain that stops growing.
        if (_highest >= 0 && record.Offset != _highest + 1)
            logger.LogCritical(
                "Audit: {Topic} jumped from offset {Last} to {Offset}; {Missing} record(s) "
                + "will never be written and the trail has a permanent gap there. Recording "
                + "continues on the same chain, so GET /audit/verify will report the gap. "
                + "Causes: the topic's retention passed while this service was down, or it "
                + "has more than one partition — it must have exactly one.",
                Topics.StaffActions, _highest, record.Offset,
                record.Offset - _highest - 1);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Built against a copy, so a failed insert does not advance the real head.
        var chain = new LedgerChain(_chain.Head);
        var entry = AuditEntry.From(record, chain, DateTimeOffset.UtcNow);

        db.Entries.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            // Another replica got there first with the identical row. Identical is
            // not a hope: every byte hashed comes from the record and the head, and
            // both replicas read the same topic in the same order, so two instances
            // cannot compute different hashes for the same offset. Adopt it and
            // move on.
            logger.LogDebug(
                "Audit: offset {Offset} was already written by another instance.",
                record.Offset);
        }

        _chain = new LedgerChain(entry.Hash);
        _highest = record.Offset;
        Recorded++;

        // Info, not debug. These are a few records per auction, and a line per
        // staff action in the service log is itself worth having — it is the one
        // copy of the trail that does not live in the table an attacker would go
        // for.
        logger.LogInformation(
            "Audit {Offset}: {Actor} {Action} {Subject}{Malformed}",
            entry.Offset, entry.ActorSubject, entry.Action, entry.Subject,
            entry.Malformed ? " (unparseable payload)" : "");
    }
}
