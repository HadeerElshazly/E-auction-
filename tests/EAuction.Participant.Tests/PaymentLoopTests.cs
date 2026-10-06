using EAuction.BidCatcher;
using EAuction.Core;
using EAuction.Outbox;
using EAuction.Participant.Domain;
using EAuction.Participant.Integration;
using EAuction.Participant.Persistence;
using EAuction.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Participant.Tests;

/// <summary>
/// The whole loop money has to travel before a bidder may bid: the participant
/// service asks, the payment service charges, the settlement comes back, and only
/// then does the catcher start accepting the bidder's frames.
///
/// This is the test the platform did not have and most needed. Until the payment
/// service existed, <c>POST .../deposit</c> took a payment reference the caller
/// invented and the participant service recorded it as proof of payment — so a
/// bidder could reach the bid floor of a state land auction, and win it, without a
/// riyal having moved. Nothing in the suite would have noticed.
/// </summary>
[Collection("postgres")]
public class PaymentLoopTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly InMemoryEventStream _events = new();
    private readonly byte[] _master = BidderKeys.NewMasterKey();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IHostedService> _running = [];

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var service in _running)
        {
            try { await service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            (service as IDisposable)?.Dispose();
        }
        await _events.DisposeAsync();
        _cts.Dispose();
    }

    private OutboxRelay<ParticipantDbContext> Relay() =>
        new(pg.Factory, new EventStreamPublisher(_events), new ParticipantOutboxRouter(),
            NullLogger<OutboxRelay<ParticipantDbContext>>.Instance);

    private async Task<T> StartAsync<T>(T service) where T : IHostedService
    {
        await service.StartAsync(_cts.Token);
        _running.Add(service);
        return service;
    }

    private Task<PaymentsService> StartPaymentsAsync(SimulatedGateway gateway) =>
        StartAsync(new PaymentsService(
            gateway, _events, NullLogger<PaymentsService>.Instance)
        {
            RecoveryTimeout = TimeSpan.FromSeconds(20)
        });

    private Task<SettlementConsumer> StartSettlementsAsync() =>
        StartAsync(new SettlementConsumer(
            pg.Factory, _events, NullLogger<SettlementConsumer>.Instance));

    private static async Task WaitFor(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }

    /// <summary>
    /// Drains the participant outbox onto the stream repeatedly, because the loop
    /// crosses it twice: once for the booklet request and once for the deposit.
    /// In deployment Debezium does this continuously.
    /// </summary>
    private async Task PumpAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            await Relay().DrainOnceAsync(100, CancellationToken.None);
            await Task.Delay(25);
        }
    }

    private async Task<(Bidder Bidder, AuctionTerms Terms)> SeedAsync(Guid auctionId)
    {
        var bidder = Build.VerifiedBidder();
        var terms = Build.Terms(auctionId);

        await using var db = await pg.Factory.CreateDbContextAsync();
        db.Bidders.Add(bidder);
        db.AuctionTerms.Add(terms);
        db.Subscriptions.Add(Subscription.Start(auctionId, bidder.Id));
        await db.SaveChangesAsync();

        return (bidder, terms);
    }

    private async Task<SubscriptionStatus> StatusAsync(Guid auctionId, Guid bidderId)
    {
        await using var db = await pg.Factory.CreateDbContextAsync();
        return (await db.Subscriptions
            .AsNoTracking()
            .FirstAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId)).Status;
    }

    private async Task ActAsync(Guid auctionId, Guid bidderId, Action<Subscription, Bidder, AuctionTerms> act)
    {
        await using var db = await pg.Factory.CreateDbContextAsync();
        var subscription = await db.Subscriptions
            .FirstAsync(s => s.AuctionId == auctionId && s.BidderId == bidderId);
        var bidder = await db.Bidders.FirstAsync(b => b.Id == bidderId);
        var terms = await db.AuctionTerms.FirstAsync(t => t.AuctionId == auctionId);

        act(subscription, bidder, terms);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_bidder_becomes_eligible_only_once_the_gateway_has_taken_the_deposit()
    {
        var auctionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var (bidder, _) = await SeedAsync(auctionId);

        var gateway = new SimulatedGateway();
        await StartPaymentsAsync(gateway);
        await StartSettlementsAsync();

        var pump = PumpAsync();

        // The booklet, asked for and not yet paid for.
        await ActAsync(auctionId, bidder.Id, (s, _, terms) => s.RequestBooklet(terms, now));

        Assert.Equal(SubscriptionStatus.Draft, await StatusAsync(auctionId, bidder.Id));

        await WaitFor(
            async () => await StatusAsync(auctionId, bidder.Id)
                        == SubscriptionStatus.BookletPurchased,
            "the booklet fee was never settled");

        await ActAsync(auctionId, bidder.Id, (s, _, terms) =>
        {
            s.AcceptTerms(now);
            s.ChooseDeposit(DepositMethod.Payment, terms, now);
            s.AuthoriseDeposit(terms, now);
        });

        // Authorising is not paying: eligibility waits on the gateway.
        Assert.Equal(SubscriptionStatus.AwaitingDeposit, await StatusAsync(auctionId, bidder.Id));

        await WaitFor(
            async () => await StatusAsync(auctionId, bidder.Id) == SubscriptionStatus.Eligible,
            "the deposit was never settled");

        await pump;

        // Both charges, and the references on the subscription are the gateway's.
        Assert.Equal(2, gateway.Charges);

        await using var db = await pg.Factory.CreateDbContextAsync();
        var settled = await db.Subscriptions.AsNoTracking()
            .FirstAsync(s => s.AuctionId == auctionId && s.BidderId == bidder.Id);

        Assert.StartsWith("SIM-BOO-", settled.BookletPaymentRef);
        Assert.StartsWith("SIM-DEP-", settled.DepositPaymentRef);

        // And the catcher, which is the only consumer of eligibility that matters,
        // will now take this bidder's money and nobody else's.
        var catcher = new CatcherState(_master) { MaxBidsPerSecondPerBidder = 1_000_000 };
        var control = new ControlPlane(catcher, _events, NullLogger<ControlPlane>.Instance);
        await StartAsync(control);

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await WaitFor(
            () => Task.FromResult(catcher.AuctionCount == 1 && catcher.EligibilityCount == 1),
            "eligibility never reached the catcher");

        var secret = BidderKeys.Derive(_master, auctionId, bidder.Id, 0);
        var frame = Frame(auctionId, bidder.Id, 1_200_000_00, now, secret);
        Assert.Equal(RejectionReason.None, catcher.Screen(frame, now));
    }

    [Fact]
    public async Task A_refused_deposit_leaves_the_bidder_off_the_floor_and_tells_them_why()
    {
        // The amount ends in 13, which is how the simulator is asked for a refusal
        // (see SimulatedGateway.RefuseAmountsEndingIn). A real decline looks the
        // same from here, and the bidder who thinks they have paid is exactly who
        // this path exists for.
        var auctionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var bidder = Build.VerifiedBidder();
        var terms = new AuctionTerms(
            auctionId, now.AddDays(7), now.AddDays(8),
            depositMinorUnits: 100_000_13, bookletPriceMinorUnits: 1_000_00);

        await using (var db = await pg.Factory.CreateDbContextAsync())
        {
            db.Bidders.Add(bidder);
            db.AuctionTerms.Add(terms);
            var subscription = Subscription.Start(auctionId, bidder.Id);
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
        }

        await StartPaymentsAsync(new SimulatedGateway());
        await StartSettlementsAsync();

        var pump = PumpAsync();

        await ActAsync(auctionId, bidder.Id, (s, _, t) => s.RequestBooklet(t, now));
        await WaitFor(
            async () => await StatusAsync(auctionId, bidder.Id)
                        == SubscriptionStatus.BookletPurchased,
            "the booklet fee was never settled");

        await ActAsync(auctionId, bidder.Id, (s, _, t) =>
        {
            s.AcceptTerms(now);
            s.ChooseDeposit(DepositMethod.Payment, t, now);
            s.AuthoriseDeposit(t, now);
        });

        await WaitFor(async () =>
        {
            await using var db = await pg.Factory.CreateDbContextAsync();
            var s = await db.Subscriptions.AsNoTracking()
                .FirstAsync(x => x.AuctionId == auctionId && x.BidderId == bidder.Id);
            return s.PaymentFailureReason is not null;
        }, "the refusal never reached the subscription");

        await pump;

        await using var after = await pg.Factory.CreateDbContextAsync();
        var refused = await after.Subscriptions.AsNoTracking()
            .FirstAsync(s => s.AuctionId == auctionId && s.BidderId == bidder.Id);

        Assert.Equal(SubscriptionStatus.AwaitingDeposit, refused.Status);
        Assert.Equal("InsufficientFunds", refused.PaymentFailureReason);
        Assert.Equal(PaymentPurposes.Deposit, refused.PaymentFailurePurpose);
        Assert.Null(refused.DepositPaidAt);

        // Nothing told the catcher this bidder may bid, which is the point.
        var catcher = new CatcherState(_master) { MaxBidsPerSecondPerBidder = 1_000_000 };
        var control = new ControlPlane(catcher, _events, NullLogger<ControlPlane>.Instance);
        await StartAsync(control);

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await WaitFor(
            () => Task.FromResult(catcher.AuctionCount == 1),
            "the auction never reached the catcher");

        var secret = BidderKeys.Derive(_master, auctionId, bidder.Id, 0);
        Assert.Equal(
            RejectionReason.NotEligible,
            catcher.Screen(Frame(auctionId, bidder.Id, 1_200_000_00, now, secret), now));
    }

    // --- harness ------------------------------------------------------------

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
                brokerageFeePercent = 2.5m,
                quietPeriodSeconds = (int?)120,
                maxExtensions = 3
            }, Json),
            "AuctionApproved", _cts.Token);

    private static byte[] Frame(
        Guid auctionId, Guid bidderId, long amount, DateTimeOffset now, byte[] secret) =>
        BidFrame.BuildClientFrame(
            auctionId, bidderId, amount, now.ToUnixTimeMilliseconds(),
            Guid.NewGuid(), Random.Shared.Next(), secret);
}
