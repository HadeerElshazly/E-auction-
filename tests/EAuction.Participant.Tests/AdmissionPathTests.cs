using EAuction.BidCatcher;
using EAuction.Core;
using EAuction.Outbox;
using EAuction.Participant.Domain;
using EAuction.Participant.Integration;
using EAuction.Participant.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Participant.Tests;

/// <summary>
/// Bridges the outbox relay onto an event stream, so a test can watch a
/// published row arrive at a consumer. In deployment Kafka is both.
/// </summary>
internal sealed class EventStreamPublisher(IEventStream events) : ITopicPublisher
{
    public Task EnsureTopicAsync(string topic, CancellationToken ct) => Task.CompletedTask;

    public Task PublishAsync(
        string topic, string key, string payload, string eventType, CancellationToken ct) =>
        events.PublishAsync(topic, key, payload, eventType, ct);
}

/// <summary>
/// The path a bidder actually takes to being allowed to bid, across both
/// services and the topic between them.
///
/// Until this existed, nothing produced auctions.participants and nothing
/// consumed it — the catcher's eligibility check could only ever fail in a
/// real deployment.
/// </summary>
[Collection("postgres")]
public class AdmissionPathTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly InMemoryEventStream _events = new();
    private readonly byte[] _master = BidderKeys.NewMasterKey();
    private readonly CancellationTokenSource _cts = new();
    private ControlPlane? _control;

    private OutboxRelay<ParticipantDbContext> Relay() =>
        new(pg.Factory, new EventStreamPublisher(_events), new ParticipantOutboxRouter(),
            NullLogger<OutboxRelay<ParticipantDbContext>>.Instance);

    private async Task<CatcherState> StartCatcherAsync()
    {
        // Screen() consumes a rate-limit token, and these tests poll it while
        // waiting for a topic to propagate. Rate limiting has its own tests.
        var state = new CatcherState(_master) { MaxBidsPerSecondPerBidder = 1_000_000 };
        _control = new ControlPlane(state, _events, NullLogger<ControlPlane>.Instance);
        await _control.StartAsync(_cts.Token);
        return state;
    }

    private Task PublishAuctionAsync(Guid auctionId, DateTimeOffset starts, DateTimeOffset ends) =>
        _events.PublishAsync(Topics.Upcoming, auctionId.ToString(),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                auctionId,
                startsAt = starts,
                endsAt = ends,
                openingPriceMinorUnits = 1_000_000_00L,
                minIncrementMinorUnits = 50_000_00L,
                depositMinorUnits = 100_000_00L,
                bookletPriceMinorUnits = 1_000_00L,
                quietPeriodSeconds = (int?)120,
                maxExtensions = 3
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            "AuctionApproved", _cts.Token);

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        // Generous on purpose: the wait returns the moment its condition
        // holds, so a long deadline costs nothing when the machine is idle
        // and stops the suite flaking when several assemblies share it.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(message);
    }

    /// <summary>Runs the subscription to eligible and relays the outbox.</summary>
    private async Task<(Guid BidderId, int Epoch)> MakeEligibleAsync(Guid auctionId)
    {
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Bidders.Add(bidder);
            db.AuctionTerms.Add(terms);
            db.Subscriptions.Add(Build.EligibleByPayment(auctionId, bidder, terms));
            await db.SaveChangesAsync();
        }

        await Relay().DrainOnceAsync(100, CancellationToken.None);
        return (bidder.Id, 0);
    }

    [Fact]
    public async Task A_bidder_who_completes_subscription_can_bid_and_one_who_has_not_cannot()
    {
        var auctionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        var (bidderId, epoch) = await MakeEligibleAsync(auctionId);

        var catcher = await StartCatcherAsync();
        await WaitFor(() => catcher.AuctionCount == 1 && catcher.EligibilityCount == 1,
            "eligibility never reached the catcher");

        // The secret the participant service would hand the bidder — derived
        // on that side, never sent, re-derived independently by the catcher.
        var secret = BidderKeys.Derive(_master, auctionId, bidderId, epoch);
        var frame = TestFrame(auctionId, bidderId, 1_200_000_00, now, secret);
        Assert.Equal(RejectionReason.None, catcher.Screen(frame, now));

        // Someone who never subscribed.
        var stranger = Guid.NewGuid();
        var strangerFrame = TestFrame(auctionId, stranger, 1_200_000_00, now,
            BidderKeys.Derive(_master, auctionId, stranger, 0));
        Assert.Equal(RejectionReason.NotEligible, catcher.Screen(strangerFrame, now));
    }

    [Fact]
    public async Task Revoking_a_subscription_stops_the_bidder_at_the_catcher()
    {
        var auctionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        var (bidderId, epoch) = await MakeEligibleAsync(auctionId);

        var catcher = await StartCatcherAsync();
        await WaitFor(() => catcher.EligibilityCount == 1, "eligibility never arrived");

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            var subscription = await db.Subscriptions
                .FirstAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId);
            var bidder = await db.Bidders.FirstAsync(b => b.Id == bidderId);
            var terms = await db.AuctionTerms.FirstAsync(t => t.AuctionId == auctionId);
            subscription.Revoke("شيك مرتجع", bidder, terms, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await Relay().DrainOnceAsync(100, CancellationToken.None);
        await WaitFor(() => catcher.EligibilityCount == 0, "revocation never reached the catcher");

        var secret = BidderKeys.Derive(_master, auctionId, bidderId, epoch);
        var frame = TestFrame(auctionId, bidderId, 1_200_000_00, now, secret);
        Assert.Equal(RejectionReason.NotEligible, catcher.Screen(frame, now));
    }

    [Fact]
    public async Task Rotating_the_key_invalidates_the_secret_the_bidder_already_had()
    {
        var auctionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        var (bidderId, _) = await MakeEligibleAsync(auctionId);

        var catcher = await StartCatcherAsync();
        await WaitFor(() => catcher.EligibilityCount == 1, "eligibility never arrived");

        var oldSecret = BidderKeys.Derive(_master, auctionId, bidderId, 0);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            var subscription = await db.Subscriptions
                .FirstAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId);
            var bidder = await db.Bidders.FirstAsync(b => b.Id == bidderId);
            var terms = await db.AuctionTerms.FirstAsync(t => t.AuctionId == auctionId);
            subscription.RotateKey(bidder, terms);
            await db.SaveChangesAsync();
        }

        await Relay().DrainOnceAsync(100, CancellationToken.None);

        var newSecret = BidderKeys.Derive(_master, auctionId, bidderId, 1);
        await WaitFor(
            () => catcher.TryGetSigningSecret(auctionId, bidderId, out var held)
                  && held.AsSpan().SequenceEqual(newSecret),
            "rotation never reached the catcher");

        Assert.Equal(RejectionReason.None,
            catcher.Screen(TestFrame(auctionId, bidderId, 1_200_000_00, now, newSecret), now));

        var withOld = TestFrame(auctionId, bidderId, 1_200_000_00, now, oldSecret);
        Assert.Equal(RejectionReason.BadSignature, catcher.Screen(withOld, now));
    }

    [Fact]
    public async Task No_signing_secret_appears_anywhere_on_the_participants_topic()
    {
        // The reason the key is derived rather than distributed: a secret on a
        // topic is readable by anything with topic access and stays in the log.
        var auctionId = Guid.NewGuid();
        await PublishAuctionAsync(auctionId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var (bidderId, _) = await MakeEligibleAsync(auctionId);

        var secret = Convert.ToHexString(BidderKeys.Derive(_master, auctionId, bidderId, 0));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var payloads = new List<string>();
        try
        {
            await foreach (var record in _events.ReadAsync(Topics.Participants, cts.Token))
                payloads.Add(record.Payload);
        }
        catch (OperationCanceledException) { }

        Assert.NotEmpty(payloads);
        foreach (var payload in payloads)
        {
            Assert.DoesNotContain(secret, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", payload, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("keyEpoch", payload);
        }
    }

    [Fact]
    public async Task A_rolled_back_subscription_never_reaches_the_topic()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Bidders.Add(bidder);
            db.AuctionTerms.Add(terms);
            db.Subscriptions.Add(Build.EligibleByPayment(auctionId, bidder, terms));
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Outbox
                .Where(m => m.AggregateId.StartsWith(auctionId.ToString()))
                .ToListAsync());
            Assert.Null(await db.Bidders.FindAsync(bidder.Id));
        }
    }

    private static byte[] TestFrame(
        Guid auctionId, Guid bidderId, long amount, DateTimeOffset at, byte[] secret) =>
        BidFrame.BuildClientFrame(
            auctionId, bidderId, amount, at.ToUnixTimeMilliseconds(),
            Guid.NewGuid(), Random.Shared.NextInt64(), secret);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_control is not null)
        {
            try { await _control.StopAsync(CancellationToken.None); } catch { }
        }
        _cts.Dispose();
    }
}
