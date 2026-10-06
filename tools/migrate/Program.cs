using EAuction.AuctionAdmin.Persistence;
using EAuction.Notifications.Persistence;
using EAuction.Participant.Persistence;
using Microsoft.EntityFrameworkCore;

// ---------------------------------------------------------------------------
// Applies the EF Core migrations for both stateful services.
//
// Deliberately not done by the services at startup. Several replicas starting at
// once would each try to migrate, and a schema change would run while the old
// version is still serving. This is the step a Helm pre-install/pre-upgrade hook
// or a Kubernetes Job runs once, before the new pods roll.
//
//   dotnet EAuction.Migrate.dll \
//     --admin "Host=…;Database=eauction_admin;Username=…;Password=…" \
//     --participant "Host=…;Database=eauction_participant;Username=…;Password=…" \
//     --notifications "Host=…;Database=eauction_notifications;Username=…;Password=…"
//
// Either connection string may also come from ConnectionStrings__Admin /
// ConnectionStrings__Participant, which is how the chart passes them.
// ---------------------------------------------------------------------------

var args_ = args;
string? Arg(string name)
{
    for (var i = 0; i < args_.Length - 1; i++)
        if (args_[i] == $"--{name}") return args_[i + 1];
    return null;
}

var admin = Arg("admin") ?? Environment.GetEnvironmentVariable("ConnectionStrings__Admin");
var participant = Arg("participant")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Participant");
var notifications = Arg("notifications")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Notifications");

if (admin is null && participant is null && notifications is null)
{
    Console.Error.WriteLine(
        "Nothing to do. Pass --admin, --participant and/or --notifications, or set "
        + "ConnectionStrings__Admin / ConnectionStrings__Participant / "
        + "ConnectionStrings__Notifications.");
    return 2;
}

var failed = false;

if (admin is not null)
    failed |= !await Apply("auction-admin", new DbContextOptionsBuilder<AdminDbContext>()
        .UseNpgsql(admin).Options, o => new AdminDbContext(o));

if (participant is not null)
    failed |= !await Apply("participant", new DbContextOptionsBuilder<ParticipantDbContext>()
        .UseNpgsql(participant).Options, o => new ParticipantDbContext(o));

if (notifications is not null)
    failed |= !await Apply("notifications", new DbContextOptionsBuilder<NotificationsDbContext>()
        .UseNpgsql(notifications).Options, o => new NotificationsDbContext(o));

return failed ? 1 : 0;

static async Task<bool> Apply<TContext>(
    string name, DbContextOptions<TContext> options, Func<DbContextOptions<TContext>, TContext> make)
    where TContext : DbContext
{
    await using var db = make(options);
    try
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pending.Length == 0)
        {
            Console.WriteLine($"{name}: already current.");
            return true;
        }

        Console.WriteLine($"{name}: applying {pending.Length} migration(s): {string.Join(", ", pending)}");
        await db.Database.MigrateAsync();
        Console.WriteLine($"{name}: done.");
        return true;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"{name}: FAILED — {e.GetType().Name}: {e.Message}");
        return false;
    }
}
