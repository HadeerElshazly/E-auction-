# Demo seeder

Plants a plan's worth of history — about two dozen auctions across six months,
in every outcome the platform can produce — so التقارير and سجل المراجعة show
management information rather than whatever the last walk-through happened to
create.

```bash
# local stack (tools/smoke/run-portals.sh)
dotnet run --project tools/seed -- --count 24

# the Compose stack: Kafka is on 9192 on the host, not 9092
dotnet run --project tools/seed -- --kafka localhost:9192 --count 24
```

Nothing else needs restarting. التقارير and سجل المراجعة follow the topics, so
the figures appear within a few seconds.

## Why it publishes events rather than calling the API

auction-admin refuses a start date in the past — *"Start must be in the future"*
— and it is right to: an auction that opened last March is not something anyone
should be able to create today. A seeder that went through the API could
therefore only produce auctions opening tomorrow, and every report would have a
single month in it.

Publishing to the control topics is not a way around that rule. It is how the
reporting service is built: a read model with no watermark, replayed from offset
0, whose own documentation says *"drop the database, restart, and the reports
come back"*. Giving it history to replay is the supported path, and the payloads
here are the consumer's own declared contracts field for field.

## What it does not do

It does not write to auction-admin's database, so seeded auctions do **not**
appear in المزادات. That is the right split rather than a shortfall: المزادات is
a work queue for auctions being prepared and run, and one that settled in March
does not belong in it. History lives in التقارير.

## Running it twice

Safe. Every id is derived from its name by hashing rather than generated, and
every consumer downstream upserts on that id, so a second run rewrites the same
rows instead of doubling the plan. The same is true of the figures: the generator
is seeded, so the plan is identical every time.

What it cannot do is *remove* a previous run with different settings — the events
are already on the topics. To start clean, wipe the broker and the read models:
`tools/smoke/run-smoke.sh --services-only --with-deps`, then seed again.

## What ends up in the plan

| Outcome | What it demonstrates |
|---|---|
| Settled | The ordinary sale: awarded, brokerage charged, losing deposits refunded |
| Unsold | Nobody cleared the reserve |
| Disqualified → unsold | The winner never paid, forfeited their deposit, and nobody below them cleared the reserve (C-2) |
| Disqualified → cascaded | The same default, but the award falls to the runner-up at their own lower bid — so the municipality gets less for the same land, which is the figure a committee wants to see |
| Rejected | Prepared and refused by the committee; never reached a bidder |
| Live / Scheduled | The present, so the catalogue is not a museum |

Both halves of a default are seeded deliberately. It is the question every
procurement officer asks, it has two answers, and a demonstration carrying only
one of them leaves the obvious follow-up hanging.
