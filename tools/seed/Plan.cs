namespace EAuction.Seed;

/// <summary>What a seeded auction is, before any of it reaches a topic.</summary>
public sealed record SeededAuction
{
    public required Guid Id { get; init; }
    public required string NameAr { get; init; }
    public required string NameEn { get; init; }
    public required string Phase { get; init; }
    public required string Channel { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public required Plot[] Plots { get; init; }
    public required long OpeningPriceMinorUnits { get; init; }
    public required long DepositMinorUnits { get; init; }
    public required long BookletPriceMinorUnits { get; init; }
    public required decimal BrokerageFeePercent { get; init; }
    public required Outcome Outcome { get; init; }
    public required Participant[] Participants { get; init; }

    /// <summary>The winning bid, where there was one.</summary>
    public long WinningAmountMinorUnits { get; init; }

    public decimal TotalAreaSqm => Plots.Sum(p => p.AreaSqm);
}

public sealed record Plot(Guid Id, string DeedNumber, decimal AreaSqm, string DescriptionAr);

/// <summary>A bidder's involvement in one auction.</summary>
public sealed record Participant(
    Guid BidderId,
    string NameAr,
    bool PaidDeposit,
    bool IsWinner,
    bool IsRunnerUp = false);

/// <summary>
/// How an auction ended.
///
/// <c>Disqualified</c> is the one worth having in a demonstration and the one
/// nobody can produce on demand: a winner who never pays, forfeits their deposit,
/// and leaves the auction unsold (C-2). It is the question every procurement
/// officer asks and the only answer that cannot be shown by the happy path.
/// </summary>
public enum Outcome
{
    Upcoming,
    Live,
    Awarded,
    Unsold,

    /// <summary>Winner defaults, nobody below them clears the reserve, land unsold.</summary>
    Disqualified,

    /// <summary>Winner defaults and the award cascades to the runner-up, who completes.</summary>
    Cascaded,

    Rejected,
}
