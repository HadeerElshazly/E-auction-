using EAuction.Core;
using EAuction.Participant.Domain;
using EAuction.Participant.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAuction.Participant.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("PARTICIPANT_TEST_DB")
        ?? "Host=127.0.0.1;Database=eauction_participant_test;Username=eauction;Password=eauction";

    public IDbContextFactory<ParticipantDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<ParticipantDbContext>()
            .UseNpgsql(ConnectionString).Options;

        Factory = new SimpleFactory(options);

        await using var db = await Factory.CreateDbContextAsync();
        EAuction.TestSupport.TestDatabases.EnsureDisposable(db.Database.GetDbConnection().Database);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class SimpleFactory(DbContextOptions<ParticipantDbContext> options)
        : IDbContextFactory<ParticipantDbContext>
    {
        public ParticipantDbContext CreateDbContext() => new(options);
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

internal static class Build
{
    public static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public static Bidder VerifiedBidder(string? nationalId = null)
    {
        var bidder = Bidder.FromNafath(
            Guid.NewGuid(),
            nationalId ?? Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(),
            "سارة", "Sara", Now);
        bidder.CompleteProfile("+966500000000", "sara@example.com", Now);
        return bidder;
    }

    public static AuctionTerms Terms(
        Guid auctionId, BidderVisibility visibility = BidderVisibility.Masked) =>
        new(auctionId, Now.AddDays(7), Now.AddDays(8), 100_000_00, 1_000_00, visibility);

    /// <summary>
    /// Carries a subscription all the way to eligible by payment, settlements and
    /// all — because that is now the only way there.
    /// </summary>
    public static Subscription EligibleByPayment(
        Guid auctionId, Bidder bidder, AuctionTerms terms)
    {
        var s = Subscription.Start(auctionId, bidder.Id);
        PayForBooklet(s, terms);
        s.AcceptTerms(Now);
        s.ChooseDeposit(DepositMethod.Payment, terms, Now);
        s.AuthoriseDeposit(terms, Now);
        s.ConfirmDepositPayment("deposit-ref", bidder, terms, Now);
        return s;
    }

    /// <summary>The booklet, asked for and then settled by the payment service.</summary>
    public static void PayForBooklet(Subscription s, AuctionTerms terms)
    {
        s.RequestBooklet(terms, Now);
        s.ConfirmBookletPayment("booklet-ref", Now);
    }
}
