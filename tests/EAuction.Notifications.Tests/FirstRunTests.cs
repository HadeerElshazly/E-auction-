using System.Text.Json;
using EAuction.Core;
using EAuction.Notifications.Delivery;
using EAuction.Notifications.Domain;
using EAuction.Notifications.Integration;
using EAuction.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Notifications.Tests;

/// <summary>
/// What this service does with the history that was already on the topics when it
/// started.
///
/// Its own database, because that is the input: whether a start counts as a first
/// run is decided by whether anything has been read before, and a sibling test's
/// rows would send these down the restart path while they claimed to test the other.
/// </summary>
[Collection("notifications")]
public class FirstRunTests : IAsyncLifetime, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InMemoryEventStream _events = new();
    private readonly CancellationTokenSource _cts = new();
    private EventConsumer? _consumer;
    private NotificationsDatabase pg = null!;

    public async Task InitializeAsync() => pg = await NotificationsDatabase.CreateAsync();

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    private readonly Guid _auction = Guid.NewGuid();
    private readonly Guid _sara = Guid.NewGuid();
    private readonly Guid _khalid = Guid.NewGuid();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_consumer is not null)
        {
            try { await _consumer.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            _consumer.Dispose();
        }
        await _events.DisposeAsync();
        _cts.Dispose();
        await pg.DisposeAsync();
    }

    private sealed class SilentChannel : INotificationChannel
    {
        public string Name => "silent";
        public Task<bool> SendAsync(Notification n, CancellationToken ct) => Task.FromResult(true);
    }

    private async Task StartAsync()
    {
        _consumer = new EventConsumer(
            pg.Factory, new SilentChannel(), _events, NullLogger<EventConsumer>.Instance)
        {
            FirstRunTimeout = TimeSpan.FromSeconds(10),
        };

        await _consumer.StartAsync(_cts.Token);
        await WaitUntilAsync(() => Task.FromResult(_consumer.Ready), "the consumer to be ready");
    }

    [Fact]
    public async Task A_first_run_absorbs_everything_already_on_the_topics_and_announces_none_of_it()
    {
        // The bug this exists for, caught by a smoke run rather than by a test:
        // auctions.participants is compacted and holds an eligibility row for every
        // bidder of every auction there has ever been, so a service deployed onto an
        // existing cluster read all of it and sent seventeen "you are eligible"
        // notices where four were due.
        //
        // Everything below happens before the consumer starts, which is exactly the
        // shape of a new deployment onto a running platform.
        await ApproveAsync("مخطط السعيد — مزاد قديم");
        await EligibleAsync(_sara, true);
        await EligibleAsync(_khalid, true);
        await CurrentWinnerAsync(_sara, 1_000_000_00);
        await CurrentWinnerAsync(_khalid, 1_100_000_00);
        await LifecycleAsync("AuctionStarted", new { auctionId = _auction });
        await LifecycleAsync("AuctionClosed", new { auctionId = _auction });
        await LifecycleAsync("AwardConfirmed", new
        {
            auctionId = _auction,
            winnerBidderId = _khalid,
            amountMinorUnits = 1_100_000_00L,
            complianceDeadline = DateTimeOffset.UtcNow.AddDays(5),
        });

        await StartAsync();
        await Settle();

        await using var db = await pg.Factory.CreateDbContextAsync();
        Assert.Empty(await db.Notifications.AsNoTracking().ToListAsync());

        // Absorbed, though: the roster and the name are what later notices need.
        Assert.Equal(2, await db.Audience.CountAsync());
        Assert.Equal(
            "مخطط السعيد — مزاد قديم",
            (await db.AuctionNames.FirstAsync()).NameAr);

        // And the next real change is announced, which is the whole point of
        // absorbing rather than skipping.
        await EligibleAsync(_sara, false);
        var revoked = await WaitForAsync(_sara, NotificationKind.Revoked);
        Assert.Contains("أُلغي", revoked.TitleAr);
    }

    [Fact]
    public async Task A_restart_catches_up_rather_than_absorbing()
    {
        // The other half of the distinction, and the reason the suppression is not
        // unconditional: after a restart the database already says what was sent,
        // so the unique index suppresses the duplicates and what is left is what
        // happened while the service was down — which has to be delivered.
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await WaitForAsync(_sara, NotificationKind.Eligible);

        // Down.
        await _consumer!.StopAsync(CancellationToken.None);

        // And something happens while it is.
        await LifecycleAsync("AuctionStarted", new { auctionId = _auction });

        // Up again, on the same database.
        await StartAsync();

        var started = await WaitForAsync(_sara, NotificationKind.AuctionStarted);
        Assert.Contains("فُتح باب المزايدة", started.BodyAr);

        // And the eligibility it was already told about is not told again.
        Assert.Single(await AllFor(_sara, NotificationKind.Eligible));
    }


    // --- harness ------------------------------------------------------------

    private Task ApproveAsync(string nameAr) =>
        _events.PublishAsync(Topics.Upcoming, _auction.ToString(),
            JsonSerializer.Serialize(new { auctionId = _auction, nameAr }, Json),
            "AuctionApproved", _cts.Token);

    private Task EligibleAsync(Guid bidderId, bool eligible) =>
        _events.PublishAsync(Topics.Participants, $"{_auction}:{bidderId}",
            JsonSerializer.Serialize(new
            {
                auctionId = _auction, bidderId, eligible, keyEpoch = 0
            }, Json),
            "ParticipantEligibilityChanged", _cts.Token);

    private Task LifecycleAsync(string eventType, object payload) =>
        _events.PublishAsync(Topics.Lifecycle, _auction.ToString(),
            JsonSerializer.Serialize(payload, Json), eventType, _cts.Token);

    private Task CurrentWinnerAsync(Guid leader, long price) =>
        _events.PublishAsync(Topics.CurrentWinner, _auction.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = _auction, priceMinorUnits = price, leaderBidderId = leader
            }, Json),
            "CurrentWinner", _cts.Token);

    private async Task<List<Notification>> AllFor(Guid bidderId, NotificationKind kind)
    {
        await using var db = await pg.Factory.CreateDbContextAsync();
        return await db.Notifications
            .AsNoTracking()
            .Where(x => x.BidderId == bidderId && x.AuctionId == _auction && x.Kind == kind)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
    }

    private async Task<Notification> WaitForAsync(Guid bidderId, NotificationKind kind)
    {
        List<Notification> found = [];
        await WaitUntilAsync(async () =>
        {
            found = await AllFor(bidderId, kind);
            return found.Count > 0;
        }, $"a {kind} notice for {bidderId}");

        return found[0];
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static Task Settle() => Task.Delay(500);
}
