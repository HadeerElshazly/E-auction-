using EAuction.AuctionAdmin.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>
/// A real PostgreSQL database, not an in-memory substitute.
///
/// The outbox pattern's whole claim is that the event and the state change
/// share a transaction. An in-memory provider has no transactions worth the
/// name, so testing it there would prove nothing about the property that
/// matters.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("ADMIN_TEST_DB")
        ?? "Host=127.0.0.1;Database=eauction_test;Username=eauction;Password=eauction";

    public IDbContextFactory<AdminDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AdminDbContext>()
            .UseNpgsql(ConnectionString).Options;

        Factory = new SimpleDbContextFactory(options);

        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

internal sealed class SimpleDbContextFactory(DbContextOptions<AdminDbContext> options)
    : IDbContextFactory<AdminDbContext>
{
    public AdminDbContext CreateDbContext() => new(options);
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
