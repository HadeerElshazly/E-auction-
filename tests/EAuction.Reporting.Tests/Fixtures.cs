using System.Text.Json;
using EAuction.Core;
using EAuction.Reporting.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAuction.Reporting.Tests;

/// <summary>
/// A database of its own, for every test.
///
/// Less necessary here than for the notification or audit services — this read
/// model is rebuilt from topics and has no watermark and no chain, so a shared
/// database would not corrupt anything. It is still per-test, because the reports
/// are aggregates: a revenue figure asserted against a shared database is a figure
/// that depends on which other tests have run, and the assertion would be about the
/// suite rather than about the report.
/// </summary>
public sealed class ReportingDatabase : IAsyncDisposable
{
    private static readonly string Template =
        Environment.GetEnvironmentVariable("REPORTING_TEST_DB")
        ?? "Host=127.0.0.1;Username=eauction;Password=eauction";

    public string ConnectionString { get; }
    public IDbContextFactory<ReportingDbContext> Factory { get; }

    private ReportingDatabase(string connectionString, IDbContextFactory<ReportingDbContext> factory)
    {
        ConnectionString = connectionString;
        Factory = factory;
    }

    public static async Task<ReportingDatabase> CreateAsync()
    {
        var name = "eauction_reporting_t" + Guid.NewGuid().ToString("N")[..12];
        var connectionString = $"{Template};Database={name}";

        IDbContextFactory<ReportingDbContext> factory = new SimpleFactory(
            new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(connectionString).Options);

        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        return new ReportingDatabase(connectionString, factory);
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
            // A leftover test database is untidy, not a failure.
        }
    }

    private sealed class SimpleFactory(DbContextOptions<ReportingDbContext> options)
        : IDbContextFactory<ReportingDbContext>
    {
        public ReportingDbContext CreateDbContext() => new(options);
    }
}

/// <summary>
/// Publishes the events the reporting service reads, with the shapes the real
/// producers use.
///
/// Written against the wire contract rather than by importing the producers' types,
/// which is the same choice the service itself makes: a test that shared the
/// producer's record would pass through a rename that broke the running system.
/// </summary>
public static class Publish
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Task Send(
        IEventStream events, string topic, string eventType, string key, object payload) =>
        events.PublishAsync(topic, key, JsonSerializer.Serialize(payload, Json), eventType,
            CancellationToken.None);

    public static Task ApprovedAsync(
        IEventStream events, Guid auctionId, string nameAr = "مخطط السعيد",
        string? phase = "مخطط السعيد — المرحلة الأولى",
        long opening = 1_000_000_00, long deposit = 100_000_00, long booklet = 1_000_00,
        decimal brokerage = 2.5m, string channel = "Online", string visibility = "Masked",
        object[]? plots = null, DateTimeOffset? startsAt = null) =>
        Send(events, Topics.Upcoming, "AuctionApproved", auctionId.ToString(), new
        {
            auctionId,
            nameAr,
            nameEn = "Al-Saeed",
            phase,
            startsAt = startsAt ?? DateTimeOffset.UtcNow.AddDays(-2),
            endsAt = (startsAt ?? DateTimeOffset.UtcNow.AddDays(-2)).AddHours(1),
            openingPriceMinorUnits = opening,
            minIncrementMinorUnits = 50_000_00,
            depositMinorUnits = deposit,
            bookletPriceMinorUnits = booklet,
            brokerageFeePercent = brokerage,
            channel,
            bidderVisibility = visibility,
            plotCount = (plots ?? DefaultPlots()).Length,
            totalAreaSqm = 1000.00m,
            plots = plots ?? DefaultPlots(),
        });

    public static object[] DefaultPlots() =>
    [
        new { id = Guid.NewGuid(), plotNumber = "1200", areaSqm = 600.00m,
              latitude = "21.5", longitude = "39.2", descriptionAr = "قطعة أ" },
        new { id = Guid.NewGuid(), plotNumber = "1201", areaSqm = 400.00m,
              latitude = "21.6", longitude = "39.3", descriptionAr = "قطعة ب" },
    ];

    public static Task StartedAsync(IEventStream events, Guid auctionId, DateTimeOffset? at = null) =>
        Send(events, Topics.Lifecycle, "AuctionStarted", auctionId.ToString(),
            new { auctionId, at = at ?? DateTimeOffset.UtcNow.AddDays(-2) });

    public static Task ClosedAsync(
        IEventStream events, Guid auctionId, int bids = 5, int extensions = 1,
        DateTimeOffset? at = null) =>
        Send(events, Topics.Lifecycle, "AuctionClosed", auctionId.ToString(), new
        {
            auctionId,
            at = at ?? DateTimeOffset.UtcNow.AddDays(-2).AddHours(1),
            effectiveEndsAt = (at ?? DateTimeOffset.UtcNow.AddDays(-2).AddHours(1)).AddMinutes(2),
            extensionsUsed = extensions,
            bidCount = bids,
        });

    public static Task CandidateAsync(
        IEventStream events, Guid auctionId, Guid bidderId, long amount, int step = 1) =>
        Send(events, Topics.Lifecycle, "CandidateOffered", auctionId.ToString(),
            new { auctionId, bidderId, amountMinorUnits = amount, cascadeStep = step });

    public static Task AwardedAsync(
        IEventStream events, Guid auctionId, Guid winner, long amount,
        int step = 1, DateTimeOffset? confirmedAt = null) =>
        Send(events, Topics.Lifecycle, "AwardConfirmed", auctionId.ToString(), new
        {
            auctionId,
            awardId = Guid.NewGuid(),
            winnerBidderId = winner,
            amountMinorUnits = amount,
            confirmedAt = confirmedAt ?? DateTimeOffset.UtcNow.AddDays(-1),
            complianceDeadline = (confirmedAt ?? DateTimeOffset.UtcNow.AddDays(-1)).AddDays(5),
            cascadeStep = step,
        });

    public static Task DisqualifiedAsync(
        IEventStream events, Guid auctionId, Guid bidderId,
        string reason = "لم يسدّد", bool forfeit = true) =>
        Send(events, Topics.Lifecycle, "WinnerDisqualified", auctionId.ToString(),
            new { auctionId, awardId = Guid.NewGuid(), bidderId, reason, depositForfeited = forfeit });

    public static Task SettledAsync(
        IEventStream events, Guid auctionId, Guid winner, long amount, DateTimeOffset? at = null) =>
        Send(events, Topics.Lifecycle, "AuctionSettled", auctionId.ToString(), new
        {
            auctionId,
            winnerBidderId = winner,
            amountMinorUnits = amount,
            at = at ?? DateTimeOffset.UtcNow.AddHours(-1),
        });

    public static Task UnsoldAsync(IEventStream events, Guid auctionId) =>
        Send(events, Topics.Lifecycle, "LadderExhausted", auctionId.ToString(),
            new { auctionId, cascadeStep = 1 });

    public static Task RejectedAsync(IEventStream events, Guid auctionId, string reason) =>
        Send(events, Topics.Lifecycle, "AuctionRejected", auctionId.ToString(),
            new { auctionId, reason });

    public static Task EligibleAsync(
        IEventStream events, Guid auctionId, Guid bidderId,
        bool eligible = true, string? nameAr = null) =>
        Send(events, Topics.Participants, "ParticipantEligibilityChanged",
            $"{auctionId}:{bidderId}",
            new { auctionId, bidderId, eligible, keyEpoch = 1, displayNameAr = nameAr });

    public static Task SettlementAsync(
        IEventStream events, Guid auctionId, Guid bidderId,
        string purpose, string outcome, long amount,
        string? failureReason = null, DateTimeOffset? at = null) =>
        Send(events, Topics.Settlements, "PaymentSettled",
            $"{auctionId}:{bidderId}:{purpose}",
            new
            {
                auctionId, bidderId, purpose, outcome,
                amountMinorUnits = amount,
                reference = outcome == "Charged" ? "SIM-1" : "",
                failureReason,
                at = at ?? DateTimeOffset.UtcNow.AddHours(-2),
            });
}

/// <summary>
/// Serialises these tests. Each has its own database, but each also runs a consumer
/// following four topics.
/// </summary>
[CollectionDefinition("reporting")]
public sealed class ReportingCollection;
