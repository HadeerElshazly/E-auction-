using System.Net;
using System.Net.Http.Json;
using EAuction.BidCatcher;
using EAuction.Core;
using EAuction.Security;
using EAuction.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// The hall (§29): a clerk enters bids for bidders who are in the room.
///
/// The claim an onsite bid makes is weaker than an online one, and deliberately so.
/// Online, the signature says the bidder made this bid and nobody else could have.
/// Onsite, the bidder has a paddle and no keyboard, so the frame says two things —
/// whose bid it is and which clerk recorded it — and the room itself is the
/// evidence that the paddle went up. These tests are mostly about keeping that pair
/// honest: that a clerk cannot be mistaken for a bidder, that a bidder cannot slip
/// past the room, and that neither can reach an auction that is not theirs.
/// </summary>
public class OnsiteChannelTests
{
    private static readonly byte[] ClerkSecret =
        Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    private sealed record Hall(
        HttpClient Clerk, CatcherState State, Guid AuctionId, Guid Bidder, Guid ClerkId);

    private static Hall Arrange(
        AuthenticatedFactory<Program> factory, BidChannel channel = BidChannel.Onsite)
    {
        var state = factory.Services.GetRequiredService<CatcherState>();
        var now = DateTimeOffset.UtcNow;

        var auction = TestAuction.Build(now.AddMinutes(-10), now.AddMinutes(30)) with
        {
            Channel = channel,
        };

        var bidder = Guid.NewGuid();
        var clerkId = Guid.NewGuid();

        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);
        state.AssignClerkWithSecret(auction.AuctionId, clerkId, ClerkSecret);

        return new Hall(
            factory.CreateClient().As(clerkId, Roles.Operator),
            state, auction.AuctionId, bidder, clerkId);
    }

    private static HttpContent Body(byte[] frame)
    {
        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    private static byte[] ClerkFrame(Hall hall, long amount = 1_200_000_00) =>
        TestAuction.Frame(
            hall.AuctionId, hall.Bidder, amount, DateTimeOffset.UtcNow, secret: ClerkSecret);

    // --- the happy path -----------------------------------------------------

    [Fact]
    public async Task A_clerk_enters_a_bid_for_a_bidder_in_the_room()
    {
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory);

        var response = await hall.Clerk.PostAsync("/bids", Body(ClerkFrame(hall)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The receipt is the bidder's, not the clerk's: it is the bidder's bid.
        var receipt = await response.Content.ReadFromJsonAsync<BidReceipt>();
        Assert.Equal(hall.Bidder, receipt.BidderId);
    }

    [Fact]
    public async Task The_frame_is_stamped_onsite_and_with_the_clerk_who_entered_it()
    {
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory);
        var log = factory.Services.GetRequiredService<IBidLog>();

        var response = await hall.Clerk.PostAsync("/bids", Body(ClerkFrame(hall)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReadOnlyMemory<byte> recorded = default;
        await foreach (var logged in log.ReadAsync(hall.AuctionId, 0, cts.Token))
        {
            recorded = logged.Frame;
            break;
        }

        Assert.False(recorded.IsEmpty);

        // Read after the loop: a span cannot be a local in an async method.
        AssertStamped(recorded.Span, hall.ClerkId, hall.Bidder);
    }

    /// <summary>
    /// Both fields are server-written. A client that could choose either could claim
    /// a hall bid was online, or put another clerk's name on its own.
    /// </summary>
    private static void AssertStamped(ReadOnlySpan<byte> frame, Guid clerk, Guid bidder)
    {
        Assert.Equal(BidChannel.Onsite, BidFrame.Channel(frame));
        Assert.Equal(clerk, BidFrame.EnteredBy(frame));
        Assert.Equal(bidder, BidFrame.BidderId(frame));
    }

    // --- who may do what ----------------------------------------------------

    [Fact]
    public async Task A_bidder_cannot_bid_directly_in_a_hall_auction()
    {
        // Otherwise a bidder registered for a hall auction could bid from their
        // phone while standing in the room, and the auctioneer would be calling a
        // price that nobody in front of them had offered.
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory);

        var frame = TestAuction.Frame(
            hall.AuctionId, hall.Bidder, 1_200_000_00, DateTimeOffset.UtcNow);

        var response = await factory.CreateClient().As(hall.Bidder, Roles.Bidder)
            .PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_clerk_from_another_auction_is_refused()
    {
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory);

        var response = await factory.CreateClient().As(Guid.NewGuid(), Roles.Operator)
            .PostAsync("/bids", Body(ClerkFrame(hall)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_clerk_cannot_enter_a_bid_in_an_online_auction()
    {
        // The mirror of the rule above, and the one that stops a clerk bidding on
        // behalf of someone who never asked them to.
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory, BidChannel.Online);

        var response = await hall.Clerk.PostAsync("/bids", Body(ClerkFrame(hall)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_clerk_cannot_bid_for_someone_who_is_not_eligible()
    {
        // Standing in the hall does not pay a deposit.
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory);

        var frame = TestAuction.Frame(
            hall.AuctionId, Guid.NewGuid(), 1_200_000_00, DateTimeOffset.UtcNow,
            secret: ClerkSecret);

        var response = await hall.Clerk.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(nameof(RejectionReason.NotEligible), body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_frame_signed_with_the_bidder_s_key_is_refused_onsite()
    {
        // The clerk's key signs onsite frames. A frame signed with the bidder's own
        // would mean the bidder built it, which is the thing the channel excludes.
        using var factory = new AuthenticatedFactory<Program>();
        var hall = Arrange(factory);

        var frame = TestAuction.Frame(
            hall.AuctionId, hall.Bidder, 1_200_000_00, DateTimeOffset.UtcNow);

        var response = await hall.Clerk.PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(nameof(RejectionReason.BadSignature), body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_unknown_auction_is_refused_rather_than_treated_as_online()
    {
        // A pod that has not replayed this auction yet must not fall through to the
        // online path, where the only check is that the caller matches the frame.
        using var factory = new AuthenticatedFactory<Program>();
        var bidder = Guid.NewGuid();

        var frame = TestAuction.Frame(
            Guid.NewGuid(), bidder, 1_200_000_00, DateTimeOffset.UtcNow);

        var response = await factory.CreateClient().As(bidder, Roles.Bidder)
            .PostAsync("/bids", Body(frame));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(nameof(RejectionReason.UnknownAuction), body.GetProperty("reason").GetString());
    }

    // --- the window ---------------------------------------------------------

    [Fact]
    public void A_hall_auction_has_no_upper_window_at_the_catcher()
    {
        // The end time is the clerk's to move and this service does not follow the
        // lifecycle topic, so the authoritative cutoff is the processor's. The same
        // arrangement the price floor already has: advisory here, decided there.
        var state = new CatcherState(BidderKeys.NewMasterKey());
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddHours(-5), now.AddHours(-4)) with
        {
            Channel = BidChannel.Onsite,
        };

        var clerk = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);
        state.AssignClerkWithSecret(auction.AuctionId, clerk, ClerkSecret);

        var frame = TestAuction.Frame(
            auction.AuctionId, bidder, 1_200_000_00, now, secret: ClerkSecret);

        Assert.Equal(RejectionReason.None, state.Screen(frame, now, clerk));
    }

    [Fact]
    public void A_hall_auction_still_refuses_a_bid_before_it_starts()
    {
        // No upper bound is not no window. Nothing is being auctioned yet.
        var state = new CatcherState(BidderKeys.NewMasterKey());
        var now = DateTimeOffset.UtcNow;
        var auction = TestAuction.Build(now.AddHours(1), now.AddHours(2)) with
        {
            Channel = BidChannel.Onsite,
        };

        var clerk = Guid.NewGuid();
        var bidder = Guid.NewGuid();
        state.UpsertAuction(auction);
        state.GrantEligibilityWithSecret(auction.AuctionId, bidder, TestAuction.Secret);
        state.AssignClerkWithSecret(auction.AuctionId, clerk, ClerkSecret);

        var frame = TestAuction.Frame(
            auction.AuctionId, bidder, 1_200_000_00, now, secret: ClerkSecret);

        Assert.Equal(RejectionReason.OutsideWindow, state.Screen(frame, now, clerk));
    }
}
