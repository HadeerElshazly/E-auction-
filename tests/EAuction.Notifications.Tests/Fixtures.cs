using EAuction.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAuction.Notifications.Tests;

/// <summary>
/// A database of its own, for every test.
///
/// Heavier than a shared one and necessary, because this service keeps a watermark
/// per topic — how far it has already read — and that is global to its database
/// while offsets are per stream. Every test gets a fresh
/// <see cref="EAuction.Core.InMemoryEventStream"/>, so its offsets start at 0 again;
/// against a shared database the previous test's watermark sits above them and the
/// new test's records are classified as history and announced to nobody. The
/// symptom is a timeout waiting for a notice that was deliberately suppressed, which
/// says nothing about the cause.
///
/// <para>
/// It is also the honest shape: what these tests exercise is a service's lifecycle
/// on its own database, from empty to not. Sharing one makes "what happens on a
/// first run" depend on which test xUnit happened to run first.
/// </para>
/// </summary>
public sealed class NotificationsDatabase : IAsyncDisposable
{
    private static readonly string Template =
        Environment.GetEnvironmentVariable("NOTIFICATIONS_TEST_DB")
        ?? "Host=127.0.0.1;Username=eauction;Password=eauction";

    private readonly string _name;

    public IDbContextFactory<NotificationsDbContext> Factory { get; }

    private NotificationsDatabase(string name, IDbContextFactory<NotificationsDbContext> factory)
    {
        _name = name;
        Factory = factory;
    }

    public static async Task<NotificationsDatabase> CreateAsync()
    {
        var name = "eauction_notifications_t" + Guid.NewGuid().ToString("N")[..12];

        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql($"{Template};Database={name}")
            .Options;

        IDbContextFactory<NotificationsDbContext> factory = new SimpleFactory(options);

        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        return new NotificationsDatabase(name, factory);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var db = await Factory.CreateDbContextAsync();
            EAuction.TestSupport.TestDatabases.EnsureDisposable(db.Database.GetDbConnection().Database);
            await db.Database.EnsureDeletedAsync();
        }
        catch (Exception)
        {
            // A test database left behind is untidy, not a failure. Dropping it can
            // lose a race with a consumer that has not quite finished closing, and
            // failing a passing test over that would be worse than the mess.
        }
    }

    private sealed class SimpleFactory(DbContextOptions<NotificationsDbContext> options)
        : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(options);
    }
}

/// <summary>
/// Serialises these tests.
///
/// Each has its own database, so they do not collide on data — but each also runs a
/// consumer following six topics, and a dozen of those at once on one machine makes
/// the waits flake for reasons that have nothing to do with the code.
/// </summary>
[CollectionDefinition("notifications")]
public sealed class NotificationsCollection;
