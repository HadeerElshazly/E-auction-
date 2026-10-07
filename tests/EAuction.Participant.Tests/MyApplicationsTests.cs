using EAuction.Core;
using EAuction.Participant.Domain;
using Xunit;

namespace EAuction.Participant.Tests;

/// <summary>«طلباتي» is filtered on the server: the stage it filters by, and the search.</summary>
public class MyApplicationsTests
{
    [Fact]
    public void The_stage_follows_the_lifecycle_and_never_goes_back()
    {
        var terms = Build.Terms(Guid.NewGuid());
        Assert.Equal(AuctionStage.Upcoming, terms.Stage);

        Assert.True(terms.Advance("AuctionStarted"));
        Assert.Equal(AuctionStage.Live, terms.Stage);

        Assert.True(terms.Advance("AwardConfirmed"));
        Assert.Equal(AuctionStage.Finished, terms.Stage);

        // A replayed «opened» must not undo a later «closed».
        Assert.False(terms.Advance("AuctionStarted"));
        Assert.Equal(AuctionStage.Finished, terms.Stage);

        // A clerk's extension is not a stage.
        Assert.False(terms.Advance("AuctionExtendedByClerk"));
    }

    [Fact]
    public void A_cancelled_auction_is_its_own_stage()
    {
        var terms = Build.Terms(Guid.NewGuid());
        terms.Cancel(Build.Now);
        Assert.Equal(AuctionStage.Cancelled, terms.Stage);
    }

    [Theory]
    [InlineData("مخطط الياسمين", "مخطط الياسمين")]
    [InlineData("أراضي", "اراضي")]
    [InlineData("قطعة", "قطعه")]
    [InlineData("المَرْجان", "المرجان")]
    [InlineData("  Al-Yasmin  ", "al-yasmin")]
    public void The_search_folds_the_spellings_people_type_either_way(string typed, string expected) =>
        Assert.Equal(expected, ArabicText.Normalise(typed));

    [Fact]
    public void The_winner_is_told_the_next_step_and_an_old_snapshot_changes_nothing()
    {
        var auctionId = Guid.NewGuid();
        var awardId = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var award = new WinnerAward(auctionId);

        bool Snapshot(DateTimeOffset at, DateTimeOffset? notified, long paid, string transfer) =>
            award.Apply(awardId, winner, 1_000_00, 25_00, t0, t0.AddDays(30), Guid.NewGuid(), notified,
                paid, 1_000_00 - paid, transfer, null, null, null, at);

        Assert.True(Snapshot(t0, null, 0, "NotStarted"));
        Assert.Equal("AwaitingLetter", award.NextStep);

        Assert.True(Snapshot(t0.AddHours(1), t0.AddHours(1), 400_00, "NotStarted"));
        Assert.Equal("Pay", award.NextStep);

        Assert.True(Snapshot(t0.AddHours(2), t0.AddHours(1), 1_000_00, "InProgress"));
        Assert.Equal("Transfer", award.NextStep);

        // Delivered late: older than what is held, so ignored.
        Assert.False(Snapshot(t0.AddMinutes(30), null, 0, "NotStarted"));
        Assert.Equal(1_000_00, award.PaidMinorUnits);

        Assert.True(Snapshot(t0.AddHours(3), t0.AddHours(1), 1_000_00, "Completed"));
        Assert.Equal("Done", award.NextStep);
    }
}
