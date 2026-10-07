using System.Text.Json;
using EAuction.Core;
using EAuction.Seed;

// ---------------------------------------------------------------------------
// Plants a plan's worth of history on the control topics.
//
//   dotnet run --project tools/seed -- --kafka 127.0.0.1:9092 --count 24
//
// Why events and not the API: auction-admin refuses a start date in the past
// ("Start must be in the future"), correctly — an auction that opened last March
// is not something anyone should be able to create today. So a seeder that went
// through the API could only ever produce auctions opening tomorrow, and the
// reports would have a single month in them.
//
// Publishing the events instead is not a workaround around that rule, it is how
// the reporting service is built: a pure read model with no watermark, replayed
// from offset 0, whose own documentation says "drop the database, restart, and the
// reports come back". Giving it history to replay is the supported path. The
// payloads below are the consumer's own declared contracts, field for field.
//
// What this does NOT do is write to auction-admin's database, so seeded auctions
// do not appear in المزادات. That is the right split rather than a shortfall:
// المزادات is a work queue for auctions being prepared and run, and an auction
// that settled in March does not belong in one. History lives in التقارير.
// ---------------------------------------------------------------------------

var kafka = Arg("--kafka") ?? Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "127.0.0.1:9092";
var count = int.TryParse(Arg("--count"), out var n) ? n : 24;
var now = DateTimeOffset.UtcNow;

Console.WriteLine($"Seeding {count} auctions onto {kafka}…");

await using IEventStream events = new KafkaEventStream(new KafkaEventStreamOptions
{
    BootstrapServers = kafka,
    ConsumerGroup = "seed",
});

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var plan = Generator.Build(now, count);

// The staff who did all of this. Fixed ids so a second run attributes the same
// actions to the same people rather than inventing a new committee.
var admin = Generator.Id("staff:admin-user");
var committee = Generator.Id("staff:committee-user");

var counts = new Dictionary<string, int>();

async Task Publish(string topic, string key, string eventType, object payload)
{
    await events.PublishAsync(
        topic, key, JsonSerializer.Serialize(payload, json), eventType, CancellationToken.None);

    counts[eventType] = counts.GetValueOrDefault(eventType) + 1;
}

// A staff action for an auction, through the same shape the services raise.
async Task StaffAction(Guid actor, string roles, string action, Guid auctionId, DateTimeOffset at, string? details = null) =>
    await Publish(Topics.StaffActions, auctionId.ToString(), "StaffActionRecorded", new
    {
        ActorSubject = actor,
        ActorRoles = roles,
        Action = action,
        Subject = $"auction/{auctionId}",
        Details = details,
        SourceAddress = (string?)null,
        At = at,
    });

foreach (var a in plan)
{
    var preparedAt = a.StartsAt.AddDays(-9);

    await StaffAction(admin, "auction-admin", "CreateAuction", a.Id, preparedAt);

    foreach (var _ in a.Plots)
        await StaffAction(admin, "auction-admin", "AddPlot", a.Id, preparedAt.AddMinutes(5));

    // A rejected auction never reaches the public topic at all, which is the point
    // of it being in the plan: the reports have to be able to say an auction was
    // prepared and refused without it ever having been visible to a bidder.
    if (a.Outcome is Outcome.Rejected)
    {
        await StaffAction(committee, "award-committee", "RejectAuction", a.Id, preparedAt.AddDays(2),
            "أُعيد المزاد إلى الإعداد لنقص في بيانات الصكوك.");

        await Publish(Topics.Lifecycle, a.Id.ToString(), "AuctionRejected", new
        {
            AuctionId = a.Id,
            Reason = "نقص في بيانات الصكوك",
        });
        continue;
    }

    await StaffAction(committee, "award-committee", "ApproveAuction", a.Id, preparedAt.AddDays(2));

    await Publish(Topics.Upcoming, a.Id.ToString(), "AuctionApproved", new
    {
        AuctionId = a.Id,
        a.NameAr,
        a.NameEn,
        a.Phase,
        a.StartsAt,
        a.EndsAt,
        a.OpeningPriceMinorUnits,
        MinIncrementMinorUnits = 50_000_00L,
        a.DepositMinorUnits,
        a.BookletPriceMinorUnits,
        a.BrokerageFeePercent,
        a.Channel,
        BidderVisibility = "Masked",
        PlotCount = a.Plots.Length,
        a.TotalAreaSqm,
        Plots = a.Plots.Select(p => new
        {
            p.Id,
            p.PlotNumber,
            p.AreaSqm,
            p.StreetWidthMeters,
            p.FrontageMeters,
            Latitude = (string?)null,
            Longitude = (string?)null,
            DescriptionAr = p.DescriptionAr,
            DescriptionEn = (string?)null,
        }),
    });

    // --- who took part, and what they paid ---------------------------------

    foreach (var person in a.Participants)
    {
        var boughtAt = a.StartsAt.AddDays(-5);

        await Publish(Topics.Settlements, $"{a.Id}:{person.BidderId}:Booklet", "PaymentSettled", new
        {
            AuctionId = a.Id,
            BidderId = person.BidderId,
            Purpose = "Booklet",
            Outcome = "Charged",
            AmountMinorUnits = a.BookletPriceMinorUnits,
            FailureReason = (string?)null,
            At = boughtAt,
        });

        if (!person.PaidDeposit) continue;

        await Publish(Topics.Settlements, $"{a.Id}:{person.BidderId}:Deposit", "PaymentSettled", new
        {
            AuctionId = a.Id,
            BidderId = person.BidderId,
            Purpose = "Deposit",
            Outcome = "Charged",
            AmountMinorUnits = a.DepositMinorUnits,
            FailureReason = (string?)null,
            At = boughtAt.AddDays(1),
        });

        // Masked auctions carry no name on the topic (D-22), and every auction in
        // this plan is masked — so the display name is deliberately absent rather
        // than merely unset.
        await Publish(Topics.Participants, $"{a.Id}:{person.BidderId}", "ParticipantEligibilityChanged", new
        {
            AuctionId = a.Id,
            BidderId = person.BidderId,
            Eligible = true,
            DisplayNameAr = (string?)null,
        });
    }

    if (a.Outcome is Outcome.Upcoming) continue;

    // --- it runs ------------------------------------------------------------

    await Publish(Topics.Lifecycle, a.Id.ToString(), "AuctionStarted", new
    {
        AuctionId = a.Id,
        At = a.StartsAt,
    });

    if (a.Outcome is Outcome.Live) continue;

    var bidders = a.Participants.Count(p => p.PaidDeposit);

    await Publish(Topics.Lifecycle, a.Id.ToString(), "AuctionClosed", new
    {
        AuctionId = a.Id,
        At = a.EndsAt,
        EffectiveEndsAt = a.EndsAt,
        ExtensionsUsed = a.Channel == "Onsite" ? 0 : Math.Min(3, bidders),
        BidCount = bidders * (3 + (int)(a.Id.GetHashCode() & 3)),
    });

    if (a.Outcome is Outcome.Unsold)
    {
        await Publish(Topics.Lifecycle, a.Id.ToString(), "AuctionUnsold", new { AuctionId = a.Id });
        continue;
    }

    // --- the committee awards it -------------------------------------------

    var winner = a.Participants.Single(p => p.IsWinner);
    var confirmedAt = a.EndsAt.AddDays(2);

    await Publish(Topics.Lifecycle, a.Id.ToString(), "CandidateOffered", new
    {
        AuctionId = a.Id,
        BidderId = winner.BidderId,
        AmountMinorUnits = a.WinningAmountMinorUnits,
        CascadeStep = 0,
    });

    await StaffAction(committee, "award-committee", "ConfirmAward", a.Id, confirmedAt);

    await Publish(Topics.Lifecycle, a.Id.ToString(), "AwardConfirmed", new
    {
        AuctionId = a.Id,
        WinnerBidderId = winner.BidderId,
        AmountMinorUnits = a.WinningAmountMinorUnits,
        ConfirmedAt = confirmedAt,
        ComplianceDeadline = confirmedAt.AddDays(5),
        CascadeStep = 0,
    });

    if (a.Outcome is Outcome.Disqualified or Outcome.Cascaded)
    {
        // The path nobody can produce on demand: the winner never pays and forfeits
        // their deposit (C-2). What happens next is the part worth showing, because
        // there are two answers and a demonstration that only has one leaves the
        // obvious question hanging — the award either falls to the next bidder up
        // the ladder, or there is nobody left who clears the reserve and the land
        // goes back on the plan.
        var defaultedAt = confirmedAt.AddDays(6);

        await StaffAction(committee, "award-committee", "DisqualifyWinner", a.Id,
            defaultedAt, "لم يسدد الفائز ثمن الأرض خلال المهلة النظامية.");

        await Publish(Topics.Lifecycle, a.Id.ToString(), "WinnerDisqualified", new
        {
            AuctionId = a.Id,
            BidderId = winner.BidderId,
            Reason = "عدم السداد خلال المهلة النظامية",
            DepositForfeited = true,
        });

        await Publish(Topics.Settlements, $"{a.Id}:{winner.BidderId}:Deposit:forfeit", "PaymentSettled", new
        {
            AuctionId = a.Id,
            BidderId = winner.BidderId,
            Purpose = "Deposit",
            Outcome = "Forfeited",
            AmountMinorUnits = a.DepositMinorUnits,
            FailureReason = (string?)null,
            At = defaultedAt,
        });

        if (a.Outcome is Outcome.Disqualified)
        {
            // Nobody below them clears the reserve.
            await Publish(Topics.Lifecycle, a.Id.ToString(), "LadderExhausted", new { AuctionId = a.Id });
            continue;
        }

        // The cascade: the runner-up is offered it at their own bid, which is lower
        // than the defaulter's — so the municipality gets less for the same land,
        // which is exactly the number a committee wants to see in a report.
        var runnerUp = a.Participants.Single(p => p.IsRunnerUp);
        var cascadeAmount = a.WinningAmountMinorUnits - 50_000_00L;
        var cascadeAt = defaultedAt.AddDays(3);

        await Publish(Topics.Lifecycle, a.Id.ToString(), "CandidateOffered", new
        {
            AuctionId = a.Id,
            BidderId = runnerUp.BidderId,
            AmountMinorUnits = cascadeAmount,
            CascadeStep = 1,
        });

        await StaffAction(committee, "award-committee", "ConfirmAward", a.Id, cascadeAt,
            "ترسية على المزايد التالي بعد استبعاد الفائز الأول.");

        await Publish(Topics.Lifecycle, a.Id.ToString(), "AwardConfirmed", new
        {
            AuctionId = a.Id,
            WinnerBidderId = runnerUp.BidderId,
            AmountMinorUnits = cascadeAmount,
            ConfirmedAt = cascadeAt,
            ComplianceDeadline = cascadeAt.AddDays(5),
            CascadeStep = 1,
        });

        winner = runnerUp;
        confirmedAt = cascadeAt;
    }

    // --- it settles ---------------------------------------------------------

    var settledAt = confirmedAt.AddDays(4);

    await Publish(Topics.Settlements, $"{a.Id}:{winner.BidderId}:Brokerage", "PaymentSettled", new
    {
        AuctionId = a.Id,
        BidderId = winner.BidderId,
        Purpose = "Brokerage",
        Outcome = "Charged",
        AmountMinorUnits = (long)(a.WinningAmountMinorUnits * a.BrokerageFeePercent / 100m),
        FailureReason = (string?)null,
        At = settledAt,
    });

    // The winner's deposit goes against the price; everyone else gets theirs back.
    await Publish(Topics.Settlements, $"{a.Id}:{winner.BidderId}:Deposit:applied", "PaymentSettled", new
    {
        AuctionId = a.Id,
        BidderId = winner.BidderId,
        Purpose = "Deposit",
        Outcome = "AppliedToPurchase",
        AmountMinorUnits = a.DepositMinorUnits,
        FailureReason = (string?)null,
        At = settledAt,
    });

    foreach (var loser in a.Participants.Where(p => p.PaidDeposit && !p.IsWinner))
    {
        await Publish(Topics.Settlements, $"{a.Id}:{loser.BidderId}:Deposit:refund", "PaymentSettled", new
        {
            AuctionId = a.Id,
            BidderId = loser.BidderId,
            Purpose = "Deposit",
            Outcome = "Refunded",
            AmountMinorUnits = a.DepositMinorUnits,
            FailureReason = (string?)null,
            At = settledAt,
        });
    }

    await Publish(Topics.Lifecycle, a.Id.ToString(), "AuctionSettled", new
    {
        AuctionId = a.Id,
        WinnerBidderId = winner.BidderId,
        AmountMinorUnits = a.WinningAmountMinorUnits,
        At = settledAt,
    });
}

Console.WriteLine();
Console.WriteLine($"{plan.Length} auctions:");
foreach (var group in plan.GroupBy(a => a.Outcome).OrderBy(g => g.Key.ToString()))
    Console.WriteLine($"  {group.Key,-14} {group.Count()}");

Console.WriteLine();
Console.WriteLine("events published:");
foreach (var (type, total) in counts.OrderByDescending(c => c.Value))
    Console.WriteLine($"  {type,-32} {total}");

Console.WriteLine();
Console.WriteLine("التقارير and سجل المراجعة pick these up as they catch the topics.");

string? Arg(string name)
{
    var all = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(all, name);
    return i >= 0 && i + 1 < all.Length ? all[i + 1] : null;
}
