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

## Measured with Kafka and authentication — 2026-10-05

Same box and shape as the run below, with RS256 bearer tokens on every
request. `--auth` starts a local RS256 issuer and mints one token per bidder,
because the catcher checks the token's subject against the bidder named in the
frame and a shared token would be rejected.

| | no auth | RS256, per request | RS256, cached |
|---|---|---|---|
| Throughput | 13,196 req/s | 7,539 req/s | **11,308 req/s** |
| p50 | 10.75 ms | 15.38 ms | 11.62 ms |
| p99 | 19.49 ms | **89.67 ms** | **35.37 ms** |
| p99.9 | 28.83 ms | 197.87 ms | 90.90 ms |
| Against the 50 ms budget | met | **missed** | met |

**Verifying an RS256 signature on every bid missed the budget by 40 ms.** It
cost more than the rest of the request put together — HTTP, framing, HMAC,
screening and the `acks=all` write combined. A bidder sends many bids under
one token, so almost every verification after the first was repeating
identical work.

Validating once per token instead of once per request brings p99 back to
35.37 ms. The entry is kept only until the token's own `exp`, so the cache
cannot extend a token's life (`ValidatedTokenCache`).

226,210 accepted, 226,210 on the topic, zero failures.

### The margin is thinner than it looks

35.37 ms against a 50 ms budget leaves far less headroom than the 19.49 ms
before auth. The residual ~16 ms is the authentication pipeline itself: header
parsing, the cache lookup, principal construction and policy evaluation. A
three-broker cluster at `min.insync.replicas=2` will add more on top, and that
has not been measured.

## Measured with real Kafka — 2026-10-04

4 shared vCPU, 15 GB. **Broker, catcher and load generator all on the same
host**, so three processes compete for four cores. Single-node KRaft broker,
replication factor 1, `acks=all`, 150 concurrent bidders, 20 seconds.

| | |
|---|---|
| Requests | 263,956 — all accepted, 0 rejected, **0 failed** |
| Throughput | 13,196 req/s |
| p50 | 10.75 ms |
| p90 | 13.99 ms |
| p99 | **19.49 ms** |
| p99.9 | 28.83 ms |
| max | 874.19 ms |

**Every accepted bid is on disk.** The topic's end offset read back as exactly
263,956 — the same number the API acknowledged. There is no gap between what a
bidder was told and what is durably recorded, which is the whole point of
`acks=all` (D-11).

### Against the target

The budget is p99 ≤ 50 ms at 10,000 bids/sec. This run clears both — 19.49 ms
and 13.2k/s — on a machine where the broker and the load generator are
stealing cycles from the service being measured.

### What it still does not prove

One broker at replication factor 1. Deployment runs three with
`min.insync.replicas=2`, where `acks=all` waits for a second replica over a
network rather than one local disk. Expect several more milliseconds.

The 874 ms maximum is worth watching — a GC pause or a broker flush. It does
not move p99.9 (28.83 ms), so it is rare, but under a real close it would be
one bidder's request taking most of a second.

## Measured without Kafka — 2026-10-04

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

**Does not:** this ran against `InMemoryBidLog`, not Kafka. Comparing the two
runs isolates what the durable write costs:

| | in-memory | real Kafka, `acks=all` |
|---|---|---|
| Throughput | 31,538 req/s | 13,196 req/s |
| p99 | 13.83 ms | 19.49 ms |

So the `acks=all` round trip adds roughly 6 ms at p99 here, and the broker
competing for the same four cores accounts for much of the throughput drop.

### An earlier run worth recording

A first pass with the default 20 bids/sec/bidder limit reported 36,686 req/s
at p99 16 ms — but 671,063 of 733,763 requests were rejected by the rate
limiter, which short-circuits before the log append. That measured the
rejection branch, not the accept path. Raising the limit for the benchmark is
what makes the number above meaningful.
