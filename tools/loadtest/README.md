# Bid hot-path load harness

Replays the shape that actually matters: a burst of concurrent bidders
hammering one auction, which is what the last thirty seconds of a hot lot
looks like.

```bash
# terminal 1 — the catcher
dotnet publish src/EAuction.BidCatcher -c Release -o /tmp/catcher
cd /tmp/catcher
ASPNETCORE_URLS=http://127.0.0.1:5080 \
Catcher__EnableDevSeed=true \
Catcher__MaxBidsPerSecondPerBidder=100000 \
DOTNET_gcServer=1 ./EAuction.BidCatcher

# terminal 2 — the load
dotnet publish tools/loadtest -c Release -o /tmp/lt
/tmp/lt/eauction-loadtest --url http://127.0.0.1:5080 \
  --auction $(uuidgen) --bidders 150 --seconds 20
```

The harness seeds the auction and the bidders' eligibility through the
catcher's dev endpoint, which stands in for the compacted topics that carry
that state in a real environment.

`--rate 0` (the default) means "as fast as the workers can go".

## Measured — 2026-10-04

4 shared vCPU, 15 GB, **load generator and server on the same host**,
`InMemoryBidLog`, 150 concurrent bidders, 20 seconds.

| | |
|---|---|
| Requests | 630,816 — all accepted, 0 rejected, 0 failed |
| Throughput | 31,538 req/s |
| p50 | 4.12 ms |
| p90 | 7.47 ms |
| p99 | **13.83 ms** |
| p99.9 | 31.03 ms |
| max | 107.82 ms |

### What this does and does not prove

**Does:** the non-durability portion of the hot path — HTTP, binary framing,
HMAC verification, auction/eligibility/window screening, rate limiting, server
metadata stamping — costs p99 ≈ 14 ms at 31.5k req/s on four shared cores,
while the generator competes for the same cores. Nothing in that path makes a
network call. Zero failures under sustained load.

**Does not:** this ran against `InMemoryBidLog`, not Kafka. The `acks=all`
round trip (D-11) is the single largest remaining cost and is **not measured
here**. Expect it to add single-digit milliseconds against a healthy
three-broker cluster, which leaves roughly 36 ms of the 50 ms budget — a
comfortable margin, but an unverified one.

**The number to beat is still the end-to-end one.** This must be re-run
against a real cluster with the generator on a separate host before the
p99 ≤ 50 ms @ 10k bids/sec target in `docs/ARCHITECTURE.md` §2 can be called
met. The development container has no Docker daemon, so that run is not
possible here.

### An earlier run worth recording

A first pass with the default 20 bids/sec/bidder limit reported 36,686 req/s
at p99 16 ms — but 671,063 of 733,763 requests were rejected by the rate
limiter, which short-circuits before the log append. That measured the
rejection branch, not the accept path. Raising the limit for the benchmark is
what makes the number above meaningful.
