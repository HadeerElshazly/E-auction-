using System.Text.Json;
using EAuction.BidCatcher;
using EAuction.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The catcher's state comes from the compacted control topics and nowhere
/// else. Before this existed the catcher started empty in any real deployment
/// and rejected every bid as an unknown auction — it only worked in tests,
/// which injected the state directly.
/// </summary>
public class ControlPlaneTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly byte[] _master = BidderKeys.NewMasterKey();
    private readonly InMemoryEventStream _events = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CatcherState _state;
    private readonly ControlPlane _control;

    public ControlPlaneTests()
    {
        // Screen() consumes a rate-limit token, and these tests poll it while
        // waiting for a topic to propagate. Rate limiting has its own tests.
        _state = new CatcherState(_master) { MaxBidsPerSecondPerBidder = 1_000_000 };
        _control = new ControlPlane(_state, _events, NullLogger<ControlPlane>.Instance);
    }

    private Task StartAsync() => _control.StartAsync(_cts.Token);

    private Task PublishAuctionAsync(Guid auctionId, DateTimeOffset starts, DateTimeOffset ends) =>
        _events.PublishAsync(Topics.Upcoming, auctionId.ToString(),
            JsonSerializer.Serialize(new
            {
                auctionId,
                startsAt = starts,
                endsAt = ends,
                openingPriceMinorUnits = 1_000_000_00L,
                minIncrementMinorUnits = 50_000_00L,
                quietPeriodSeconds = (int?)120,
                maxExtensions = 3
            }, Json),
            "AuctionApproved", _cts.Token);

    private Task PublishEligibilityAsync(Guid auctionId, Guid bidderId, bool eligible, int epoch = 0) =>
        _events.PublishAsync(Topics.Participants, $"{auctionId}:{bidderId}",
            JsonSerializer.Serialize(new { auctionId, bidderId, eligible, keyEpoch = epoch }, Json),
            "ParticipantEligibilityChanged", _cts.Token);

    private Task PublishPriceAsync(Guid auctionId, long price) =>
        _events.PublishAsync(Topics.CurrentWinner, auctionId.ToString(),
            JsonSerializer.Serialize(new { auctionId, priceMinorUnits = price }, Json),
            "CurrentWinner", _cts.Token);

    private async Task WaitFor(Func<bool> condition, string message)
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

    [Fact]
    public async Task An_approved_auction_becomes_biddable_without_anyone_calling_the_catcher()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await PublishEligibilityAsync(auctionId, bidder, eligible: true);
        await StartAsync();

        await WaitFor(() => _state.AuctionCount == 1 && _state.EligibilityCount == 1,
            "control plane never loaded the auction and its participant");

        // Signed with the key the participant service would have derived.
        var secret = BidderKeys.Derive(_master, auctionId, bidder, 0);
        var frame = TestAuction.Frame(auctionId, bidder, 1_200_000_00, now, secret: secret);

        Assert.Equal(RejectionReason.None, _state.Screen(frame, now));
    }

    [Fact]
    public async Task A_key_derived_by_the_participant_side_verifies_on_the_catcher_side()
    {
        // The two services never exchange the secret — only the epoch travels.
        var auctionId = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await PublishEligibilityAsync(auctionId, bidder, eligible: true, epoch: 7);
        await StartAsync();

        await WaitFor(() => _state.EligibilityCount == 1, "eligibility never loaded");

        var participantSideSecret = BidderKeys.Derive(_master, auctionId, bidder, 7);
        var frame = TestAuction.Frame(auctionId, bidder, 1_200_000_00, now, secret: participantSideSecret);

        Assert.Equal(RejectionReason.None, _state.Screen(frame, now));
    }

    [Fact]
    public async Task A_stale_epoch_no_longer_signs_a_valid_bid()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await PublishEligibilityAsync(auctionId, bidder, eligible: true, epoch: 0);
        await StartAsync();
        await WaitFor(() => _state.EligibilityCount == 1, "eligibility never loaded");

        var oldSecret = BidderKeys.Derive(_master, auctionId, bidder, 0);

        // Rotated after a suspected leak.
        await PublishEligibilityAsync(auctionId, bidder, eligible: true, epoch: 1);
        var newSecret = BidderKeys.Derive(_master, auctionId, bidder, 1);
        await WaitFor(
            () => _state.TryGetSigningSecret(auctionId, bidder, out var held)
                  && held.AsSpan().SequenceEqual(newSecret),
            "rotation never took effect");

        var withOldKey = TestAuction.Frame(auctionId, bidder, 1_200_000_00, now, secret: oldSecret);
        Assert.Equal(RejectionReason.BadSignature, _state.Screen(withOldKey, now));
    }

    [Fact]
    public async Task Revoking_eligibility_stops_the_bidder()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await PublishEligibilityAsync(auctionId, bidder, eligible: true);
        await StartAsync();
        await WaitFor(() => _state.EligibilityCount == 1, "eligibility never loaded");

        await PublishEligibilityAsync(auctionId, bidder, eligible: false);
        await WaitFor(() => _state.EligibilityCount == 0, "revocation never applied");

        var secret = BidderKeys.Derive(_master, auctionId, bidder, 0);
        var frame = TestAuction.Frame(auctionId, bidder, 1_200_000_00, now, secret: secret);

        Assert.Equal(RejectionReason.NotEligible, _state.Screen(frame, now));
    }

    [Fact]
    public async Task The_current_price_from_the_processor_drives_edge_rejection()
    {
        var auctionId = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await PublishEligibilityAsync(auctionId, bidder, eligible: true);
        await PublishPriceAsync(auctionId, 1_500_000_00);
        await StartAsync();
        await WaitFor(() => _state.AuctionCount == 1 && _state.EligibilityCount == 1,
            "control plane never loaded");

        var secret = BidderKeys.Derive(_master, auctionId, bidder, 0);

        await WaitFor(
            () => _state.TryGetCurrentPrice(auctionId, out var price) && price == 1_500_000_00,
            "the price from auctions.current-winner was never applied");

        Assert.Equal(RejectionReason.BelowMinimumIncrement,
            _state.Screen(TestAuction.Frame(auctionId, bidder, 1_520_000_00, now, secret: secret), now));

        // Clearing the increment is still accepted — the check is advisory,
        // not a second authority on the price.
        var good = TestAuction.Frame(auctionId, bidder, 1_600_000_00, now, secret: secret);
        Assert.Equal(RejectionReason.None, _state.Screen(good, now));
    }

    [Fact]
    public async Task State_is_rebuilt_from_the_log_by_a_catcher_that_was_not_running()
    {
        // The durability claim: an auction approved and a deposit paid while
        // this pod was down are still in the log when it starts.
        var auctionId = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await PublishAuctionAsync(auctionId, now.AddMinutes(-1), now.AddMinutes(30));
        await PublishEligibilityAsync(auctionId, bidder, eligible: true);

        // A different pod, started after the fact, over the same topics.
        var cold = new CatcherState(_master) { MaxBidsPerSecondPerBidder = 1_000_000 };
        var coldControl = new ControlPlane(cold, _events, NullLogger<ControlPlane>.Instance);
        await coldControl.StartAsync(_cts.Token);

        await WaitFor(() => cold.AuctionCount == 1 && cold.EligibilityCount == 1,
            "a cold start did not rebuild from the log");

        var secret = BidderKeys.Derive(_master, auctionId, bidder, 0);
        var frame = TestAuction.Frame(auctionId, bidder, 1_200_000_00, now, secret: secret);

        Assert.Equal(RejectionReason.None, cold.Screen(frame, now));
        await coldControl.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _control.StopAsync(CancellationToken.None); } catch { }
        _cts.Dispose();
    }
}
