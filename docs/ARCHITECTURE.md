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
| **bid-catcher** | .NET 9 minimal API, AOT | Accept bids, verify, append to Kafka. No DB, no outbound calls |
| **bid-processor** | .NET worker | Order by offset, apply auction rules, determine winner, maintain the ladder and the hash-chained ledger |
| **auction-admin** | .NET + Postgres + outbox | Auction CRUD, plots, documents, scheduling, preparation + award workflows |
| **participant** | .NET + Postgres | Registration, profile, booklet purchase, deposit, eligibility |
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
| `auctions.participants` | `auctionId:bidderId` | yes | participant | bid-catcher |
| `bids.{auctionId}` | `auctionId` | no | bid-catcher | bid-processor |
| `auctions.current-winner` | `auctionId` | yes | bid-processor | bid-catcher, live-fanout, query-bff |
| `bids.rejected` | `auctionId:bidderId` | no | bid-processor | notification, query-bff |
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

- **Kubernetes distribution unknown** → charts target plain Kubernetes but all
  containers run as **non-root with explicit UIDs**, security contexts are
  fully parameterised, and ingress is switchable between `Ingress` and
  OpenShift `Route`. This costs little now and avoids a rewrite if the client
  turns out to run OpenShift.
- **Outbound internet access unknown** → every external call (Nafath, PayTabs,
  SADAD, SMS, FCM/APNs) goes through a single configurable **egress proxy
  abstraction**. Works unchanged whether the DC has full access, a whitelist,
  or is fully air-gapped behind a DMZ relay.

### 10.2 Chart structure

```
deploy/helm/
├── e-auction/                 umbrella chart — version = release version
│   ├── Chart.yaml             version: 0.1.0   appVersion: 0.1.0
│   ├── values.yaml
│   ├── values-<client>.yaml   per-client overrides
│   └── charts/                subchart per service
└── e-auction-common/          library chart: shared templates, security contexts
```

Chart version drives the deployment version. Images are tagged with
`appVersion`. Charts are pushed as OCI artifacts alongside images.

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

| # | Question | Current assumption |
|---|---|---|
| E-1 | On-prem Kubernetes distribution | Plain k8s + OpenShift compatibility layer (§10.1) |
| E-2 | Outbound internet: full / whitelist / air-gapped | Whitelist, behind egress proxy abstraction (§10.1) |
| E-3 | Is ACR reachable from on-prem, or local Harbor? | Follows E-2 |

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
| P-3 | Payment: PayTabs (cards) + SADAD (bills) — confirm merchant accounts exist | Adapter interface built either way |

### Known risk

**Apple In-App Purchase.** The كراسة الشروط is a downloadable digital good sold
in-app. Apple's carve-out for "goods and services used outside the app" should
apply — land is physical — but a PDF sold in-app is exactly the grey zone App
Review argues over. Mitigation: make the booklet purchase web-only with the app
deep-linking out, or get a pre-submission ruling. Cheap to design around now; a
rejected build days before launch is not.
