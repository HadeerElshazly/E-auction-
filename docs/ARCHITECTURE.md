# E-Auction Platform — Architecture

**Status:** Draft for review · **Version:** 0.1.0 · **Date:** 2026-10-04

Electronic public auction (المزاد العلني) platform for government land plot sales.
Built as a multi-client product: the first deployment is the Al-Saeed plan
(مخطط السعيد) sale for Jeddah Municipality, but nothing here is specific to it.

---

## 1. Scope

### In scope (v1)

| Capability | Arabic |
|---|---|
| Auction catalogue, browsing, plot detail | عرض المزادات والقطع والتصفح |
| Bidder registration, profile, eligibility | التسجيل والاشتراك |
| Terms booklet purchase | شراء كراسة الشروط |
| Deposit payment / bank guarantee upload | دفع التأمين أو رفع الضمان البنكي |
| Live ascending bidding with real-time winner | المزايدة اللحظية |
| Quiet-period auto-extension (anti-sniping) | تمديد المزاد |
| Admin portal: auction preparation workflow | إعداد المزاد |
| Award committee workflow + award letters | الترسية وخطابات الترسية |
| Per-auction Q&A | أسئلة المزاد |
| Notifications (SMS / email / push) | الإشعارات |
| Reports | التقارير |
| Mobile app (iOS + Android) | تطبيق الجوال |

### Deferred

- **Blind / sealed final round.** Mechanism seam and nullable config columns are
  retained so this is additive later. See §6.4.
- Proxy / auto-bidding up to a ceiling.
- Company (juridical person) bidders — individuals only in v1.
- Automated title transfer (الإفراغ) — status tracking + document handoff only.
- Call centre ticketing — read-only admin view only.

---

## 2. Decisions register

Settled during design discussion. Each entry is a commitment; changing one is a
design change, not a configuration change.

### Core mechanics

| # | Decision | Rationale |
|---|---|---|
| D-01 | **Live ascending (English) auction.** Visible current winner, repeated bidding | Slides 4–5 commit to المزايدة اللحظية |
| D-02 | **`auctionId` is the bidding key.** An auction sells 1..N plots as one indivisible package | Client confirmed: bidding is on the whole package, never on a plot within it |
| D-03 | **Kafka partition offset order is the ordering authority**, not timestamps | Clocks skew across pods by 1–10ms; a bidding war is decided inside that window. Offset order is a true total order |
| D-04 | **Quiet-period extension.** A bid inside the last `Q` extends the end by `Q`, up to `max_extensions` (default 3) | Anti-sniping; removes most timing disputes |
| D-05 | **Ties resolve themselves.** Equal bids are applied in offset order; the second fails the minimum-increment check | No separate tie-break rule needed while blind rounds are deferred |
| D-06 | **Reserve price (السعر الاحتياطي) is secret**, per auction, never revealed — including when an auction fails to meet it | It is a reserve |
| D-07 | **Award cascades on disqualification** to the next bidder whose bid is `>=` reserve | Client requirement |
| D-08 | **The committee awards, not the system.** `provisional_leader` and `awarded_winner` are separate fields with separate lifecycles | Slide 6: لجنة الترسية confirms, prints, signs, re-uploads |

### Platform

| # | Decision | Rationale |
|---|---|---|
| D-09 | **All services on-premise.** Docker Compose for dev, Kubernetes for deployment | Client decision |
| D-10 | **Kafka (Strimzi, KRaft mode)**, RF=3, `min.insync.replicas=2` | |
| D-11 | **`acks=all` on bid produce.** HTTP 202 is returned only after the delivery report | Losing a bid in a government land auction is a legal problem, not a performance footnote |
| D-12 | **Bid catcher holds no database.** State is rebuilt in memory from compacted topics | Removes the DB from the hot path; Kafka is the durable store |
| D-13 | **Gateway split.** Envoy/NGINX ingress on the bid path, WSO2 APIM for everything else | APIM's Synapse mediation adds tens of ms at the tail under burst — exactly when auctions are busiest. It earns its place on the governed APIs |
| D-14 | **Keycloak** as IdP, federating Nafath via OIDC redirect | Lighter than WSO2 IS, strong k8s operator |
| D-15 | **Nafath 2-digit step-up** at KYC, deposit payment and award acceptance — never on the bid path | Takes seconds and needs phone interaction |
| D-16 | **Debezium for the transactional outbox only**, never raw table CDC | Raw CDC leaks internal schema into the public event contract |
| D-17 | **Event-driven domain; WSO2 MI only at integration edges** (payment, SADAD, municipality systems) | MI is an ESB, not a human-workflow engine. Workflows are state machines in the owning service |
| D-18 | **React (web) + React Native/Expo (mobile)**, shared TypeScript contracts | |

### Security

| # | Decision | Rationale |
|---|---|---|
| D-19 | **JWT via Keycloak**, verified offline against cached JWKS. No introspection call in the hot path | |
| D-20 | **HMAC-signed bids.** Each bid carries `nonce + ts + HMAC(secret, payload)`; the secret never goes on the wire | ~200ns to verify. Gives replay protection and non-repudiation |
| D-21 | **Hash-chained append-only bid ledger** + signed receipt to the bidder | Slide 5: منع التلاعب بسجلات العروض والمزايدات |
| D-22 | **Leading bidder identity is masked** in public views (`مزايد #4`) | Privacy, anti-collusion, PDPL |
| D-23 | **The reserve price travels on its own topic** (`auctions.sealed`), never inside the public auction definition | Added while building auction-admin. The catcher and the query BFF both consume `auctions.upcoming`, and the BFF feeds public APIs — so a misconfigured BFF could leak the reserve. Splitting it makes D-06 an ACL guarantee instead of a thing every developer must remember |

### Performance target

**p99 bid acceptance ≤ 50ms at 10,000 bids/sec**, measured server-side from
request receipt to Kafka delivery report. Verified by the k6 harness in
`tools/loadtest/`.

---

## 3. Domain model

```
Auction (المزاد)                    ← THE BIDDING UNIT. auctionId is the key.
├── phase                           grouping label (Phase 1 / Phase 2)
├── channel                         online (عبر الإنترنت) | onsite (في الموقع)
├── name_ar, name_en
├── starts_at, ends_at
├── opening_price                   السعر الافتتاحي
├── reserve_price                   السعر الاحتياطي — SECRET
├── min_increment_policy            fixed | tiered bands
├── deposit_amount                  التأمين
├── brokerage_fee_pct               مبلغ الوساطة
├── booklet_price                   كراسة الشروط
├── quiet_period_seconds            Q — nullable (extension off)
├── max_extensions                  default 3
├── blind_round_enabled             NULLABLE — deferred, see §6.4
├── blind_duration_seconds          NULLABLE — deferred
├── max_blind_rounds                NULLABLE — deferred
├── status                          see §6.1
└── Plot[] (قطع)                    1..N parcels sold together, indivisible
    ├── deed_number
    ├── area_sqm
    ├── coordinates
    └── photos[]
```

**The package is indivisible.** The winner takes every plot in the auction.
The reserve applies to the package, and a cascade moves the whole package.

UI consequence: when an auction holds several plots, `تفاصيل الأرض` renders a
list — a bidder needs every parcel's deed number, area and photos before
bidding on the bundle. Total area and plot count are derived display fields.

---

## 4. Service map

| Service | Stack | Responsibility |
|---|---|---|
| **bid-catcher** | .NET 8 minimal API | Accept bids, verify, append to Kafka. No DB, no outbound calls |
| **bid-processor** | .NET 8 worker | Order by offset, apply auction rules, determine winner, maintain the ladder and the hash-chained ledger. **Implemented** (§14) |
| **auction-admin** | .NET 8 + Postgres + outbox | Auction CRUD, plots, documents, scheduling, preparation + award workflows. **Implemented** (§13) |
| **participant** | .NET 8 + Postgres + outbox | Registration, profile, booklet purchase, deposit, eligibility. **Implemented** (§17) |
| **payment** | .NET | Deposit, brokerage, refunds, settlement. PayTabs / SADAD adapters |
| **document** | .NET + MinIO | Photos, booklets, signed award letters. AV scan |
| **live-fanout** | .NET | SSE/WebSocket push of current price. Deliberately separate from the catcher |
| **notification** | .NET | SMS, email, FCM/APNs push |
| **query-bff** | .NET | Read model for web + mobile. Versioned API |
| **reporting** | .NET | التقارير |
| **admin-web** | React | Admin + committee portal (RTL, AR/EN) |
| **bidder-web** | React | Public catalogue + bidding (RTL, AR/EN) |
| **bidder-mobile** | React Native (Expo) | iOS + Android |

### Why live-fanout is separate from bid-catcher

The catcher is a write-only, low-memory, high-churn process. Fan-out holds
thousands of long-lived connections and is memory-heavy. Sharing a process
would couple the read blast radius to the write path. They scale on different
signals and fail independently.

---

## 5. Event contracts

### Topics

| Topic | Key | Compacted | Producer | Consumers |
|---|---|---|---|---|
| `auctions.upcoming` | `auctionId` | yes | auction-admin | bid-catcher, query-bff |
| `auctions.sealed` | `auctionId` | yes | auction-admin | **bid-processor only** — ACL restricted |
| `auctions.deposits` | `auctionId` | no | auction-admin | payment |
| `auctions.participants` | `auctionId:bidderId` | yes | participant | bid-catcher |
| `participants.payments` | `auctionId:bidderId` | no | participant | payment |
| `bids.{auctionId}` | `auctionId` | no | bid-catcher | bid-processor |
| `auctions.current-winner` | `auctionId` | yes | bid-processor | bid-catcher, live-fanout, query-bff |
| `bids.rejected` | `auctionId:bidderId` | no | bid-processor | notification, query-bff |
| `processor.checkpoints` | `auctionId` | yes | bid-processor | bid-processor (own recovery) |
| `auctions.lifecycle` | `auctionId` | no | bid-processor, auction-admin | all |

### Topic lifecycle

Topic creation is automated and **ordered**: `auctions.create-topic` runs at
approval time and must complete *before* the auction is published to
`auctions.upcoming`. Since the catcher only accepts auctions present in its
local store — which is fed by `auctions.upcoming` — this ordering guarantees
the topic exists before any bid can target it. Broker auto-create is disabled.

After settlement, topics are archived to object storage and deleted.

### Bid wire format

A fixed-width binary frame. The catcher reads a few fields at fixed offsets,
appends server metadata, and produces the same buffer — no object
materialisation, no allocation.

```
offset  size  field
     0     16  auctionId        (UUID)
    16     16  bidderId         (UUID)
    32      8  amountMinorUnits (int64, halalas)
    40      8  clientTimestamp  (int64, unix ms)
    48     16  clientBidId      (UUID — idempotency key)
    64      8  nonce            (int64)
    72     32  hmac             (HMAC-SHA256 over bytes 0..71)
   ---- server appends ----
   104      8  serverTimestamp  (int64, unix ms)
   112      4  catcherPodOrdinal
   116      1  channel          (0 = online, 1 = onsite)
   117     16  enteredByUserId  (onsite operator; zero for online)
```

`clientBidId` is mandatory: mobile networks drop mid-request, and a retry must
be safe. The processor deduplicates on it.

> JSON parsing would cost ~300ns and is **not** the bottleneck. The binary frame
> is chosen for the HMAC-over-exact-bytes property and zero allocation, not
> because JSON would be too slow. What actually determines latency is: no
> downstream calls, no DB, offline JWT verification, and the Kafka ack.

---

## 6. Auction lifecycle

### 6.1 Status state machine

```
Draft ──► PendingReview ──► Approved ──► Scheduled ──► Live
                   │                                     │
                   └──► Rejected                         ▼
                                                      Closing
                                                         │
                                                         ▼
                                            PendingEligibilityReview
                                                         │
                                                         ▼
                                                   PendingAward ◄────┐
                                                         │           │
                                      ┌──────────────────┤           │
                                      ▼                  ▼           │
                                  Awarded            Unsold          │
                                      │            (لم تُرسَ)         │
                                      ▼                              │
                           WinnerDisqualified ──► CascadeToNext ─────┘
                                      │
                                      ▼
                                   Settled ──► Closed
```

`CascadeToNext` re-enters `PendingAward`: **each cascade step is a full new
ترسية** — fresh committee confirmation, fresh award letters, fresh signature,
fresh notification. The award workflow is re-entrant by design.

### 6.2 The extension rule

```
bid arrives at time T, auction has end E, quiet period Q, extensions used X

if T ∈ [E−Q, E] and X < max_extensions:
    E ← E + Q
    X ← X + 1
```

### 6.3 The hard ceiling (why the catcher never knows the real end time)

The processor decides extensions; the catcher enforces the window. If the
catcher used the live end time, a bid arriving just after the original end
would be rejected before the catcher learned of the extension.

The catcher therefore gates on a **permissive ceiling** and never on `ends_at`:

```
hard_ceiling = ends_at
             + (max_extensions × quiet_period)
             + grace
```

With defaults (Q=2min, max=3): `ends_at + 7 minutes`. The catcher accepts
anything in `[starts_at, hard_ceiling]`. The processor — which sees the fully
ordered stream — is authoritative on the exact cutoff. The race disappears and
the catcher stays stateless about auction timing.

This also leaves room for the deferred blind rounds: the ceiling formula simply
grows by `max_blind_rounds × blind_duration` when that feature lands.

### 6.4 Deferred: blind final round

Not built. Two things retained so it is additive rather than a refactor:

1. `IWinnerStrategy` seam in bid-processor (`AscendingStrategy` today).
2. Nullable `blind_*` columns on `auction`, present from the first migration.

Adding these later would mean a migration against a table holding live and
already-awarded auctions, plus re-approval workflow churn. Nullable-and-unused
is free.

---

## 7. Bid flow

### 7.1 Hot path

```
 mobile / web
      │  HTTP/2 POST, 120-byte binary frame
      ▼
 Envoy ingress ──── rate limit per bidder ────┐
      │                                        │
      ▼                                        │
 bid-catcher                                   │
      │ 1. verify JWT (cached JWKS, offline)   │
      │ 2. verify HMAC                         │  all in-memory,
      │ 3. auction known? in [start, ceiling]? │  zero I/O
      │ 4. bidder eligible for this auction?   │
      │ 5. amount > current + increment?       │  ← advisory only
      │ 6. append server metadata              │
      ▼                                        │
  Kafka produce (acks=all) ────────────────────┘
      │
      │ delivery report
      ▼
  HTTP 202 + signed receipt
```

Steps 3–5 read in-memory state built from compacted topics. **Nothing in this
path makes a network call to another service.**

Step 5 is deliberately advisory — the catcher's price view is milliseconds
stale. It rejects obviously-too-low bids instantly for a good UX and keeps junk
out of the topic, but the processor remains authoritative.

### 7.2 Two-phase acceptance (a product decision, not just technical)

Because step 5 is advisory, a bid can be accepted at the edge and then rejected
by the processor. The UI must model both:

| Phase | Signal | Meaning |
|---|---|---|
| 1 | `202` + signed receipt | Durably recorded in Kafka. Not yet judged |
| 2 | SSE/push: `accepted` / `rejected` / `outbid` | The processor's authoritative verdict |

The React and React Native clients must show "جاري المعالجة" between the two,
never "فزت" on the 202 alone.

### 7.3 Catcher state, and why there is no database

| State | Source | Size |
|---|---|---|
| Open auctions + windows | `auctions.upcoming` (compacted) | hundreds of rows |
| Bidder eligibility | `auctions.participants` (compacted) | thousands of rows |
| Current price | `auctions.current-winner` (compacted) | one int64 per auction |
| Per-bidder rate limit | in-process token bucket | — |

On startup the catcher replays all three compacted topics from offset 0 and
rebuilds in seconds. Kafka is the durable store. This is exactly what satisfies
*"even if the service is down it can read it later"*: an auction approved while
the catcher was offline is still in the log when it comes back.

Every pod runs its own consumer group and holds a full copy. Cheap at this size.

### 7.4 Scaling

Stateless and horizontally scalable. HPA on CPU plus bids/sec.

**Auction-aware pre-warming:** the catcher knows every auction's end time, so
KEDA scales up *before* the close rather than reactively after latency has
already spiked.

---

## 8. Award, cascade and money

### 8.1 The ladder

The processor persists the **full ranked list** of valid bids per auction,
permanently. "Current winner" is a view over position 1. Without the full
ladder there is nothing to cascade down.

### 8.2 Cascade

```
Awarded(bidder A)
  └─ A fails compliance within the window
       ├─ A's deposit FORFEITED
       └─ next bidder B where bid(B) >= reserve
            ├─ yes → new ترسية for B (full committee cycle)
            └─ no  → Unsold (لم تُرسَ)
```

Reserve comparison is `>=` ("مش أقل من الاحتياطي").

### 8.3 Deposit hold — the operational trap

**Losers' deposits cannot be released when the auction closes.** If the cascade
can reach bidder #2 or #3, their deposit must be held through the compliance
window of every bidder above them.

Policy (configurable, default): hold the **top 3** until the award is final;
release the rest at close.

> ⚠️ **Contract terms, not configuration.** The hold policy, the forfeit rule
> and the compliance window must be fixed in the كراسة الشروط *before* any
> auction opens, because bidders accept the booklet. These move slowly —
> legal review is on the critical path. See §11.

---

## 9. Identity and security

### 9.1 Authentication

```
Bidder ──► Keycloak ──OIDC redirect──► Nafath ──► identity claims
                │
                └──► JWT (15 min) + refresh, auction-scoped claim
```

The catcher verifies the JWT signature offline against cached JWKS. **Keycloak
is never called on the bid path.**

### 9.2 Nafath step-up (2-digit code)

Used where non-repudiation matters, never on the bid path:

| Moment | Why |
|---|---|
| Registration / KYC | Verified national identity |
| Deposit payment | Non-repudiable consent to pay |
| Award acceptance | Strongest evidence the winner accepted |

### 9.3 Bid signing

The bidder holds a per-auction signing secret, minted at auction entry with
TTL = `hard_ceiling + grace`, bound to the Nafath-verified identity and logged.

- **Web:** WebCrypto `CryptoKey`, non-extractable. XSS can sign while on the
  page but cannot exfiltrate the key for later use.
- **Mobile:** iOS Keychain / Android Keystore — genuinely hardware-backed.
  Stronger than the browser.

### 9.4 Tamper-evident ledger

Every accepted bid is appended to a hash-chained log:

```
record_n.hash = SHA256(record_n.payload || record_{n-1}.hash)
```

A broken chain is detectable. The bidder's signed receipt lets them prove their
bid was accepted at time T for amount X. Together these answer slide 5's
requirement directly.

---

## 10. Deployment

### 10.1 Portability constraints

Two environment facts are **unknown** at time of writing (see §11), so the
charts are written defensively:

- **Kubernetes distribution unknown** → the chart carries both paths behind a
  single `platform` value. **Built and verified** — see §16.
- **Outbound internet access unknown** → every external call (Nafath, PayTabs,
  SADAD, SMS, FCM/APNs) goes through a single configurable **egress proxy
  abstraction**. Works unchanged whether the DC has full access, a whitelist,
  or is fully air-gapped behind a DMZ relay.

### 10.2 Chart structure

```
deploy/helm/e-auction/
├── Chart.yaml              version 0.1.0 = the deployment version
├── values.yaml             defaults
├── values-openshift.yaml   platform overlay
├── values-jeddah.yaml      client overlay
├── charts/
│   └── e-auction-common/   library chart: shared templates
└── templates/              one file per service
```

Chart version drives the deployment version. Images are tagged with
`appVersion`. Charts are pushed as OCI artifacts alongside images.

This is a library chart plus one application chart, **not** the umbrella and
subcharts originally proposed here. Subcharts buy independent versioning, and
the chart version *is* the deployment version — a client names one number for
the whole release. Three near-identical subcharts would be duplication in
exchange for a property we deliberately do not want. Reasoning in
`deploy/helm/README.md`.

### 10.3 Local development

`docker-compose.yml` brings up the full stack: Kafka (KRaft), Postgres,
Keycloak, MinIO, and every service. Configuration is env-var only (12-factor),
so Compose and Helm share one source of truth.

### 10.4 Infrastructure defaults

| Component | Choice |
|---|---|
| Kafka | Strimzi operator, KRaft, 3 brokers, RF=3, `min.insync.replicas=2` |
| Postgres | CloudNativePG |
| Object storage | MinIO (S3 API — keeps code portable) |
| Secrets | External Secrets Operator; sealed-secrets as fallback |
| Observability | kube-prometheus-stack + OpenTelemetry + Loki |
| Service mesh | None in v1. Envoy ingress + cert-manager mTLS where needed |

---

## 11. Open items

### Environment facts — needed before charts and CI are final

| # | Question | Status |
|---|---|---|
| E-1 | On-prem Kubernetes distribution | **Still open.** Plain k8s + OpenShift compatibility layer (§10.1) |
| E-2 | Outbound internet | **Answered: full access.** Nafath, PayTabs, SADAD and SMS are called directly. The egress abstraction is kept anyway — this is a multi-client product and the next client may be restricted |
| E-3 | ACR reachable from on-prem | **Answered: yes.** ACR for images and charts; no local Harbor needed |

### Contract terms — needed before go-live, not before code

These block the كراسة الشروط, and legal review is slow. Start them now.

| # | Question | Default used |
|---|---|---|
| C-1 | Compliance window before disqualification | 5 business days |
| C-2 | Enumerated disqualification reasons (مش مطابق) | Non-payment · document mismatch · failed eligibility re-check · withdrawal · Nafath identity mismatch |
| C-3 | Deposit hold policy | Hold top 3 until award final |
| C-4 | Disqualified winner's deposit | Forfeited |
| C-5 | Terms booklet price, refundability | Non-refundable, per auction |

### Product data

| # | Question | Note |
|---|---|---|
| P-1 | 327 or 372 plots? Deck says 372 total but 165 + 162 = 327 | Reference data only |
| P-2 | Report definitions for التقارير | Framework stubbed |
| P-3 | Confirm PayTabs and SADAD merchant accounts exist | Adapter interface built either way |

### Known risk

**Apple In-App Purchase.** The كراسة الشروط is a downloadable digital good sold
in-app. Apple's carve-out for "goods and services used outside the app" should
apply — land is physical — but a PDF sold in-app is exactly the grey zone App
Review argues over. Mitigation: make the booklet purchase web-only with the app
deep-linking out, or get a pre-submission ruling. Cheap to design around now; a
rejected build days before launch is not.


---

## 12. Validation status

A vertical slice of the hot path is implemented and tested: `EAuction.Core`
(frame codec, bid log, auction engine, ledger), `EAuction.BidCatcher` (the
HTTP endpoint) and `EAuction.BidProcessor` (the pump that drives the engine).

### Verified

**37 tests pass** (`dotnet test tests/EAuction.Tests`), covering:

| Claim | Where |
|---|---|
| Binary frame round-trips; HMAC rejects a forged or altered amount | `BidFrameTests` |
| Server metadata does not invalidate the client signature | `BidFrameTests` |
| Minimum increment and opening price enforced | `AuctionEngineTests` |
| Equal bids resolve by offset order with no tie-break rule (D-05) | `AuctionEngineTests` |
| A leader cannot outbid themselves | `AuctionEngineTests` |
| A retried bid with the same `clientBidId` is not counted twice | `AuctionEngineTests` |
| Quiet-period extension slides the end, and stops at `max_extensions` (D-04) | `AuctionEngineTests` |
| Extension lets a bid land that the original end time would have refused | `AuctionEngineTests` |
| Catcher accepts past `ends_at` but inside the hard ceiling (§6.3) | `EndToEndTests` |
| Ineligible bidder, unknown auction and forged signature rejected at the edge | `EndToEndTests` |
| Concurrent bids produce one total order and one winner | `EndToEndTests` |
| Replaying the log twice reproduces the identical winner and ledger head | `EndToEndTests` |
| Cascade walks the ladder, honours `>=` reserve, skips the disqualified | `LadderAndLedgerTests` |
| Altering or removing a ledger record breaks the hash chain (D-21) | `LadderAndLedgerTests` |

Load: **630,816 requests, 31,538 req/s, p99 13.83 ms, zero failures** on four
shared vCPU with the generator on the same host. See `tools/loadtest/README.md`.

### Not yet verified

| Gap | Why |
|---|---|
| **`KafkaBidLog` has never run against a broker** | No Docker daemon in the build container. The pipeline is validated through `InMemoryBidLog`, which mirrors the same per-partition ordering contract, but the Kafka path is unexercised code |
| ~~The `acks=all` round trip is not in the measured latency~~ | **Measured** — 13,196 req/s at p99 19.49 ms with a real broker, every accepted bid verified on disk. See `tools/loadtest/README.md` |
| **The target is met on one broker, not three** | Deployment runs 3 brokers at `min.insync.replicas=2`, where `acks=all` waits for a replica over a network rather than one local disk |
| **`docker-compose.yml` is unrun** | Same reason. Treat the first `up` as work, not as a regression |
| **JWT verification is not wired** | The seam is documented in `Program.cs`. The HMAC signature (D-20), which binds a bid to a bidder, is implemented |
| **Admin, participant, payment, fan-out, notification services** | Not started — the slice covers the bid path only |

### Next

1. Run the Compose stack and point the catcher at real Kafka; re-measure.
2. Wire JWT verification against cached JWKS.
3. Auction-admin service with the outbox, and the approval workflow that
   publishes to `auctions.upcoming`.

---

## 13. Auction administration service

Implements both workflows from slide 6: **إعداد المزاد** (prepare) and
**الترسية** (award), with the transactional outbox.

### The outbox, and why it is not CDC

Domain events raised by the `Auction` aggregate are drained into outbox rows
inside `SaveChangesAsync`, so the event and the state change share one
transaction. The event cannot exist without the state change, and the state
change cannot commit without the event queued.

The table uses **Debezium EventRouter's default column names verbatim**
(`id`, `aggregatetype`, `aggregateid`, `type`, `payload`), so the production
path needs no bespoke publisher — Debezium tails the WAL and routes. See
`deploy/debezium/`.

CDC directly off the domain tables was rejected (D-16): it makes the internal
schema the public event contract, and every column rename becomes a breaking
change for consumers.

A polling `OutboxRelay` ships alongside for local development and small
deployments. The two are interchangeable because the contract is the table,
not the publisher. **Run one or the other, never both** — both are
at-least-once, so running both doubles every event.

### Topic ordering guarantee

The relay creates an auction's bid topic **before** publishing the auction to
`auctions.upcoming`. Broker auto-create is off, so without that ordering the
catcher could accept a bid for a topic that does not exist. Tested in
`OutboxTests.The_relay_creates_the_bid_topic_before_publishing_the_auction`.

### Where the reserve price goes

Approval raises two events, not one:

| Event | Topic | Contents |
|---|---|---|
| `AuctionApproved` | `auctions.upcoming` | the public definition — **no reserve field exists on the type** |
| `AuctionReserveSet` | `auctions.sealed` | the reserve alone, ACL'd to bid-processor |

`AuctionResponse`, the service's own API view, also omits the reserve — it is
not returned to anyone through HTTP, including admins, and not after an
auction fails to reach it. A test asserts the public event type has no
property whose name contains "Reserve", so adding one fails the build rather
than leaking quietly.

### The award workflow is re-entrant

A cascade creates a **new `Award` row**, never mutates the previous one. Each
step is a full new ترسية — fresh committee confirmation, fresh letters, fresh
signature — and the record of who was awarded and why it moved on survives.

Order is enforced: an award letter must exist before a signed one can be
uploaded, and the winner cannot be notified before the signed letter is back.
Telling a winner before the letter is executed announces a decision that is
not yet binding.

`DepositsReleasable` is emitted **only** on settlement or unsold — never when
bidding closes. While the cascade can still reach a losing bidder, their
deposit is held through the compliance window of everyone above them (§8.3).

### Editing stops at approval

Edits are confined to `Draft` and `Rejected`. Once approved, the auction is
public and bidders have relied on its terms; changing the dates or the deposit
underneath them is not an edit, it is a different auction.

### Verified

**33 tests pass** against a real PostgreSQL instance — not an in-memory
provider, because the outbox's entire claim is transactional and an in-memory
provider has no transactions worth testing.

| Claim | Where |
|---|---|
| Approval writes the state change and both events in one commit | `OutboxTests` |
| A rolled-back transaction leaves neither the state change nor the event | `OutboxTests` |
| The public payload contains no reserve price; the sealed one does | `OutboxTests` |
| The bid topic is created before the auction is published | `OutboxTests` |
| Each aggregate type routes to its own topic, keyed by auctionId | `OutboxTests` |
| A relayed message is not published twice | `OutboxTests` |
| A failed publish leaves the message queued for retry | `OutboxTests` |
| Incomplete auctions report every problem at once and cannot be submitted | `PreparationWorkflowTests` |
| A reserve below the opening price, or a past start date, is refused | `PreparationWorkflowTests` |
| Editing is refused once approved | `PreparationWorkflowTests` |
| The committee confirms the award; the system only offers a candidate | `AwardWorkflowTests` |
| The winner is notified only after the signed letter returns | `AwardWorkflowTests` |
| A cascade creates a fresh award and preserves the previous one | `AwardWorkflowTests` |
| Deposits are not releasable until the award is final | `AwardWorkflowTests` |
| Lifecycle transitions cannot be skipped | `AwardWorkflowTests` |

### A bug this work surfaced

EF Core treats a pre-assigned Guid primary key on an entity discovered through
a navigation as proof the row already exists, and emits an `UPDATE` instead of
an `INSERT`. The update affects zero rows and throws
`DbUpdateConcurrencyException`.

It did not show when saving a new aggregate — an explicit `Add` cascades
`Added` to everything reachable — only when adding an `Award` to an
already-tracked `Auction`, which is exactly the cascade path. Fixed with
`ValueGeneratedNever()` on every domain-assigned key, which is also simply
accurate: these identities come from the domain, not the store.

### Not yet done

| Gap | Note |
|---|---|
| `KafkaTopicPublisher` has never run against a broker | Same constraint as `KafkaBidLog`: no Docker daemon. Routing and ordering are tested through the in-memory publisher |
| The Debezium connector is unrun | Config is written against EventRouter's documented defaults and the table matches them, but it has not been registered against a live Connect cluster |
| No authentication or authorisation | Every endpoint is open. Committee actions in particular must be role-gated before any real use |
| Documents are referenced by id only | The document service does not exist yet; `BookletDocumentId` and the letter ids are not validated against anything |
| Lifecycle transitions are manual | `MarkLive`, `MarkClosing` and `OfferCandidate` are endpoints. They should be driven by `auctions.lifecycle` from the processor |

---

## 14. Bid processor wiring

The processor is now wired end to end, and with auction-admin's lifecycle
consumer the loop closes: an approved auction runs itself through bidding, a
close, a candidate for the committee, and a cascade if that candidate fails.

### The loop

```
auction-admin                          bid-processor
─────────────                          ─────────────
Approve
  ├─ AuctionApproved ──► auctions.upcoming ─┐
  └─ AuctionReserveSet ─► auctions.sealed ──┤
                                            ▼
                                     AuctionRegistry
                                   (waits for both halves)
                                            │
relay publishes                             ▼
  └─ Approved → Scheduled            AuctionSupervisor
                                            │
   ◄── AuctionStarted ──────────────────────┤
Live                                        │
                                       bids processed
                                       ├─► auctions.current-winner
                                       └─► bids.rejected
                                            │
   ◄── AuctionClosed ───────────────────────┤  at effectiveEnd + grace
PendingEligibilityReview                    │
   ◄── CandidateOffered ────────────────────┘
PendingAward
   │
   ├─ committee confirms ──► Awarded
   │
   └─ DisqualifyWinner
        └─ WinnerDisqualified ──► auctions.lifecycle
                                            │
   ◄── CandidateOffered (step n+1) ─────────┤  next bidder ≥ reserve
   ◄── LadderExhausted ─────────────────────┘  or nobody qualifies
Unsold
```

### Who decides what

The processor says what happened to the bidding. **It never awards anything.**
The most it does is put a candidate in front of the committee (D-08), and
`CandidateOffered` carries no reserve price — the committee learns that
someone qualifies, not what they had to beat (D-06). A test asserts no payload
on `auctions.lifecycle` contains the reserve.

### Two halves of a definition

The public definition and the reserve arrive on separate topics because the
reserve is ACL-restricted (D-23). Either can land first, so `AuctionRegistry`
holds a partial auction until both are present.

They are written in one transaction by auction-admin and relayed in order, so
the gap is normally microseconds. A persistent gap means the sealed topic is
misconfigured, which the registry reports through `AwaitingReserve` rather than
papering over by running an auction with no reserve — a loud failure beats a
silently wrong one.

### Closing, and why it is not a scheduled timer

A quiet-period bid moves `EffectiveEndsAt`, so a deadline computed once would
close the auction while bidding was still legitimately open. The supervisor
re-reads the engine's current end on every tick instead.

Close fires at `effectiveEnd + CloseGrace` (default 5s). The grace exists
because the catcher accepts up to the hard ceiling: bids can still be in
flight when the clock passes the end, and the grace lets them land and be
rejected **on the record** rather than vanish.

Tick interval bounds how late a close can be, not how accurate the result is —
the log decides that, and a replay reproduces it exactly.

### At-least-once everywhere

Both directions redeliver, and both sides are built for it:

- A redelivered `WinnerDisqualified` must not advance the cascade twice and
  silently skip a qualifying bidder. The supervisor keeps a disqualified set
  and ignores a repeat.
- A redelivered `AuctionStarted` or `AuctionClosed` finds the auction already
  past that transition. The aggregate's transition guard throws, and the
  consumer logs and moves on — that is the expected path, not a failure.
- Admin's own events come back on `auctions.lifecycle` and are ignored.

### Contract drift

The processor declares its own view of admin's events rather than sharing the
types, so the dependency points the right way and admin can add fields without
recompiling consumers. That only works if the shapes line up, so
`ContractDriftTests` serialises the **real producer types** and deserialises
them as the processor's payloads. If admin renames a field, the test fails
instead of production going quiet.

### Verified

**54 tests** in `EAuction.Tests` (37 bid path + 17 wiring and contract) and
**41** in `EAuction.AuctionAdmin.Tests`. New coverage:

| Claim | Where |
|---|---|
| An auction runs only once both halves of its definition arrive, in either order | `ProcessorWiringTests` |
| An accepted bid publishes the new current winner; a rejected one publishes its reason | `ProcessorWiringTests` |
| An auction does not close before its end time | `ProcessorWiringTests` |
| Closing offers the highest bidder clearing the reserve, or reports the ladder exhausted | `ProcessorWiringTests` |
| A quiet-period bid pushes the close out | `ProcessorWiringTests` |
| Closing happens once however many ticks follow | `ProcessorWiringTests` |
| The reserve never appears on the lifecycle topic | `ProcessorWiringTests` |
| A disqualification offers the next qualifying bidder; a redelivered one does not skip anyone | `ProcessorWiringTests` |
| The processor reads every field of real admin event types | `ContractDriftTests` |
| The relay moves an auction to Scheduled when it actually publishes | `LifecycleConsumerTests` |
| Started / Closed / CandidateOffered / LadderExhausted advance the workflow | `LifecycleConsumerTests` |
| Offering a candidate is not awarding one | `LifecycleConsumerTests` |
| Redelivered events are ignored rather than failing | `LifecycleConsumerTests` |

### Not yet done

| Gap | Note |
|---|---|
| `KafkaEventStream` and `KafkaBidLog` have never run against a broker | Still no Docker daemon. Both are validated through in-memory implementations mirroring the same ordering contract |
| ~~Processor state is not durable across restarts~~ | **Fixed** — see §15 |
| One pump per auction, all in one process | Fine at this scale. Sharding auctions across processor instances by consumer group is not done |
| No supervisor backpressure | A very hot auction's pump publishes to `current-winner` on every accepted bid. Should coalesce |

---

## 15. Processor restart

A restart used to replay every bid from offset 0 and republish as it went —
deterministic, so the result was right, but it re-sent an outbid notification
for every bid the auction ever had.

### The split that fixes it

Two different questions were being conflated:

| Question | Answer |
|---|---|
| What is the auction's state? | Replay **every** bid from offset 0. Deterministic, reproduces exactly, needs no stored state |
| What have consumers already seen? | A **checkpoint**: the highest bid offset whose side effects were published |

So the replay still runs in full — that is what rebuilds the price, leader,
ladder, extension count and ledger — but the supervisor stays **silent** over
every offset at or below the checkpoint. Nothing is recomputed from a snapshot,
and nothing is re-announced.

The checkpoint lives on a compacted topic, `processor.checkpoints`, keyed by
auction, because the processor must be restartable on any node.

### Why checkpoints are periodic

Committing on every bid would double the write load on the hot path. Delivery
is at-least-once either way, so the only cost of a stale checkpoint is
republishing the tail since the last commit — bounded by the commit interval
(default: 100 records or 2 seconds), not by the length of the auction. A test
pins that bound.

The one place it commits unconditionally is immediately before announcing a
close, so everything the close implies is durable before anyone is told.

### Recovery before action

Startup has two phases, and the order matters:

1. **Recover.** Replay `processor.checkpoints`, `auctions.upcoming`,
   `auctions.sealed`, then `auctions.lifecycle` — to a quiet point. The
   supervisor records only; it takes no action.
2. **Resume.** Start the bid pumps. From here it acts on what it consumes.

Acting during phase 1 would re-announce auctions, re-close them, and re-offer
candidates the committee has already seen.

**The processor's own published events are its recovery log.** Replaying
`auctions.lifecycle` tells it which auctions it already announced, which it
already closed, how many candidate offers it already made, and who has been
disqualified. Cascade position is the number of disqualifications, which is
exactly how it is incremented in the first place — so there is nothing extra
to persist.

### Re-issuing an owed offer

One window needs explicit repair: the processor dies after a disqualification
reaches the topic but before the next candidate is published. Nothing would
ever offer it, and the committee would wait forever.

Resume reconciles it. One offer is owed per close, plus one per
disqualification; if fewer were published, the missing one is issued.

**A bug this surfaced.** The first version reconciled immediately after
launching the pumps — before the replay had rebuilt the ladder. It read an
empty engine and published `LadderExhausted` on an auction with a perfectly
good next bidder: an auction wrongly declared unsold, from a restart.

The fix needed the log to say where it ends, so `IBidLog.GetEndOffsetAsync`
was added (Kafka watermarks; record count in memory). Resume now waits for the
replay to reach the end of the log before reading the rebuilt ladder.

### Verified

Eight tests in `ProcessorRestartTests`:

| Claim |
|---|
| A restart republishes nothing for bids already handled |
| A restart rebuilds price, leader and ledger head exactly |
| A restart does not resend rejections |
| A restart after a close does not close, announce or offer again |
| A restart restores the cascade position and the disqualified set |
| An offer owed when the process died is re-issued on resume |
| Bids arriving after a restart publish normally |
| A stale checkpoint republishes only what it missed, not the whole auction |

---

## 16. Portable Helm charts

The on-prem Kubernetes distribution is still unknown, and guessing wrong is
not a small problem: a chart written for plain Kubernetes does not degrade on
OpenShift, it is **rejected**. So the chart carries both paths behind one
switch.

```yaml
platform: kubernetes   # or: openshift
```

### What actually differs

Two things, and nothing else:

| | plain Kubernetes | OpenShift |
|---|---|---|
| External traffic | `Ingress` | `Route` |
| Pod UID | pinned to 10001 | assigned by the SCC |

**Exposure.** OpenShift ships no Ingress controller by default; it has Routes,
served by its own router. Plain Kubernetes has no `Route` type at all, so a
chart shipping one is refused by the API server before anything runs.

**UID.** OpenShift's `restricted-v2` SCC gives each namespace a UID range and
admits a pod only if it does not request a UID outside it. Hard-coding
`runAsUser` makes the pod unschedulable. So on OpenShift the chart states the
requirement — `runAsNonRoot` — and lets the platform choose the number. Plain
Kubernetes assigns nothing, so there the chart must pin it.

Everything else is identical and equally hardened on both: no privilege
escalation, all capabilities dropped, read-only root filesystem, RuntimeDefault
seccomp, and `/tmp` as an emptyDir so an arbitrary assigned UID still has
somewhere to write.

### Verified

`deploy/helm/test-chart.sh` lints, renders, asserts and packages:

| Claim |
|---|
| Both platforms lint and render |
| Ingress on Kubernetes, Route on OpenShift, neither crosses over |
| UID pinned on Kubernetes, delegated on OpenShift |
| Non-root, read-only rootfs, no privilege escalation, seccomp — both platforms |
| Credentials referenced from Secrets, never templated into output |
| Chart packages |

The rendered manifests were also parsed and structurally checked: every object
labelled, every deployment hardened, probes present, resource requests set, a
writable `/tmp`, and the backends wired to the right services.

**The test was checked to have teeth.** Removing the OpenShift UID guard —
exactly the mistake the chart exists to prevent — makes it fail, while
`helm lint` passes the broken chart without comment. That gap is the whole
reason the script exists: the failure would otherwise surface on the client's
cluster as a pod that will not schedule.

### Not yet done

| Gap | Note |
|---|---|
| **Never deployed to a real cluster** | Linted, rendered, validated and packaged, but no cluster was available. The first `helm upgrade --install` is part of the work |
| `bidProcessor.replicas` must stay 1 | Auctions are not sharded across processor instances; a second replica would drive every auction in parallel. `NOTES.txt` warns if it is raised |
| Reactive autoscaling only | CPU-driven pods arrive after the spike. KEDA on auction end times is the real answer (§7.4) |
| No charts for Kafka, Postgres, Keycloak, MinIO | Strimzi and CloudNativePG have their own operators; these are dependencies to declare, not to reimplement |
| No NetworkPolicy | `auctions.sealed` is ACL'd at the Kafka level (D-23), but pod-level isolation is not expressed |
| No front-end charts | React apps and the mobile BFF are not built yet |

---

## 17. Participant service, and the catcher's missing wiring

### The gap this closed

The bid catcher's state came from nowhere. `CatcherState` had `UpsertAuction`,
`GrantEligibility` and `UpdateCurrentPrice`, and nothing called them outside
the dev-seed endpoint and the tests — so in any real deployment the catcher
started empty and rejected every bid as an unknown auction. It passed its
tests because the tests injected the state directly.

`auctions.participants` was not even declared in `Topics`; it existed as a
string in a comment. Nothing produced it, because the participant service did
not exist.

Both halves are now built: `ControlPlane` fills the catcher from
`auctions.upcoming`, `auctions.participants` and `auctions.current-winner`, and
the participant service produces the eligibility topic.

### Deriving the signing key instead of distributing it

The obvious design has the participant service mint a random per-auction
secret and publish it on `auctions.participants` for the catcher to read. That
puts a live credential on a topic, readable by anything with topic access and
retained in the log until compaction happens to catch up — and compaction is a
background process with no deadline.

Deriving removes the problem rather than guarding it. Both services hold the
same master key, from the cluster's secret store and never from Kafka:

```
secret = HMAC-SHA256(master, "eauction-bid-key-v1" || auctionId || bidderId || epoch)
```

The topic then carries the eligibility fact and an epoch. Reading the whole
topic tells you **who may bid, not how to bid as them**. A test asserts no
payload on that topic contains a derived secret.

The epoch rotates one bidder's key — a lost phone, a suspected leak — without
touching the master or anybody else's. The catcher derives once when
eligibility is granted and keeps the result in process memory, so the hot path
still costs exactly one HMAC: the one that verifies the frame.

### The admission path

```
Nafath assertion ──► Bidder registered (identity is never self-asserted)
         │
         ▼
   CompleteProfile  (إكمال الملف الشخصي)
         │
         ▼
   PurchaseBooklet  (شراء كراسة الشروط) ── non-refundable, payment ref recorded
         │
         ▼
   AcceptTerms      (الموافقة على الشروط والأحكام) ── timestamped
         │
         ▼
   ChooseDeposit ───► DepositRequested ──► participants.payments
         │
         ├─ Payment:        ConfirmDepositPayment(ref)
         └─ BankGuarantee:  SubmitBankGuarantee → VerifyBankGuarantee(admin)
         │
         ▼
      Eligible ──► ParticipantEligibilityChanged ──► auctions.participants
                                                           │
                                                           ▼
                                                     bid catcher
```

Eligibility re-checks every requirement at the moment it is granted rather
than trusting the status it arrived in — this is the point where the catcher
starts accepting the bidder's money.

**Uploading a bank guarantee is not settling one.** A guarantee is worth
nothing until an administrator has checked it is real, and one that expires
before the auction ends is refused outright: it would be worthless at exactly
the moment it is needed.

### Shared outbox library

The relay, the Debezium-shaped `OutboxMessage` and the Kafka publisher moved
to `EAuction.Outbox`, used by both auction-admin and participant. Duplicating
~250 lines of at-least-once relay logic across services is exactly the kind of
thing that drifts.

Service-specific behaviour goes through `IOutboxRouter`: where a row is
routed, what must happen before publishing it (auction-admin creates the bid
topic before announcing the auction), and what becomes true after
(auction-admin moves Approved to Scheduled).

### Verified

**135 tests.** New coverage:

| Claim | Where |
|---|---|
| A key derived on the participant side verifies on the catcher side, with only the epoch travelling | `ControlPlaneTests`, `AdmissionPathTests` |
| No signing secret appears anywhere on the participants topic | `AdmissionPathTests` |
| A bidder who completes subscription can bid; one who has not cannot | `AdmissionPathTests` |
| Revoking a subscription stops the bidder at the catcher | `AdmissionPathTests` |
| Rotating the key invalidates the secret the bidder already had | `AdmissionPathTests` |
| A rolled-back subscription never reaches the topic | `AdmissionPathTests` |
| A catcher started cold rebuilds everything from the log | `ControlPlaneTests` |
| Subscription steps cannot be taken out of order | `SubscriptionTests` |
| An unverified identity never becomes eligible | `SubscriptionTests` |
| A guarantee expiring before the auction ends is refused | `SubscriptionTests` |
| Uploading a guarantee is not settling it | `SubscriptionTests` |
| Deposit resolution is applied once however often it arrives | `SubscriptionTests` |

**A latent test bug this surfaced.** `CatcherState.Screen()` consumes a
rate-limit token, so polling it as a wait predicate drains the bucket (20/sec)
long before a 5-second wait expires, and the condition can never become true.
Two earlier `ControlPlaneTests` had the same pattern and passed only because
their condition happened to be met in the first few polls. Fixed by lifting
the limit in tests that are not about rate limiting.

### Not yet done

| Gap | Note |
|---|---|
| **Nafath is not integrated** | `POST /bidders/nafath` stands in for the callback and is open. It must be gated before any real use — identity is the one thing a bidder cannot be allowed to assert about themselves |
| **No authentication on any endpoint** | Including the signing-key endpoint, which hands out a bidder's credential to anyone who asks |
| Payments are references, not integrations | `DepositRequested` is published; nothing consumes it. PayTabs and SADAD adapters are not built |
| Documents are ids only | The bank guarantee is a `Guid` validated against nothing |
| Company bidders | Individuals only; Nafath's delegation path is a different integration |

---

## 18. Kafka, against a real broker

Three Kafka implementations and a Debezium connector were written without a
broker ever being available. In-memory stand-ins mirror the ordering contract,
but they cannot tell you whether the *client* is configured correctly — and
that is where the bug was.

A single-node KRaft broker now runs with no Docker, straight from Java:

```bash
./tools/kafka/run-local-broker.sh start
KAFKA_BOOTSTRAP=127.0.0.1:9092 dotnet test EAuction.sln
./tools/kafka/run-local-broker.sh stop
```

Without `KAFKA_BOOTSTRAP` those 16 tests **skip** rather than fail, so the
rest of the suite still runs anywhere.

### The bug it found

`KafkaBidLog.ReadAsync` assigned **partition 0**. The producer keys every bid
by `auctionId`, and the relay created each bid topic with **12 partitions**.
One key hashes to exactly one partition — essentially never partition 0.

So the reader attached to a partition the bids were not in, and **read
nothing**. No error, no exception, no warning. An auction with a full bid log
would have looked empty: no winner, no candidate for the committee, nothing to
explain it. Every in-memory test passed throughout, because an in-memory log
has no partitions to get wrong.

Two changes:

- **A bid topic has exactly one partition, and that is no longer
  configurable.** Topic-per-auction means the topic *is* the ordering domain
  (D-03). Extra partitions do not spread load — every record carries the same
  key, so they all land on one anyway — they just put that one somewhere the
  reader is not looking. Scale comes from more auctions, which means more
  topics. The setting was removed from configuration rather than given a safer
  default, because exposing it only invites the bug back. Compose said 6 and
  the admin service defaulted to 12; both were wrong.
- **The reader checks and fails loudly.** `ReadAsync` and `GetEndOffsetAsync`
  verify the topic has one partition and throw a named error otherwise. Silent
  data loss becomes a startup failure.

### What now runs on a real broker

| | |
|---|---|
| `KafkaBidLog` | append returns durable offsets, offsets increase, replay from 0, resume from an offset, end-offset watermarks, **60 concurrent appends still yield one total order** (D-03), multi-partition topic refused |
| `KafkaEventStream` | key and event-type header round-trip, a cold consumer replays a topic from the beginning, two consumers each get a full copy (every catcher pod holds the whole control plane, so they must not share a group) |
| `KafkaTopicPublisher` | `EnsureTopicAsync` creates with one partition and is idempotent on repeat, published rows readable with their event type |
| Full pipeline | approval and reserve on their topics → registry assembles the auction → bids through the log → close → `CandidateOffered`; current-winner reaches the topic the catcher reads; quiet-period extension pushes the close out; checkpoints survive on their topic |

**151 tests** with the broker; 135 without.

### A test-design fault this surfaced

Several tests polled `CatcherState.Screen()` as a wait predicate. `Screen()`
takes a rate-limit token on every call, so a polling loop drains the bucket and
the condition can never become true. It passed in isolation and failed when
four test assemblies and a JVM shared four cores — the worst kind of flake,
since it looks like slowness.

Raising the timeout did not fix it, which was the clue: the waits now poll
non-mutating accessors (`TryGetSigningSecret`, `TryGetCurrentPrice`) and assert
on `Screen()` once, afterwards.

### Still not done

| Gap | Note |
|---|---|
| **The Debezium connector is still unregistered** | Needs a Kafka Connect cluster, which this broker is not. The config is written against EventRouter's documented defaults and the table matches them |
| Single broker, replication factor 1 | Deployment is 3 brokers, RF=3, `min.insync.replicas=2`. `acks=all` returning `Persisted` means less with one replica than with three |
| No ACL enforcement tested | `auctions.sealed` and `auctions.participants` rely on topic ACLs (D-23). PLAINTEXT with no authorization here |
| No failure injection | Broker restarts, leader elections and partition unavailability are untested |

---

## 19. Authentication and authorization

Every endpoint was open, including the one that hands a bidder the credential
that signs their bids. That is now closed.

### Tokens, verified offline

Keycloak issues the tokens; every service validates them against the issuer's
**cached signing keys** (D-19). No request waits on the identity provider, and
no bid does in particular — an introspection call per bid would put Keycloak
on the hot path and make its availability the auction's availability.

Keycloak nests realm roles under `realm_access.roles`, which no standard
handler reads. `KeycloakRoles.Project` copies them into role claims on token
validation. A test mints tokens with that exact nesting, because a test using
flat role claims would prove nothing about the real token — and if this
regressed, every policy would silently deny and the system would look broken
rather than insecure.

A **fallback policy** requires an authenticated user, so a new endpoint is
closed unless it opts out. Only the health probes do.

### The bidder's identity is the Keycloak subject

`Bidder.Id` **is** the `sub` claim rather than a separate identifier. No token
mapper, no custom claim, no lookup stands between a request and knowing who
made it.

That matters most in the catcher, which cannot afford a lookup on the hot
path — and a service that cannot check ownership cheaply tends to stop
checking it.

### Two proofs on the bid path, for two different questions

| | Proves |
|---|---|
| **JWT** | *Who is calling.* Revocable, short-lived, tied to a session |
| **HMAC** (D-20) | *What they asked for.* Non-repudiable, bound to the exact amount |

The catcher checks that the token's subject matches the bidder named in the
frame:

```
caller = token.sub
if caller != frame.bidderId  →  403 BidderMismatch
```

**Correction.** An earlier version of this section said the signature alone
would not catch an impersonated frame. That was wrong, and it came from a test
that gave two bidders the same secret — something that never happens in
practice. Keys are derived per `(auctionId, bidderId)`, and the catcher looks
up the secret of whoever the *frame* names, so a frame in Sara's name signed
with Khalid's key fails on the signature with no token involved at all.
`The_signature_rejects_an_impersonated_frame_on_its_own` now asserts exactly
that.

The subject check is still worth having — it fails earlier and more cheaply,
it carries the role check, and it means a request is tied to an authenticated
session rather than only to possession of a key — but it is defence in depth,
not the only thing standing between a bidder and someone else's deposit.

### Roles, and why there are only four

| Role | Arabic | Holds |
|---|---|---|
| `bidder` | مزايد | Their own subscription and their own bids |
| `auction-admin` | مدير النظام | Auction preparation, documents, dates, guarantee verification |
| `award-committee` | ممثل لجنة الترسية | Approval, award confirmation, disqualification, settlement |
| `operator` | — | Floor bids for an on-site auction (not yet used) |

**The separation between the last two is the point, not bureaucracy.** Slide 6
shows them as different actors. Whoever sets an auction's terms must not also
decide who won it, and a committee member able to edit an auction could change
its terms to fit the outcome they intend. Tests assert the denial in both
directions.

### The signing key belongs to the bidder alone

`GET /signing-key` is the most dangerous endpoint in the system, and **not
even an administrator can call it**. Anyone holding that key could bid as the
bidder, and the signature would be indistinguishable from the real one — which
would destroy the entire evidential value of signing.

### Verified

**179 tests** with a broker. New coverage:

| Claim | Where |
|---|---|
| No token, expired token, or wrong signature is refused | `CatcherAuthTests`, `ParticipantAuthTests` |
| An authenticated caller without the right role is refused | `CatcherAuthTests` |
| A bidder cannot submit a frame naming someone else | `CatcherAuthTests` |
| Keycloak's nested realm roles are actually read | `CatcherAuthTests` |
| Health probes stay open | `CatcherAuthTests`, `AdminAuthTests` |
| An administrator cannot confirm an award | `AdminAuthTests` |
| The committee cannot edit the auction it is awarding | `AdminAuthTests` |
| A bidder cannot read another bidder's signing key — nor can an administrator | `ParticipantAuthTests` |
| A bidder cannot drive another's subscription, or start one in their name | `ParticipantAuthTests` |
| A bidder cannot verify their own guarantee or revoke a subscription | `ParticipantAuthTests` |

### A bug a flaky test was pointing at

`TokenBucket.TryTake` computed `elapsed = now - lastSeen` without clamping. If
`now` was ever **earlier** than the bucket's last-seen time, elapsed went
negative and the refill *subtracted* tokens.

Time does move backwards in practice: an NTP correction, a VM clock
adjustment, or simply two requests whose timestamps are taken out of order. An
unclamped bucket locks a bidder out of their own auction for no reason, and
the longer the step, the longer the lockout.

It surfaced as an intermittent test failure that looked like slowness — raising
the timeout did not help, which was the clue. Elapsed is now clamped at zero
and the refill clock never rewinds.

### Still open

| Gap | Note |
|---|---|
| **Nafath is not integrated** | `POST /bidders/register` trusts the token's subject and reads `national_id` / `name` claims. The Keycloak↔Nafath mapper that populates them does not exist, and the endpoint falls back to the request body — which must be removed before production |
| **No Keycloak realm definition** | Roles, clients and mappers are described here but not provisioned. A realm export belongs in `deploy/` |
| **Tokens are not revocable mid-session** | Short lifetimes limit the window; a back-channel logout or token-revocation check is not wired |
| **`/dev/seed` still exists** | Config-gated and refuses to start in Production without a master key, but it should not ship at all |
| Service-to-service calls are unauthenticated | Nothing makes them today; when something does, it needs client credentials |
| ~~The load figures predate auth~~ | **Measured** — see below |


### What authentication costs on the bid path

Verifying an RS256 signature on every bid **missed the latency budget**:

| | no auth | RS256, per request | RS256, cached |
|---|---|---|---|
| Throughput | 13,196 req/s | 7,539 req/s | 11,308 req/s |
| p99 | 19.49 ms | **89.67 ms** | **35.37 ms** |
| Against the 50 ms budget | met | **missed** | met |

The signature cost more than the rest of the request put together — HTTP,
framing, HMAC, screening and the `acks=all` write combined. A bidder sends
many bids under one token, so nearly every verification after the first was
repeating identical work.

`ValidatedTokenCache` verifies once per token instead of once per request.
Three properties keep that safe:

- An entry lives only until the token's own `exp`, so the cache cannot extend
  a token's life.
- The key is a hash of the complete token, so altering any byte misses and the
  token is validated in full.
- Revocation is unchanged: offline validation already means a revoked session
  works until expiry. For bidding it does not depend on tokens at all —
  clearing eligibility on `auctions.participants` stops the bidder
  immediately, which is both faster and stronger than token revocation.

**The margin is now thin.** 35.37 ms against 50 ms is much less headroom than
the 19.49 ms before auth, and the residual ~16 ms is the authentication
pipeline itself. A three-broker cluster at `min.insync.replicas=2` will add
more, and that has not been measured. If it stops fitting, the next move is
terminating JWT validation at the Envoy ingress so the catcher reads a trusted
header — which moves the cost rather than removing it, but moves it somewhere
that scales independently of the bid path.

## 20. The Keycloak realm, against a real Keycloak

§19 built the token handling and tested it against a locally minted token. That
proved the handler; it could not prove the realm, because the realm did not
exist yet. `deploy/keycloak/eauction-realm.json` is it, and running it surfaced
two things the test suite could not have.

### What the realm defines

Realm `eauction`, bilingual with Arabic as the default locale, 15-minute access
tokens, `registrationAllowed: false`, brute-force protection on. Realm roles
`bidder`, `auction-admin`, `award-committee`, `operator` — realm rather than
client roles, because a bidder is a bidder across the web portal, the mobile app
and the onsite terminal alike. Three public clients, each requiring PKCE `S256`:
`bidder-web`, `bidder-mobile`, `admin-web`. Four dev users, one per role.

Nafath is deliberately absent. It needs a client id and secret issued by Elm/NIC
under contract, and a secret does not belong in git.
`deploy/keycloak/README.md` carries the identity-provider shape it takes and the
two attribute mappers it must produce.

### D-24: the extra claims ride on the clients, not on a realm client scope

The realm adds three claims: `aud: eauction` (all clients), and `national_id`
and `name_ar` (bidder clients only — staff are local users, not brokered
citizens). The obvious way to do that is a realm-level `clientScopes` array
holding two custom scopes.

That is wrong, and silently so. **A realm-level `clientScopes` array replaces
Keycloak's built-in scopes rather than adding to them.** The realm came up
holding exactly three scopes — the two custom ones and `offline_access` — with
no `basic` and no `roles`. Tokens were then issued happily, and carried:

```json
{ "aud": "eauction", "national_id": "1012345678", "name_ar": "سارة الحربي" }
```

No `sub`, and no `realm_access.roles`. Every `[Authorize]` in the platform
would have refused every caller, and `SubjectId()` would have returned null on
a token that validated perfectly. The import logged no warning. The only thing
that showed it was decoding a token.

So the three mappers are attached to the clients directly and Keycloak keeps
its own defaults. The cost is the audience mapper repeated three times; the
alternative was a realm that cannot authorize anybody.

Verified against Keycloak 26.0.7 — all four dev users, both bidder clients and
the admin client:

| user | client | `sub` | `aud` | `realm_access.roles` | `national_id` | `name_ar` |
|---|---|---|---|---|---|---|
| sara | bidder-web | ✓ | eauction | `[bidder]` | 1012345678 | سارة الحربي |
| khalid | bidder-web | ✓ | eauction | `[bidder]` | 1087654321 | خالد العتيبي |
| admin-user | admin-web | ✓ | eauction | `[auction-admin]` | — | — |
| committee-user | admin-web | ✓ | eauction | `[award-committee]` | — | — |

### A realm import is not idempotent

Against an existing realm, Keycloak logs "already exists", keeps the old realm,
and the edited file has no effect. `deploy/keycloak/run-local.sh` wipes the
dev database before importing for that reason, and validates the realm file
first: the import rejects any field Keycloak does not know — including a key
added as a comment, which is how the first version of the file failed — and
that failure takes down the whole server start, 40 seconds in.

### The registration hole, closed

`POST /bidders/register` read `national_id` from the token **with a fallback to
the request body**. Anyone holding a valid bidder token could register under
anyone's national ID. The endpoint now takes no body at all; every field comes
from the token, and a token without `national_id` is refused with 403 rather
than registering a bidder with no verified identity. Both halves are pinned by
tests that were confirmed to fail when the fallback is put back.

### D-25: one national ID is one bidder, and a collision is a 409

Closing that hole exposed a defect underneath it. `NationalId` is uniquely
indexed, and the endpoint only checked for an existing row by *subject*. A
Keycloak account deleted and re-brokered hands the same citizen a new `sub`, so
the insert hit the index and the caller got a **500** — leaking the constraint
name and leaving that person permanently unable to register.

It cannot create a second bidder: a deposit, a signing key, a subscription and
a ladder position all hang off `Bidder.Id`, so two rows for one person means one
person standing twice in the same auction. Nor should it quietly move the row to
the new subject — that is an account-takeover primitive if the claim is ever
wrong, and `Bidder.Id` is an input to the signing-key derivation, so moving it
silently re-keys their bids. It now answers `409 NationalIdAlreadyRegistered`,
and operations re-link the account deliberately. The in-flight race is caught
too: a concurrent pair both pass the pre-check, and the loser's unique violation
maps to the same 409 instead of a 500.

### Still not verified

- The Nafath identity provider itself — no contract, so no endpoints to point at.
- The `acr` step-up check at KYC, deposit and award acceptance. The realm can
  express the flow; the three endpoints still require only the `bidder` role and
  do not inspect `acr`. This is the largest remaining gap in §9.
- Keycloak behind the chart's issuer URL. The chart points at an external
  issuer by design and was not run against a clustered Keycloak.

## 21. The smoke test, and the seven faults it found

`tools/smoke/run-smoke.sh` drives one complete auction through all four services
against real Keycloak, real Kafka and real Postgres — 33 checks, no stubs, no
state seeded behind a service's back. `tools/smoke/README.md` has the walk-through
and how to run it.

It exists for the faults that live *between* services, where each side is
separately correct and the suite is green. Every one of the following was found by
running it, after 190 tests passed.

### D-26: the catcher's price floor before the first verdict

The screen applied a floor only when `auctions.current-winner` had already given it
a price, and that topic is empty until the processor judges the first bid. So for
the opening moments of every auction there was **no floor at all**: a bid of one
halala on a million-riyal auction was accepted, signed, and appended to the
hash-chained ledger that is the legal record of the sale.

The processor would never have let it win. But "keep junk out of the log" is the
screen's stated purpose, the ledger is evidence, and the opening price was sitting
in the catcher's own `AuctionDefinition` the whole time. The floor is now the
opening price until a verdict exists, with `BelowOpeningPrice` as its own rejection
reason so a bidder is told the right thing. It is still checked *after* eligibility:
the difference between `BelowOpeningPrice` and `NotEligible` would otherwise let
anyone with a token binary-search the opening price of an auction they are not in.

### D-27: topics are provisioned, never auto-created

Nothing created the control topics. Five of them must be compacted, and an
auto-created topic gets `cleanup.policy=delete` — which works perfectly until
retention expires, at which point a restarting bid-catcher replays a topic with
the eligibility rows aged out of it, comes up *warm* with empty state, and rejects
every bid in the auction. Nothing logs an error, because from Kafka's point of view
nothing is wrong.

`ControlTopics.All` in `EAuction.Core` now holds every control topic with the shape
it needs and why, and `tools/topics` creates (or `--verify`s) them from that list.
Brokers should run with `auto.create.topics.enable=false`.

`auctions.lifecycle` is explicitly **not** compacted. The processor recovers by
replaying `AuctionStarted`, `AuctionClosed` and `CandidateOffered`, all keyed by
auction id, so compaction would erase the history it recovers from and the
processor would re-announce work consumers had already seen.

### One unprovisioned topic stalled an entire service

`participants.payments` existed only as a string constant inside the participant
service's own router, so it was invisible to anything that might have created it.
The outbox relay is ordered and cannot skip a failing message without losing
ordering — correctly — so that one missing topic stalled the whole relay. Both
bidders reported `Eligible` through the participant API while the catcher never
learned either of them existed.

The name moved into `Topics`, and `ControlTopicsTests` now fails the build if any
topic declared there is missing from `ControlTopics.All`. The class of bug is worth
naming: a topic name that lives in one service's constant is a topic nothing
provisions.

### D-28: migrations are a step, not a startup side effect

No service applied its own migrations; only the test fixtures did, so a fresh
deployment started against an empty database and every write failed with
`relation "outbox" does not exist`. Doing it at startup would be worse — several
replicas would race, and a schema change would run while the previous version was
still serving. `tools/migrate` is the step a Helm `pre-install`/`pre-upgrade` hook
or a Kubernetes Job runs once, before the new pods roll. It reports pending
migrations before applying them and is a no-op when current.

### `min.insync.replicas` must follow the replication factor

The bid-topic creation hard-coded `min.insync.replicas=2` while the replication
factor was configurable. With `acks=all` that makes **every bid fail** with
`NOT_ENOUGH_REPLICAS` on any single-broker cluster — which is exactly what a small
client, a pilot, or a test environment is. It is now `max(1, replication - 1)`,
still 2 on the three-broker cluster the chart defaults to.

### The API could not round-trip its own responses

Responses serialised enums as names (`"Online"`, `"Eligible"`) while request
binding accepted only the ordinal, so a portal that read an auction and PUT it back
got a 400. Both services now register `JsonStringEnumConverter`, which still
accepts numbers — this widens the contract rather than changing it.

`AuctionResponse` also exposed `PendingCandidateBidderId` without
`PendingCandidateAmountMinorUnits`, which would have asked the award committee to
approve a sum it could not see.

### What the smoke test deliberately does not assert

That the catcher refuses a bid below the *current* price. Its price view is
milliseconds stale by design, so it may accept one; the check allows either
outcome and, when the catcher accepts, waits for the processor's `BidRejected`.
This is D-03 working as intended — the catcher is a cheap filter, the processor is
the judge — and asserting otherwise would have encoded a race as a requirement.

### Still not verified

- A multi-broker cluster. Replication, leader failover and the ISR behaviour above
  are reasoned about, not measured; `tools/topics` refuses a replication factor
  larger than the cluster, which is the only part that is checked.
- Debezium as the outbox transport. The smoke test runs the in-process polling
  relay; the Debezium connector config exists but the two were never compared.
- The cascade path end to end. `DisqualifyWinner` → next candidate is covered by
  48 unit tests in the admin service but the smoke test stops at the first award.
- The `acr` step-up check at KYC, deposit and award acceptance — still the largest
  gap in §9.
- Anything in the chart. The smoke test runs the services directly, so the Helm
  templates, the migration hook that should run `tools/migrate`, and the topic
  provisioning Job are all still unexercised.
