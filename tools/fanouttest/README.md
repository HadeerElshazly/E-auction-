# Fan-out load test

How many watchers one query BFF replica can hold, and how long a price change takes
to reach them.

```bash
tools/smoke/run-smoke.sh --with-deps --services-only
dotnet tools/fanouttest/bin/Release/net8.0/EAuction.FanOutTest.dll \
  --watchers 10000 --changes 20 --interval-ms 300
```

It publishes the auction definition and the price changes to Kafka itself, standing
in for auction-admin's outbox and the bid processor. That is deliberate: the bid path
has its own load test in `tools/loadtest`, and driving real bids here would measure
the catcher and the processor again instead of the fan-out.

Latency is measured from the `asOf` the BFF stamps when it serialises a change to the
moment the watcher's socket delivers it — the fan-out's own cost, with no clock
synchronisation needed because both ends are the same machine.

## Measured

One BFF replica, 20 price changes 300 ms apart, Kafka and Postgres on the same host.

| watchers | delivered | p50 | p95 | p99 | max | RSS | per stream |
|---|---|---|---|---|---|---|---|
| 10 | 100% | 0.85 ms | 6.96 ms | 7.90 ms | 8.21 ms | — | — |
| 200 | 100% | 3.06 ms | 7.47 ms | 9.20 ms | 17.12 ms | 137 MiB | ~702 KiB |
| 1,000 | 100% | 10.49 ms | 17.13 ms | 20.07 ms | 43.05 ms | 166 MiB | ~170 KiB |
| 5,000 | 100% | 34.13 ms | 73.42 ms | 155.32 ms | 164.14 ms | 289 MiB | ~59 KiB |
| 10,000 | 100% | 77.58 ms | 182.68 ms | 226.73 ms | 240.71 ms | 565 MiB | ~58 KiB |

**No message was dropped at any size** — 200,000 of 200,000 at ten thousand
watchers. The per-connection queue drops its oldest entry when full, so loss was the
thing most likely to appear here and did not.

The "per stream" column is RSS divided by watchers, so it carries the runtime's own
~120 MiB baseline and overstates the marginal cost badly at small sizes. The marginal
figure is the honest one: between 1,000 and 10,000 watchers RSS grew 399 MiB for
9,000 connections, about **45 KiB per stream**.

### What this means against polling

At the design target of 10,000 concurrent bidders, the one-second poll this replaces
would be **10,000 requests a second** through the final minute of an auction — the
same order as the bid load itself — to carry 20 actual changes. The stream carried
the same information in 20 messages per watcher, and a poll's own staleness (500 ms
on average, ~1 s at worst) is *worse* than this channel's p99 of 227 ms. So the push
channel is both cheaper and fresher, which is the whole argument for it.

### Caveats worth stating

- **The load client shares the machine**, and this box has 4 cores. At 10,000
  watchers the client is holding 10,000 sockets and parsing 200,000 frames on the
  same CPUs as the service under test, so these are a floor rather than a ceiling.
- **One replica.** The chart defaults to two with an HPA, and connections are
  independent, so the fleet scales by replica count — but that was not measured.
- **The first run of this tool reported p50 of 1.1 seconds**, which was entirely the
  client: the .NET thread pool grows by roughly one thread every half second, so
  thousands of socket readers started at once queue behind pool growth and the delay
  gets attributed to the server. The tool now pre-sizes the pool. Any load client
  that does not is measuring itself.
- **Latency is measured to the socket, not to a rendered price.** React's render and
  the browser's paint are on top.
