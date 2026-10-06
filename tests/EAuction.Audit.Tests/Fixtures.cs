using System.Text.Json;
using EAuction.Audit.Persistence;
using EAuction.Core;
using EAuction.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAuction.Audit.Tests;

/// <summary>
/// A database of its own, for every test.
///
/// The same reasoning as the notification service's fixture, for a sharper reason:
/// the chain's head is a property of the whole table. A shared database would make
/// each test's first entry chain onto whatever the previous test happened to write,
/// so "an entry's previous hash is the all-zero head" — the one assertion that
/// pins the start of the chain — would pass or fail on test ordering.
/// </summary>
public sealed class AuditDatabase : IAsyncDisposable
{
    private static readonly string Template =
        Environment.GetEnvironmentVariable("AUDIT_TEST_DB")
        ?? "Host=127.0.0.1;Username=eauction;Password=eauction";

    public string ConnectionString { get; }
    public IDbContextFactory<AuditDbContext> Factory { get; }

    private AuditDatabase(string connectionString, IDbContextFactory<AuditDbContext> factory)
    {
        ConnectionString = connectionString;
        Factory = factory;
    }

    public static async Task<AuditDatabase> CreateAsync()
    {
        var name = "eauction_audit_t" + Guid.NewGuid().ToString("N")[..12];
        var connectionString = $"{Template};Database={name}";

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        IDbContextFactory<AuditDbContext> factory = new SimpleFactory(options);

        await using var db = await factory.CreateDbContextAsync();

        // Migrate, not EnsureCreated: the append-only trigger is raw SQL in the
        // migration, so EnsureCreated would build the table without the one
        // protection these tests are partly about.
        await db.Database.MigrateAsync();

        return new AuditDatabase(connectionString, factory);
    }

    /// <summary>
    /// Lifts the append-only trigger so a test can tamper.
    ///
    /// Needed by exactly the tests that prove tampering is caught, and the fact
    /// that they need it is itself the point: altering an entry is not something
    /// the ordinary credentials can do by accident.
    /// </summary>
    public async Task AllowTamperingAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE audit_entry DISABLE TRIGGER audit_entry_append_only;");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var db = await Factory.CreateDbContextAsync();
            await db.Database.EnsureDeletedAsync();
        }
        catch (Exception)
        {
            // A leftover test database is untidy, not a failure.
        }
    }

    private sealed class SimpleFactory(DbContextOptions<AuditDbContext> options)
        : IDbContextFactory<AuditDbContext>
    {
        public AuditDbContext CreateDbContext() => new(options);
    }
}

/// <summary>Writes a staff action onto the topic the way a service's relay does.</summary>
public static class StaffActions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task PublishAsync(
        IEventStream events, Guid actor, string action, string subject,
        string? details = null, string roles = "auction-admin",
        CancellationToken ct = default)
    {
        var entry = StaffActionRecorded.By(actor, roles, "10.0.0.1", action, subject, details);

        return events.PublishAsync(
            Topics.StaffActions, entry.AggregateId,
            JsonSerializer.Serialize(entry, Json), nameof(StaffActionRecorded), ct);
    }

    /// <summary>
    /// A record as the consumer sees it, at an offset of the caller's choosing.
    ///
    /// For the gap case, which cannot be produced by publishing in order: the
    /// stream assigns offsets itself, and the whole point of that test is an offset
    /// the stream would never hand out.
    /// </summary>
    public static StreamEvent AsRecord(
        long offset, Guid actor, string action, string subject,
        string roles = "auction-admin")
    {
        var entry = StaffActionRecorded.By(actor, roles, "10.0.0.1", action, subject);

        return new StreamEvent(
            Topics.StaffActions, entry.AggregateId,
            JsonSerializer.Serialize(entry, Json), nameof(StaffActionRecorded), offset);
    }

    /// <summary>Something on the topic this service cannot read. See the Malformed tests.</summary>
    public static Task PublishGarbageAsync(
        IEventStream events, string payload, CancellationToken ct = default) =>
        events.PublishAsync(
            Topics.StaffActions, "auction/unknown", payload,
            nameof(StaffActionRecorded), ct);
}

/// <summary>
/// Serialises these tests. Each has its own database, but each also runs a
/// consumer, and a dozen at once makes the waits flake for reasons unrelated to
/// the code.
/// </summary>
[CollectionDefinition("audit")]
public sealed class AuditCollection;
