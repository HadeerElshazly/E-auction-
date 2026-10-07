using System.Security.Cryptography;
using System.Text;

namespace EAuction.Seed;

/// <summary>
/// Builds a plausible year of مخطط السعيد.
///
/// Deterministic throughout, and that is the point rather than a convenience. Every
/// id is derived from a name by hashing, so running the seeder twice produces the
/// same auctions — and because every consumer downstream upserts on the auction id,
/// a second run rewrites the same rows instead of doubling the plan. A demonstration
/// that grows a duplicate set each time it is prepared is worse than no seed at all.
///
/// The figures are shaped from the Al-Saeed worked example rather than invented
/// freely: plots of a few hundred to a few thousand square metres, opening prices
/// struck per square metre, a deposit that is a round fraction of the opening price,
/// and a brokerage percentage that matches what the platform actually charges.
/// </summary>
public static class Generator
{
    private const decimal Brokerage = 2.5m;

    private static readonly string[] Phases =
    [
        "مخطط السعيد — المرحلة الأولى",
        "مخطط السعيد — المرحلة الثانية",
        "مخطط السعيد — المرحلة الثالثة",
    ];

    private static readonly (string Ar, string En)[] Districts =
    [
        ("حي الفيصلية", "Al-Faisaliyah"),
        ("حي النزهة", "Al-Nuzha"),
        ("حي الشاطئ", "Al-Shati"),
        ("حي المرجان", "Al-Murjan"),
        ("حي الصواري", "Al-Sawari"),
        ("حي الزمرد", "Az-Zumurrud"),
        ("حي الياقوت", "Al-Yaqut"),
        ("حي اللؤلؤ", "Al-Lulu"),
    ];

    private static readonly string[] BidderNames =
    [
        "سارة الحربي", "خالد العتيبي", "نورة القحطاني", "عبدالله الشهري",
        "منى الزهراني", "فيصل الدوسري", "ريم الغامدي", "سلطان المالكي",
        "هند الشمري", "ماجد البقمي", "لمياء العنزي", "تركي السبيعي",
    ];

    /// <summary>
    /// A stable GUID for a name.
    ///
    /// SHA-256 rather than <c>Guid.NewGuid</c> so that the plan is reproducible, and
    /// rather than a counter so that adding an auction in the middle of the list does
    /// not renumber everything after it — which would orphan every row a previous run
    /// wrote under the old ids.
    /// </summary>
    public static Guid Id(string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));

    public static SeededAuction[] Build(DateTimeOffset now, int count)
    {
        // Seeded so the shuffle is the same every run, for the same reason the ids
        // are: a demonstration prepared twice has to look the same twice.
        var random = new Random(20260826);
        var auctions = new List<SeededAuction>();

        for (var i = 0; i < count; i++)
        {
            // Spread backwards from this month, a few per month, so the revenue
            // report has a shape rather than a single bar.
            var monthsBack = i switch
            {
                < 3 => 0,                       // this month: upcoming and live
                _ => 1 + (i - 3) / 3,           // then roughly three a month going back
            };

            var district = Districts[i % Districts.Length];
            var phase = Phases[(monthsBack / 3) % Phases.Length];
            var name = $"أراضي {district.Ar} — قطعة {i + 1}";
            var id = Id($"auction:{name}");

            // A hall auction every fourth one. The channel matters to the reports —
            // revenue by channel is one of the groupings — and a plan that was
            // entirely online would make that grouping look broken.
            var channel = i % 4 == 3 ? "Onsite" : "Online";

            var plotCount = 1 + random.Next(0, 3);
            var plots = Enumerable.Range(0, plotCount)
                .Select(p =>
                {
                    var area = 600 + random.Next(0, 24) * 100;
                    return new Plot(
                        Id($"plot:{name}:{p}"),
                        $"{430000 + i * 7 + p}/{2 + (i % 8)}",
                        area,
                        $"{district.Ar} — قطعة رقم {p + 1}");
                })
                .ToArray();

            // Struck per square metre, which is how land is actually priced, so the
            // opening price moves with the package rather than being a round number
            // that happens to sit beside an area.
            var perSqm = 900 + random.Next(0, 7) * 100;
            var opening = (long)(plots.Sum(p => p.AreaSqm) * perSqm) * 100;
            var deposit = RoundTo(opening / 10, 5_000_00);

            var starts = now
                .AddMonths(-monthsBack)
                .AddDays(random.Next(1, 25))
                .AddHours(10 - now.Hour)
                .AddMinutes(-now.Minute);

            // The three newest are the present: one running now and two still to
            // open, so the catalogue and المزادات are not a museum.
            var outcome = i switch
            {
                0 => Outcome.Live,
                1 or 2 => Outcome.Upcoming,
                _ when i % 11 == 5 => Outcome.Disqualified,
                _ when i % 9 == 7 => Outcome.Cascaded,
                _ when i % 7 == 4 => Outcome.Unsold,
                _ when i % 13 == 9 => Outcome.Rejected,
                _ => Outcome.Awarded,
            };

            if (outcome is Outcome.Live)
                starts = now.AddMinutes(-20);
            else if (outcome is Outcome.Upcoming)
                starts = now.AddDays(1 + i).AddHours(2);

            var ends = starts.AddHours(channel == "Onsite" ? 2 : 48);

            var bidders = PickBidders(random, i, outcome);

            // What it actually went for: the opening price plus a few increments,
            // because a winning bid that equals the opening price on every auction
            // makes every report look synthetic.
            var winning = outcome is Outcome.Awarded or Outcome.Disqualified or Outcome.Cascaded
                ? opening + RoundTo((long)(opening * (0.04 + random.NextDouble() * 0.22)), 50_000_00)
                : 0;

            auctions.Add(new SeededAuction
            {
                Id = id,
                NameAr = name,
                NameEn = $"{district.En} land — lot {i + 1}",
                Phase = phase,
                Channel = channel,
                StartsAt = starts,
                EndsAt = ends,
                Plots = plots,
                OpeningPriceMinorUnits = opening,
                DepositMinorUnits = deposit,
                BookletPriceMinorUnits = 1_000_00,
                BrokerageFeePercent = Brokerage,
                Outcome = outcome,
                Participants = bidders,
                WinningAmountMinorUnits = winning,
            });
        }

        return [.. auctions];
    }

    /// <summary>
    /// Who took part, and how far each of them got.
    ///
    /// Not everyone who buys a booklet pays the deposit, which is the whole reason
    /// the participation funnel has more than one column — a plan where every
    /// booklet became a deposit would make that report a straight line and tell a
    /// reader nothing.
    /// </summary>
    private static Participant[] PickBidders(Random random, int index, Outcome outcome)
    {
        if (outcome is Outcome.Upcoming or Outcome.Rejected) return [];

        var interested = 2 + random.Next(0, 5);
        var people = new List<Participant>();

        for (var b = 0; b < interested; b++)
        {
            var name = BidderNames[(index * 3 + b) % BidderNames.Length];

            // Roughly a quarter buy the booklet and stop there.
            var paid = random.Next(0, 4) != 0;

            people.Add(new Participant(
                Id($"bidder:{name}"),
                name,
                PaidDeposit: paid,
                IsWinner: false));
        }

        if (outcome is Outcome.Awarded or Outcome.Disqualified or Outcome.Cascaded)
        {
            // Somebody has to have paid for there to be a winner at all, and a
            // cascade needs a second payer for the award to fall to.
            var needed = outcome is Outcome.Cascaded ? 2 : 1;

            for (var k = 0; k < people.Count && people.Count(p => p.PaidDeposit) < needed; k++)
                if (!people[k].PaidDeposit)
                    people[k] = people[k] with { PaidDeposit = true };

            while (people.Count(p => p.PaidDeposit) < needed)
            {
                var name = BidderNames[(index * 5 + people.Count) % BidderNames.Length];
                people.Add(new Participant(Id($"bidder:{name}"), name, true, false));
            }

            var payers = people.Where(p => p.PaidDeposit).ToList();
            people[people.IndexOf(payers[0])] = payers[0] with { IsWinner = true };

            if (outcome is Outcome.Cascaded)
                people[people.IndexOf(payers[1])] = payers[1] with { IsRunnerUp = true };
        }

        return [.. people];
    }

    private static long RoundTo(long value, long step) => Math.Max(step, value / step * step);
}
