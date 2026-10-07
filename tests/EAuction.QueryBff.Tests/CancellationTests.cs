using EAuction.QueryBff;
using Xunit;

namespace EAuction.QueryBff.Tests;

/// <summary>
/// A cancelled auction must read as cancelled whichever topic replays first —
/// the lifecycle and the definitions are followed concurrently.
/// </summary>
public class CancellationTests
{
    private static AuctionEntry Definition(Guid id) => new()
    {
        AuctionId = id,
        NameAr = "أ", NameEn = "A", Channel = "Online",
        StartsAt = DateTimeOffset.UtcNow.AddDays(1), EndsAt = DateTimeOffset.UtcNow.AddDays(2),
        OpeningPriceMinorUnits = 1_000_000_00,
        MinIncrementMinorUnits = 50_000_00,
        DepositMinorUnits = 100_000_00,
        BookletPriceMinorUnits = 1_000_00,
        MaxExtensions = 3, TotalAreaSqm = 600m, Plots = []
    };

    [Fact]
    public void A_cancellation_after_the_definition_marks_it_with_its_reason()
    {
        var state = new CatalogueState();
        var id = Guid.NewGuid();
        state.Upsert(Definition(id));

        state.MarkCancelled(id, "تعديل المخطط");

        Assert.True(state.TryGet(id, out var entry));
        Assert.Equal("Cancelled", entry.Status);
        Assert.Equal("تعديل المخطط", entry.CancellationReason);
    }

    [Fact]
    public void A_cancellation_that_replays_before_its_definition_is_not_lost()
    {
        var state = new CatalogueState();
        var id = Guid.NewGuid();

        Assert.Null(state.MarkCancelled(id, "سبب"));
        state.Upsert(Definition(id));

        Assert.True(state.TryGet(id, out var entry));
        Assert.Equal("Cancelled", entry.Status);
    }

    [Fact]
    public void A_definition_replayed_later_does_not_bring_it_back()
    {
        var state = new CatalogueState();
        var id = Guid.NewGuid();
        state.Upsert(Definition(id));
        state.MarkCancelled(id, "سبب");

        state.Upsert(Definition(id));

        Assert.True(state.TryGet(id, out var entry));
        Assert.Equal("Cancelled", entry.Status);
        Assert.Equal("سبب", entry.CancellationReason);
    }
}
