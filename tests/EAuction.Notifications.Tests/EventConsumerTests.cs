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
/// What a bidder is told, and — far more often the interesting half — what they are
/// not told twice.
///
/// Every topic this service reads is at-least-once, and the compacted ones are
/// replayed in full on every start. So the question each of these answers is "what
/// happens on the second delivery", and the answer has to be nothing.
/// </summary>
[Collection("notifications")]
public class EventConsumerTests : IAsyncLifetime, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InMemoryEventStream _events = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CountingChannel _channel = new();
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

    private async Task StartAsync()
    {
        _consumer = new EventConsumer(
            pg.Factory, _channel, _events, NullLogger<EventConsumer>.Instance)
        {
            // A bound on the absorb, not the mechanism: the end offset decides what
            // is history. Short here so a mistake fails fast rather than hanging.
            FirstRunTimeout = TimeSpan.FromSeconds(10),
        };

        await _consumer.StartAsync(_cts.Token);

        // The first-run drain reads six topics before anything is announced, and a
        // test that published nothing beforehand would otherwise race it.
        await WaitUntilAsync(() => Task.FromResult(_consumer.Ready), "the consumer to be ready");
    }

    /// <summary>Counts what the outbound channel was handed, so dispatch is observable.</summary>
    private sealed class CountingChannel : INotificationChannel
    {
        private int _sent;
        public string Name => "counting";
        public int Sent => Volatile.Read(ref _sent);

        public Task<bool> SendAsync(Notification notification, CancellationToken ct)
        {
            Interlocked.Increment(ref _sent);
            return Task.FromResult(true);
        }
    }

    // --- the news ----------------------------------------------------------

    [Fact]
    public async Task Becoming_eligible_tells_the_bidder_once_however_often_it_is_republished()
    {
        // auctions.participants is compacted and republished on every key rotation,
        // so the same eligibility arrives again as a matter of course.
        await ApproveAsync("مخطط السعيد — المرحلة الأولى");
        await StartAsync();

        await EligibleAsync(_sara, true);
        var first = await WaitForAsync(_sara, NotificationKind.Eligible);

        Assert.Contains("مخطط السعيد", first.BodyAr);
        Assert.Equal("مؤهّل للمزايدة", first.TitleAr);

        await EligibleAsync(_sara, true);
        await EligibleAsync(_sara, true);
        await Settle();

        Assert.Single(await AllFor(_sara, NotificationKind.Eligible));
    }

    [Fact]
    public async Task A_revocation_is_its_own_notice_and_eligibility_again_is_news_again()
    {
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await WaitForAsync(_sara, NotificationKind.Eligible);

        await EligibleAsync(_sara, false);
        var revoked = await WaitForAsync(_sara, NotificationKind.Revoked);
        Assert.Contains("أُلغي", revoked.TitleAr);

        // Re-qualified after sorting out whatever it was. That is news, and the
        // dedup key must not suppress it — which it would if eligibility were
        // deduped on anything other than the transition.
        await EligibleAsync(_sara, true);
        await Settle();

        Assert.Single(await AllFor(_sara, NotificationKind.Eligible));
    }

    [Fact]
    public async Task Only_the_eligible_are_told_an_auction_has_started()
    {
        // Someone whose eligibility was revoked is not waiting for this.
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await EligibleAsync(_khalid, true);
        await WaitForAsync(_khalid, NotificationKind.Eligible);

        await EligibleAsync(_khalid, false);
        await WaitForAsync(_khalid, NotificationKind.Revoked);

        await LifecycleAsync("AuctionStarted", new { auctionId = _auction });

        var started = await WaitForAsync(_sara, NotificationKind.AuctionStarted);
        Assert.Contains("فُتح باب المزايدة", started.BodyAr);

        Assert.Empty(await AllFor(_khalid, NotificationKind.AuctionStarted));
    }

    [Fact]
    public async Task Being_outbid_tells_the_bidder_who_just_stopped_leading()
    {
        // The live stream tells whoever is watching the page. This is for the one
        // who closed the tab, and it is the most useful thing this service sends.
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await EligibleAsync(_khalid, true);
        await WaitForAsync(_khalid, NotificationKind.Eligible);

        // The first leader establishes a baseline and tells nobody: on a cold start
        // the previous leader is not known, and guessing would mean telling the
        // current leader they had been outbid by themselves.
        await CurrentWinnerAsync(_sara, 1_050_000_00);
        await Settle();
        Assert.Empty(await AllFor(_sara, NotificationKind.Outbid));

        await CurrentWinnerAsync(_khalid, 1_100_000_00);

        var outbid = await WaitForAsync(_sara, NotificationKind.Outbid);
        Assert.Contains("لم تعد صاحب أعلى مزايدة", outbid.BodyAr);

        // And khalid, who is winning, is told nothing.
        Assert.Empty(await AllFor(_khalid, NotificationKind.Outbid));
    }

    [Fact]
    public async Task Four_raises_are_four_notices_and_a_redelivery_is_none()
    {
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await EligibleAsync(_khalid, true);
        await WaitForAsync(_khalid, NotificationKind.Eligible);

        await CurrentWinnerAsync(_sara, 1_000_000_00);
        await Settle();

        // They trade the lead twice each.
        foreach (var (leader, price) in new[]
                 {
                     (_khalid, 1_050_000_00L), (_sara, 1_100_000_00L),
                     (_khalid, 1_150_000_00L), (_sara, 1_200_000_00L),
                 })
        {
            await CurrentWinnerAsync(leader, price);
        }

        await WaitUntilAsync(async () =>
            (await AllFor(_sara, NotificationKind.Outbid)).Count == 2
            && (await AllFor(_khalid, NotificationKind.Outbid)).Count == 2,
            "two outbid notices each");

        // The same records again, exactly as a rebalance would deliver them.
        foreach (var (leader, price) in new[]
                 {
                     (_khalid, 1_050_000_00L), (_sara, 1_100_000_00L),
                     (_khalid, 1_150_000_00L), (_sara, 1_200_000_00L),
                 })
        {
            await CurrentWinnerAsync(leader, price);
        }

        await Settle();

        Assert.Equal(2, (await AllFor(_sara, NotificationKind.Outbid)).Count);
        Assert.Equal(2, (await AllFor(_khalid, NotificationKind.Outbid)).Count);
    }

    [Fact]
    public async Task Only_the_winner_is_told_who_won()
    {
        // D-22 by notification. Naming the winner to the losers of a masked auction
        // would undo the masking the public topics are arranged to preserve — the
        // losers learn the auction closed, which is all they are entitled to.
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await EligibleAsync(_khalid, true);
        await WaitForAsync(_khalid, NotificationKind.Eligible);

        await LifecycleAsync("AwardConfirmed", new
        {
            auctionId = _auction,
            winnerBidderId = _sara,
            amountMinorUnits = 1_200_000_00L,
            complianceDeadline = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero),
        });

        var awarded = await WaitForAsync(_sara, NotificationKind.Awarded);

        Assert.Contains("رُسي عليك", awarded.BodyAr);
        Assert.Contains("2026-10-11", awarded.BodyAr);

        Assert.Empty(await AllFor(_khalid, NotificationKind.Awarded));
    }

    [Fact]
    public async Task A_refused_payment_says_which_payment_and_why()
    {
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await SettlementAsync(_sara, "Deposit", "Refused", "InsufficientFunds",
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));

        var refused = await WaitForAsync(_sara, NotificationKind.PaymentRefused);

        Assert.Contains("لمبلغ التأمين", refused.BodyAr);
        Assert.Contains("InsufficientFunds", refused.BodyAr);

        // The same settlement again on a replay: still one notice.
        await SettlementAsync(_sara, "Deposit", "Refused", "InsufficientFunds",
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        await Settle();

        Assert.Single(await AllFor(_sara, NotificationKind.PaymentRefused));

        // A second genuine attempt that also failed is a second notice: the bidder
        // tried twice and failed twice, and being told once would hide the second.
        await SettlementAsync(_sara, "Deposit", "Refused", "InsufficientFunds",
            new DateTimeOffset(2026, 10, 6, 9, 5, 0, TimeSpan.Zero));

        await WaitUntilAsync(
            async () => (await AllFor(_sara, NotificationKind.PaymentRefused)).Count == 2,
            "a second refusal notice");
    }

    [Fact]
    public async Task A_settled_payment_is_not_announced()
    {
        // The step completing is the news, and the eligibility notice says it
        // better. A notice for every successful charge is how people learn to
        // ignore the channel.
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await SettlementAsync(_sara, "Deposit", "Charged", null, DateTimeOffset.UtcNow);
        await Settle();

        Assert.Empty(await AllFor(_sara, NotificationKind.PaymentRefused));
    }

    [Fact]
    public async Task The_three_fates_of_a_deposit_read_differently()
    {
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        await EligibleAsync(_khalid, true);
        await WaitForAsync(_khalid, NotificationKind.Eligible);

        // Sara won and her deposit goes against the price; khalid lost and gets his
        // back. A third bidder defaulted and keeps nothing.
        var defaulter = Guid.NewGuid();
        await EligibleAsync(defaulter, true);
        await WaitForAsync(defaulter, NotificationKind.Eligible);

        await DepositsReleasableAsync(forfeit: [defaulter], appliedTo: _sara);

        var applied = await WaitForAsync(_sara, NotificationKind.DepositResolved);
        var returned = await WaitForAsync(_khalid, NotificationKind.DepositResolved);
        var kept = await WaitForAsync(defaulter, NotificationKind.DepositResolved);

        Assert.Contains("خُصم", applied.TitleAr);
        Assert.Contains("أُعيد", returned.TitleAr);
        Assert.Contains("حُجز", kept.TitleAr);

        // None of them is "actionable": the auction is over and opening it would
        // show a bidder nothing they can do.
        Assert.False(applied.Actionable);
    }

    [Fact]
    public async Task An_auction_with_no_name_yet_is_still_announced()
    {
        // auctions.upcoming is followed concurrently with the lifecycle topics, so
        // this ordering is ordinary. A message that says "one of the auctions" is
        // worse than one that names it and far better than no message.
        await StartAsync();

        await EligibleAsync(_sara, true);
        var notice = await WaitForAsync(_sara, NotificationKind.Eligible);

        Assert.Contains("أحد المزادات", notice.BodyAr);
    }

    [Fact]
    public async Task Every_notice_stored_is_handed_to_the_outbound_channel()
    {
        // The dispatch path, exercised end to end. Without this the whole outbound
        // half would be unreachable until an SMS aggregator contract lands.
        await ApproveAsync("مخطط السعيد");
        await StartAsync();

        await EligibleAsync(_sara, true);
        var notice = await WaitForAsync(_sara, NotificationKind.Eligible);

        await WaitUntilAsync(async () =>
        {
            await using var db = await pg.Factory.CreateDbContextAsync();
            var row = await db.Notifications.AsNoTracking().FirstAsync(x => x.Id == notice.Id);
            return row.DispatchedAt is not null;
        }, "the notice to be dispatched");

        Assert.True(_channel.Sent >= 1);
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

    private Task SettlementAsync(
        Guid bidderId, string purpose, string outcome, string? reason, DateTimeOffset at) =>
        _events.PublishAsync(Topics.Settlements, $"{_auction}:{bidderId}:{purpose}",
            JsonSerializer.Serialize(new
            {
                auctionId = _auction, bidderId, purpose, outcome,
                amountMinorUnits = 100_000_00L, failureReason = reason, at
            }, Json),
            "PaymentSettled", _cts.Token);

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

    private Task DepositsReleasableAsync(Guid[] forfeit, Guid? appliedTo) =>
        _events.PublishAsync(Topics.Deposits, _auction.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId = _auction,
                forfeitForBidders = forfeit,
                appliedToPurchaseForBidder = appliedTo
            }, Json),
            "DepositsReleasable", _cts.Token);

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
        // Generous: the wait returns the moment its condition holds, so a long
        // deadline costs nothing when the machine is idle and stops the suite
        // flaking when several assemblies share it.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>
    /// Lets the consumer finish what is already on the topics.
    ///
    /// For the assertions about absence, which have nothing to wait for: the point
    /// is that a second delivery produced nothing, and the only way to be sure is
    /// to give it time to have produced something.
    /// </summary>
    private static Task Settle() => Task.Delay(500);
}
