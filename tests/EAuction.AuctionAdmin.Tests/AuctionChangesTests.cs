using EAuction.AuctionAdmin.Domain;
using Xunit;

namespace EAuction.AuctionAdmin.Tests;

/// <summary>الخاصية 14: a correction keeps the value it replaced — except the reserve's figure.</summary>
public class AuctionChangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_changed_field_is_recorded_with_its_old_and_new_value()
    {
        var before = AuctionChanges.Of(Build.AwaitingSettlement(Now, out _));
        var after = before with { Opening = before.Opening + 50_000_00, Deposit = before.Deposit + 1_000_00 };

        var text = AuctionChanges.Describe(before, after)!;

        Assert.Contains("سعر الافتتاح", text);
        Assert.Contains(AuctionChanges.Money(before.Opening), text);
        Assert.Contains(AuctionChanges.Money(after.Opening), text);
        Assert.Contains("التأمين", text);
        Assert.DoesNotContain("الاسم", text);
    }

    [Fact]
    public void The_reserve_is_said_to_change_but_never_by_how_much()
    {
        var before = AuctionChanges.Of(Build.AwaitingSettlement(Now, out _));
        var after = before with { Reserve = 1_750_000_00 };

        var text = AuctionChanges.Describe(before, after)!;

        Assert.Contains("reserve price was changed", text);
        Assert.DoesNotContain("1,750,000", text);
        Assert.DoesNotContain(AuctionChanges.Money(before.Reserve), text);
    }

    [Fact]
    public void Nothing_changed_records_nothing()
    {
        var same = AuctionChanges.Of(Build.AwaitingSettlement(Now, out _));
        Assert.Null(AuctionChanges.Describe(same, same with { }));
    }
}
