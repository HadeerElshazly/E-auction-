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
| D-36 | **An onsite bid is signed by the clerk, and the frame names both them and the bidder** (§29) | A bidder in the hall has a paddle, not a keyboard. An onsite record that looked identical to an online one would overstate what it proves |
| D-37 | **Eligibility follows a settlement, so the deposit endpoint returns 202** (§30) | The caller used to invent the payment reference and the service believed it, so a bidder could reach the bid floor of a land auction without a riyal having moved |
| D-38 | **The payment service proves its replay with a marker it writes, rather than a quiet period** (§30) | A quiet period is a guess about whether money has already been taken; a slow broker looks exactly like an empty topic, and an empty topic means charge everybody again |
| D-39 | **The document service does not know what an auction is: restricted documents open to a signed grant, never to a role** (§31) | Whether a bidder may read the booklet depends on whether they paid; that fact lives in the participant service, and a document service that learned it would have to consume the auction domain |
| D-40 | **A notification is sent once because of a unique index, not a check** (§32) | Every topic is at-least-once and the compacted ones replay in full on start, so a restart would otherwise tell every bidder again that they won |
| D-42 | **The notification service keeps a watermark per topic** (§32) | A notice is not idempotent to a person: below the watermark a record is history to absorb silently, above it news to send — including what happened while the service was down |
| D-41 | **A bidder's inbox is private even from staff** (§32) | It is a list of which auctions they are in, when they were outbid and what they won — the whole of what D-22 keeps off the public topics, assembled in one place |
| D-43 | **A portal image is built per environment** (§33) | Vite inlines the service URLs and the Content-Security-Policy's connect-src is derived from the same table in the same build; one artifact for every environment needs the policy to move from the document to a response header |
| D-44 | **The record of a staff action does not live in the service that performed it** (§34) | An administrator who can approve an auction and also amend the record of having approved it has no audit trail, only a story in a database. The row is written through the acting service's outbox in the same transaction as the change, so neither can exist alone, and it is consumed into a hash-chained append-only table with its own credentials, its own role, and no write endpoint |
| D-45 | **The reporting service never sees the reserve price** (§35) | The reserve's secrecy is an ACL rather than a convention (D-06, D-23), and a service whose output is spreadsheets is exactly where a secret stops being one. The cost is stated: a report can say an auction was unsold and cannot say by how much it missed |
| D-16 | **Debezium for the transactional outbox only**, never raw table CDC | Raw CDC leaks internal schema into the public event contract |
| D-17 | **Event-driven domain; WSO2 MI only at integration edges** (payment, SADAD, municipality systems) | MI is an ESB, not a human-workflow engine. Workflows are state machines in the owning service |
| D-18 | **React (web) + React Native/Expo (mobile)**, shared TypeScript contracts | |

### Security

| # | Decision | Rationale |
|---|---|---|
| D-19 | **JWT via Keycloak**, verified offline against cached JWKS. No introspection call in the hot path | |
| D-20 | **HMAC-signed bids.** Each bid carries `nonce + ts + HMAC(secret, payload)`; the secret never goes on the wire | ~200ns to verify. Gives replay protection and non-repudiation |
| D-21 | **Hash-chained append-only bid ledger** + signed receipt to the bidder | Slide 5: منع التلاعب بسجلات العروض والمزايدات |
| D-22 | **Leading bidder identity is masked** in public views (`مزايد #4`) — the default, which the administrator may override per auction (§27) | Privacy, anti-collusion, PDPL. A hall auction is held in public, so masking is the right default rather than the only answer |
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
| **payment** | .NET | Deposit, brokerage, refunds, settlement. **Implemented** (§30); PayTabs / SADAD adapters are not |
| **document** | .NET + MinIO | Photos, booklets, signed award letters. **Implemented** (§31); no AV scan |
| **live-fanout** | .NET | SSE/WebSocket push of current price. **Implemented** inside query-bff (§24); still a separate process from the catcher, which is what the note below is about |
| **notification** | .NET | **Implemented** (§32) with an in-product inbox; SMS, email and push are not |
| **query-bff** | .NET | Read model for web + mobile. **Implemented** (§23) |
| **audit** | .NET + Postgres | Who did it — the hash-chained staff audit trail. **Implemented** (§34) |
| **reporting** | .NET + Postgres | التقارير. **Implemented** (§35) |
| **admin-web** | React | Admin + committee portal (RTL, AR/EN) |
| **bidder-web** | React | Public catalogue + bidding (RTL, AR/EN) |
| **bidder-mobile** | React Native (Expo) | iOS + Android. **Not built** — the only service in this table with no code |

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
| P-3 | Confirm PayTabs and SADAD merchant accounts exist | Adapter interface built either way (§30) |
| P-7 | An SMS aggregator and a registered sender name | Channel interface built either way; the in-product inbox works without one (§32) |
| P-8 | Where the object store lives on-premise, and its retention policy | Anything that speaks S3 drops in; nothing deletes a document yet (§31) |

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
| ~~No front-end charts~~ | *Superseded by §33.* Both portals have a chart entry and an image; the mobile BFF is still not built |
| No chart for `payments` at the time | *Superseded by §30:* `templates/payments.yaml` exists, pinned to one replica, and `values-jeddah.yaml` deliberately withholds the simulator flag so a production install refuses to start without a real gateway |

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
| ~~Payments are references, not integrations~~ | *Superseded by §30.* `DepositRequested` now reaches a payment service, and the caller no longer supplies the reference. PayTabs and SADAD adapters are still not built |
| ~~Documents are ids only~~ | *Superseded by §31.* The bank guarantee is now a real file in the document service, and its id resolves |
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

Realm `eauction`, bilingual with Arabic as the default locale, one-hour access
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
- ~~The `acr` step-up check at KYC, deposit and award acceptance.~~ **Done** —
  see §25, which also records what the realm's step-up flow actually does when a
  signed-in session asks for a higher level.
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
- ~~The `acr` step-up check at KYC, deposit and award acceptance.~~ **Done** —
  see §25. The walk-through now asserts the gate refuses an ordinary token.
- Anything in the chart. The smoke test runs the services directly, so the Helm
  templates, the migration hook that should run `tools/migrate`, and the topic
  provisioning Job are all still unexercised.

## 22. Provisioning in the chart and in Compose

§21 found that nothing created the topics and nothing applied the migrations. The
smoke runner did both as script steps, which proved the sequence but left the
deployment artefacts still broken: `helm upgrade --install` or
`docker compose up` would have failed the same way, with services reporting
healthy while every write failed.

Both now carry the same two steps, in the same order.

| | Helm | Compose |
|---|---|---|
| topics | `pre-install,pre-upgrade` hook, weight `-10` | `provision-topics`, depends on `kafka` healthy |
| migrations | `pre-install,pre-upgrade` hook, weight `-5` | `provision-migrate`, depends on topics completed |
| services | Deployments, after both hooks | `service_completed_successfully` on `provision-migrate` |

One image for both, `e-auction/provision`, with the command selecting the tool —
they are one deployment step, not two concerns.

### Why topics come first

The outbox relay is ordered and cannot skip a message to an unknown topic without
losing ordering. So a single missing topic stalls a service's entire relay, and
the service keeps reporting healthy: §21's `participants.payments` left both
bidders reading `Eligible` through the participant API while the catcher never
learned they existed. "Everything is green and nothing arrives" is an expensive
symptom to debug, and the ordering that prevents it is one line of hook weight.
`deploy/helm/test-chart.sh` asserts the weights stay in that order, and that
assertion was verified to fail when they are swapped.

### Why migrations are a hook and not a startup step

Several replicas starting together would race on the same schema, and a schema
change would run while the previous version is still serving. The job also reads
the same Secrets the services read, so no connection string reaches a values file
— the existing `test-chart.sh` check for that still passes.

A failed hook blocks the release. A release that proceeds past a failed migration
is worse than one that stops.

### A third gap, in Compose only

`POSTGRES_DB` creates one database. The participant service uses its own —
a national ID is personal data under PDPL and must not share a schema with the
auction catalogue that feeds public read paths — and nothing created it, so the
migration step would have failed on a fresh volume.
`deploy/compose/postgres-init/01-databases.sql` creates it during Postgres's
initial initialisation, which is the right moment: it must not re-run over a
database that already holds data. The health check now names that database rather
than the default, because `pg_isready` answers before the init scripts have run.

### Still not verified

- No Kubernetes cluster was available, so the hooks are rendered, ordered,
  asserted and packaged but never executed by Helm. What has been run is the same
  sequence they encode, by `tools/smoke/run-smoke.sh`.
- No Docker daemon was available either. `docker compose config` validates, and
  the Dockerfile follows the four that exist, but no image here was built.

## 23. The two portals, and the read path they needed

The portals were the next slice. Building them needed a read surface that did not
exist, and driving them in a real browser found eleven defects — eight in the
platform, three in the portals themselves.

### The read path

Nothing could be read. There was no list endpoint anywhere, so the admin portal
could not open on anything. `AuctionApproved` carried `PlotCount` but not the
plots, so a bidder could not see which land was in the package they were being
asked to put a deposit on. And no HTTP endpoint anywhere returned the current
price.

- **`GET /auctions`** on auction-admin: the staff list, including drafts and
  rejections, bounded server-side.
- **Plots on the public event**: deed number, area, coordinates and descriptions.
  D-23 restricts the reserve and nothing else, so this belongs on the public topic.
- **`EAuction.QueryBff`**: a new service with no database, serving the public
  catalogue and the live price from the compacted control topics.

### D-29: the public read model is its own service

Not the participant service, which holds national IDs and deposit records — giving
that a public read path would put one careless `Include` between a bidder's file
and the internet. Not the bid catcher either, for the opposite reason: the catcher
is the hot path, and a read API sharing its process competes with bidding for CPU
in the last thirty seconds of a hot lot, which is when both matter most.

The BFF consumes three topics and deliberately not a fourth: it never reads
`auctions.sealed`, so the reserve is unreachable from the public read path by ACL
rather than by care. The leading bidder's id is held privately and no response type
has a field for it, so D-22 is a property of the shape rather than a line of code
someone must remember — `MaskingTests` asserts both by reflection.

Aliases are numbered **per auction**, assigned in the order bidders first lead. A
global sequence would be worse than none: the same alias appearing in two auctions
would tell a watcher the two leaders are the same person, which is exactly the
collusion signal the masking exists to remove.

### D-30: the bid frame has a second implementation, pinned by a shared vector

The bidder portal builds and signs the 104-byte frame in the browser with Web
Crypto. That is a deliberate duplication of `BidFrame.cs`: the signing key is the
bidder's own, and a server signing on their behalf would destroy the evidential
value of a signed bid — nobody could later distinguish a bid the bidder made from
one the platform made for them.

Two implementations of a wire format drift silently, so both assert against one
committed vector, `web/shared/src/__fixtures__/bid-frame-vector.json`. The trap it
exists for: `Guid.TryWriteBytes` writes .NET's **mixed-endian** layout — first three
groups little-endian, last eight in order — which is not RFC 4122 byte order. Get it
wrong and the catcher reads a different auction and a different bidder, refuses the
bid as `UnknownAuction`, and nothing in either codebase looks wrong. The vector's
GUIDs are chosen so no group equals its own reverse, and a test asserts that
property so nobody replaces them with palindromic ones that would pass either way.

Verified both directions, and verified the vector discriminates: inverting the byte
order in the TypeScript fails three of its six tests.

### What the browser found

Eight platform defects:

1. **The PKCE callback redeemed its code twice.** React StrictMode runs every effect
   twice, and an authorization code is single-use with the verifier consumed
   alongside it. The second attempt reported "the login response did not match this
   tab" and discarded a login that had in fact succeeded. One in-flight promise per
   code now.
2. **A page refresh logged the user out.** The access token is in memory only, by
   design — a token in storage outlives the tab and is readable by any script on the
   page, and here it can award land. The cost is that a refresh loses it, which
   dumped the user on a sign-in button while Keycloak still held their session. A
   cold load now tries `prompt=none` once, marked in sessionStorage so a refusal
   cannot loop.
3. **The bid catcher and the query BFF both reported ready before reading
   anything.** "The count stopped changing" is indistinguishable from "the count has
   not started changing yet" while a Kafka consumer joins its group, so a fresh pod
   declared itself warm with empty state. The catcher would have rejected every bid
   as `UnknownAuction` behind a passing probe. Both now require a settling floor,
   default 8 seconds, as well as stability.
4. **Nafath supplies identity, not contact details.** The deposit cannot be
   confirmed without somewhere to send an award letter, and the bidder portal had no
   step to collect one — so qualification stopped dead at "the bidder's profile is
   incomplete".
5. **The reserve had to be retyped on every edit.** No read path returns it, so the
   editor cannot prefill it, and a required field meant correcting a typo in an
   auction's name obliged a clerk to retype the reserve from paper — with a wrong
   figure silently replacing the number the whole auction turns on. Null now means
   "leave it", covered by `ReserveUpdateTests`.
6. **Request and response enums disagreed** (found earlier, in §21) and the
   **pending candidate's amount was missing from the API**, which would have asked
   the committee to approve a sum it could not see.
7. **No service had CORS**, so neither portal could call anything from a browser.
   It defaults to no origins, with no wildcard option: a wildcard origin on a
   bearer-token API lets any page on the internet spend a signed-in bidder's session.
8. **A refused bid looked accepted for ever.** The engine rejects a leader raising
   their own bid (B-04) and publishes that to `bids.rejected`, which no browser
   reads. The portal now refuses the raise itself — it knows it leads, because the
   BFF tells it so without naming anyone else — and marks a bid the price has moved
   past as superseded rather than leaving it "recorded".

Three in the portals:

9. **Neither portal refreshed.** Both fetched once on mount, so an auction going
   live, a price moving, and the candidate the processor offers after a close were
   invisible until a reload. Both now poll, the bidder's catalogue backing off in a
   hidden tab and the admin editor only while the auction is in a state the
   processor owns.
10. **The editor's form reseeded on every sibling action**, so adding a plot wiped
    unsaved dates and prices — and a save that raced a refresh sent the reseeded
    defaults, producing an auction whose end preceded its start. Keyed on the
    auction id now, not the auction object.
11. **The plot form cleared asynchronously after each POST**, so a fast typist lost
    the next plot's input.

### Two silent mistakes in the test itself

Worth recording because both would have passed for ever:

- The D-23 leak check looked for `1,200,000` on a page that renders Arabic-Indic
  digits. It could not have failed whether the reserve leaked or not. Assertions
  about rendered money now go through the same formatter the portals use.
- The API smoke test's winner wait filtered on price but not auction id. The control
  topics are compacted and long-lived, so it matched a verdict from an auction held
  minutes earlier and reported the wrong winner as this run's. The auction id is now
  a required argument on `TopicWatcher.WaitForAsync`, so a caller cannot omit it.

### Still not verified

- **No push channel.** The portals poll. This remains the largest functional gap:
  §7.2's design has the processor's verdict reaching the bidder over a push channel,
  and `bids.rejected` reaches no browser at all, so a bidder learns a bid was
  refused only by inference from the price.
- **No payment gateway and no document service.** The booklet, the deposit and
  every award letter are a reference string or a `Guid` the portals generate. The
  portals say so on screen rather than implying money moved.
- ~~**The `acr` step-up is still unimplemented**~~ **Done** — see §25, including
  why the portal must resume the refused action without reloading the page.
- **One browser, one viewport.** Chromium headless at desktop size. No Safari, no
  Firefox, no phone, and the mobile app does not exist.
- **No accessibility audit.** The portals use real labels and roles, which is what
  let the walk-through address them by name, but nothing has been checked against
  WCAG or a screen reader.
- ~~**No CSP.**~~ **Done** — see §26. The walk-through can now run against the
  built bundles, which is the only way it sees the policy at all.

## 24. The push channel

§7.2 always said the processor's verdict reaches the bidder over a push channel.
It did not exist, so the portals polled, and `bids.rejected` — written by the
processor since §14 — was read by nobody at all. A bidder could not learn why a
bid failed, and the portal left a refused bid on screen marked "recorded".

### Why it is worth the complexity

At the design target of 10,000 concurrent bidders, a one-second poll through the
final minute of an auction is **~10,000 requests a second** on the query BFF — the
same order as the bid load the whole platform is built around — to carry a number
that changed perhaps fifty times. The push channel carries the same information in
fifty messages per watcher.

And it is *fresher*. A one-second poll is 500 ms stale on average and up to a
second at worst; the measured p99 of this channel at ten thousand watchers is
227 ms. Cheaper and more current, which is the whole argument.

### D-31: server-sent events, read over `fetch`

Nothing flows upwards on this channel — bids go to the catcher over their own POST
— so half a WebSocket would be unused, and SSE survives a proxy that mangles
upgrade handshakes.

Read with `fetch` and a `ReadableStream`, **not** `EventSource`. `EventSource`
cannot set request headers, so an authenticated stream would have to carry its
bearer token in the query string, where it lands in every access log, proxy log and
browser history entry on the way. A token that can award land does not belong in a
URL. The cost is reimplementing reconnect, which is about forty lines in
`web/shared/src/sse.ts`.

### D-32: a snapshot on connect, then deltas — and no resume

A connecting or reconnecting client is sent the truth as of now, then kept current.
There is no `Last-Event-ID` and no replay from an offset, for two reasons: this
service's state is a compacted read model with no arbitrary history to serve, and a
snapshot is self-healing — a delta missed while disconnected is irrelevant because
the snapshot supersedes it.

**A bid verdict breaks that symmetry**, because it is an event rather than state:
it is absent from the snapshot, and a dropped one is simply lost. So each bidder
has a bounded buffer of their last ten verdicts in an auction, replayed on connect.
Without it, a two-second blip during a bidding war loses the one message that
explains why a bid failed. It is bounded because it is memory a bidder influences
directly — a thousand doomed bids must not grow a replica's heap with them.

### Two payloads per change, not one per subscriber

A price update differs between recipients only in whether the recipient is the
leader, so it is serialised twice — once for everyone who is not, once for the one
who is. At ten thousand watchers that is two serialisations per change rather than
ten thousand. It is also the only place the leader-only fields can escape from, so
having exactly one pair of builders is worth more than the speed; `LiveViewTests`
asserts the "others" variant carries neither the leader's id nor the winning bid's.

The per-connection queue is bounded at 32 and drops its **oldest** entry when full.
A dropped price is harmless — the next supersedes it, and a client that far behind
will reconnect to a fresh snapshot — while an unbounded queue lets one client on a
stalled connection take a replica down. Verdicts survive the same policy because of
the buffer above.

### Measured

One BFF replica, 20 price changes 300 ms apart, Kafka and Postgres on the same
4-core host as the load client. Full table and caveats in
`tools/fanouttest/README.md`.

| watchers | delivered | p50 | p99 | RSS |
|---|---|---|---|---|
| 1,000 | 100% | 10.49 ms | 20.07 ms | 166 MiB |
| 5,000 | 100% | 34.13 ms | 155.32 ms | 289 MiB |
| 10,000 | 100% | 77.58 ms | 226.73 ms | 565 MiB |

Nothing was dropped at any size — 200,000 of 200,000 at ten thousand watchers,
which is the measurement that mattered since loss was the likeliest failure.
Marginal cost between 1,000 and 10,000 watchers is about **45 KiB per stream**.

### Four bugs in one async loop

The handler races a channel read against a keep-alive timer, and every version of
that race was wrong in a different way. Recording them because each is a trap that
compiles, passes a unit test, and fails only against a real client:

1. **`ValueTask` consumed twice.** `MoveNextAsync()` returns a `ValueTask`, and the
   loop called `AsTask()` on the same one each iteration. A `ValueTask` may be
   consumed exactly once.
2. **`ReadAllAsync`'s enumerator cannot be disposed outside `await foreach`.**
   Disposing it manually throws `NotSupportedException` and kills the connection.
   The designed API for this shape is `WaitToReadAsync` + `TryRead`.
3. **`PeriodicTimer` permits one outstanding waiter.** The loop abandoned the timer
   arm whenever the read arm won, so the next call threw
   `InvalidOperationException` — the stream died as soon as any message arrived.
   `Task.Delay` has no such restriction.
4. **A minimal-API handler must not both write the response and return an
   `IResult`.** Doing so leaves the framework executing a result over a started
   response, surfacing as `NotSupportedException` from the error-page middleware
   with the real stack swallowed. The handler now returns `Task` and sets its own
   status code.

A fifth belongs to clients rather than the server: **`HttpClient.Timeout` covers
reading the body even with `ResponseHeadersRead`**, so an ordinary 30-second timeout
severs every stream at exactly 30 seconds and looks like the server hanging up. Any
.NET consumer of this channel needs `Timeout.InfiniteTimeSpan` and per-request
cancellation. A browser's `fetch` has no default timeout and is unaffected.

### What the tests got wrong

Three of the assertions written for this were wrong in ways worth recording:

- **The load test's first run reported p50 of 1.1 seconds**, which was entirely the
  client: the .NET thread pool grows by about one thread every half second, so
  thousands of socket readers started at once queue behind pool growth and the
  delay gets charged to the server. Pre-sizing the pool gave 0.85 ms at ten
  watchers. A load client that does not pre-size is measuring itself.
- **A matcher threw on an event it did not want.** The predicate read
  `priceMinorUnits` as a number, and the push from an auction going live carries
  null because no bid has been judged. A predicate runs against every event on the
  stream, so it has to tolerate all of them.
- **The browser test waited for a state the fan-out is too fast to show.** It
  asserted the bid row reads "recorded, not yet judged" before the verdict — true
  of the 202, and over this channel the verdict arrives in milliseconds, so the row
  goes straight to "leading". The test failed because the feature worked.

### Still not verified

- **One replica, one machine.** The chart defaults to two with an HPA and
  connections are independent, but a multi-replica fleet was not measured, and the
  load client shared the four cores with the service under test — so the numbers
  above are a floor.
- **Connection count is not an autoscaling signal.** The HPA scales the BFF on CPU,
  which is the wrong metric for a connection-bound service. A long-lived stream
  costs little CPU while idle, so a replica can be at its connection ceiling while
  looking unloaded.
- **No ingress in the path.** `X-Accel-Buffering: no` is set for nginx and
  `no-transform` for intermediaries, but nothing was tested behind a real ingress,
  which is where SSE most often breaks.
- **The portal's fallback is untested in anger.** Polling is still there and
  `transport` reports which path is live, but no test forces the stream to fail and
  checks the portal degrades rather than going blank.

## 25. The second factor, and what Keycloak actually does

D-15 promised a Nafath two-digit step-up at KYC, at deposit payment and at award
acceptance, and never on the bid path. §20 built a realm that can express it. The
endpoints still required only a role: a token good enough to read an auction was
good enough to pay a hundred thousand riyals against it. §21, §22 and §24 each
closed with the same line — the `acr` check is the largest remaining gap in §9.
This closes it.

Four things about Keycloak had to be measured rather than read, and three of them
change the design.

### What it gates, and what it does not

| Endpoint | Why |
|---|---|
| `POST /bidders/register` | Binds a national identity to an account permanently. D-25 makes one national ID one bidder forever, so a mistake here is not correctable |
| `POST /auctions/{id}/subscriptions/{bidderId}/deposit` | Money |
| `POST /auctions/{id}/subscriptions/{bidderId}/guarantee` | Money |
| `POST /auctions/{id}/award` | A parcel of state land |

Not the bid path. A step-up takes seconds and needs a phone; a bidding war is
decided inside ten milliseconds. D-15 said so from the start and nothing found
here argues with it — the bid path has its own integrity story in D-20 and D-21.

### D-33: the service checks the token, never the request

`acr_values` is a *voluntary* claims request in OIDC. A provider that cannot
satisfy it is not obliged to refuse — it issues a token at whatever level it
reached. So a client asking for a level proves nothing, and a service that treats
the asking as the answer has a gate that opens for anyone who knows to ask.

`StepUpHandler.Evaluate` reads the `acr` claim out of the validated token and
compares it against a configured list of accepted values, by ordinal equality.
Not a substring test and not a numeric comparison: `"HIGH"`, `"high2"` and `"2"`
all fail, so a realm renumbering its map or a provider inventing a level cannot
accidentally satisfy the gate. An empty accepted list refuses everything, because
a typo in a values file should not read as "anything goes".

### D-34: a step-up expires, and the expiry is half the control

The half usually left out. A confirmation at sign-in that authorised every
payment for the rest of the session would pass a test suite that only checked
`acr` — the claim is still there, the token is still inside its fifteen-minute
life, and the person at the keyboard is no longer necessarily the person who
confirmed.

So `auth_time` must be recent as well as `acr` sufficient: five minutes by
default, plus thirty seconds of clock skew. Three details carry weight:

- **A missing or unparseable `auth_time` is refused, not waved through.** An
  undateable confirmation cannot be shown to be recent. Keycloak omits the claim
  on a direct grant — which, see below, is exactly the path that can never step
  up at all.
- **A slightly future `auth_time` is allowed, a far-future one is not.** The
  provider's clock running a few seconds ahead must not reject a confirmation
  that just succeeded; an `auth_time` an hour ahead would otherwise never age out
  and would satisfy the gate indefinitely.
- **The two failures are different answers.** `StepUpRequired` means ask for a
  level; `StepUpStale` means ask again, and the portal has to send `prompt=login`
  for it — Keycloak treats a level as reached for the life of the session, so a
  stale confirmation handed back unchanged is a redirect loop with no way out.

### The challenge is a body, not a bare 403

The framework's answer to a failed policy is an empty 403, which tells a portal
nothing. It cannot distinguish "you are the wrong person", where retrying is
pointless, from "confirm it is you and try again", where retrying is the entire
remedy. So a step-up failure answers with the reason, the `acr` values to ask for
— sent, so the portal does not hard-code a level the realm can renumber under it
— the freshness window, and an Arabic sentence for the user. Every other
authorization failure keeps the default.

### What Keycloak actually does

The realm's flow is Keycloak's own documented step-up shape: `auth-cookie` and
the forms subflow as alternatives, and inside the forms subflow two CONDITIONAL
subflows each guarded by a `conditional-level-of-authentication` condition —
level 1 holding the password form, level 2 holding the second factor. None of
what follows is a misconfiguration of it.

**`acr` is a name, not a number.** It comes from the realm's `acr.loa.map`
(`{"low": 1, "high": 2}`), so the token says `"high"`, not `2`. A service
comparing numbers reads nothing at all — and reads it as a failure, which is the
safe direction but for the wrong reason.

**A direct grant is always level 1.** `grant_type=password` ignores `acr_values`
entirely, and supplying the one-time code does not raise the level either. So no
token minted by a password grant can ever pass this gate, however it was
obtained. That is a useful property rather than a limitation — it is why the API
smoke test can assert the gate by presenting exactly those tokens and expecting
403 — but it means a stepped-up token can only be minted through a browser, which
is what `web/e2e/stepup-token.mjs` exists for.

**A user holding a TOTP credential must supply it on every grant, including a
direct one.** Seeding the dev users with an OTP credential made `otp` mandatory
on their password grants, which bought no `acr` and broke every direct grant in
the smoke test until they sent one. And a code is single-use: Keycloak remembers
the counter it last accepted and refuses a replay, so two authentications for the
same user inside one thirty-second window cannot both succeed. The smoke test
mints its stepped-up tokens moments before its password grants, so it waits for
the next window and retries — the same remedy a human with an authenticator app
has.

**Stepping up re-runs the whole ladder.** This is the one that cost the most. The
conditional Level-of-Authentication condition compares the configured level
against the level reached by the authentication *now in progress*, which starts
at zero — not against the level the SSO session already holds. So a user who is
signed in at level 1 and asks for level 2 is shown the password form again (with
the username field absent: Keycloak knows who they are) and only then the second
factor. Measured three ways, including with `acr_values=low` on the original
sign-in, in case the level had simply never been recorded; it makes no
difference.

It is left alone. The documentation implies one prompt and Keycloak gives two,
but two is the safer behaviour: a stolen session cookie cannot step itself up.
The browser test completes both.

### D-35: the stepped-up token lives in the tab, so the retry must not reload

§23 keeps the access token in React state and nowhere else — not
`localStorage`, not a cookie — because in this system a bidder's token can commit
money and a committee member's can award land. The cost is that a page load has
no token and silently acquires one from Keycloak's session with `prompt=none`.

Put that together with the fact above and there is a trap. A reload after a
confirmation throws the stepped-up token away, and the silent re-acquisition
brings back a *level 1* one, because Keycloak does not carry the level across
authentications. The gated action is then refused again, the portal redirects
again, and the user is in a loop they cannot escape by pressing harder.

So the step-up resumes in place. The portal remembers an opaque label — what the
user was doing, never the request itself — across the redirect, shows it when the
user lands back, and the user presses the button again on the page that is
already loaded. The browser walk-through navigates inside the single-page app for
the same reason, which is also what a real user does.

The pending action is deliberately **not** resumed automatically. A payment
should be the result of someone pressing a button, not of a redirect completing,
and a user who confirmed their identity for one purpose has not thereby agreed to
whatever happened to be pending.

### Three faults this found

- **The acknowledgement was read in a React state initialiser.** Reading the
  pending label cleared it, and StrictMode invokes an initialiser twice — so the
  label went to one invocation and `null` to the other, and the portal came back
  from a confirmation showing no sign that anything had happened. `takePendingAction`
  now answers the same for the whole page load. This is the second bug of exactly
  this shape in the portals (§23 had the PKCE code redeemed twice); an impure
  state initialiser is the standing hazard in this codebase.
- **The browser test concluded "no second factor was demanded" while the redirect
  was still in flight.** The challenge only arrives after the service has refused
  the action, so the portal is still on its own origin for a moment after the
  click. The assertion that the gate engaged could therefore pass with the gate
  wide open. It now waits for the identity provider before judging.
- **The test counted the challenge itself as a page fault.** The honest fix is
  not to ignore 403s from the gated endpoints but to count them: the walk-through
  now asserts that both bidders and the committee were each challenged at least
  once, and that the auction admin — who prepares auctions and commits nothing —
  never was. A gate that stopped engaging would otherwise leave a green suite.

### Still not verified

- **Nafath itself.** The OTP authenticator stands in the slot the two-digit
  confirmation will occupy; it has the same shape — an out-of-band confirmation
  of a login already in progress — but it is not the same thing, and the real one
  needs the Elm/NIC contract.
- **The gate is at the HTTP boundary only.** Nothing re-checks freshness part-way
  through a multi-step flow. The award is a single call, so this is sound today;
  the letter steps that follow it are not gated at all, on the grounds that the
  award was the decision.
- **Five minutes is a judgement, not a measurement.** Long enough to fill in a
  payment form, short enough that a walk-away does not hand someone else a
  deposit. No one has watched a real bidder do it.
- **The mobile client has never stepped up.** `bidder-mobile` is in the realm with
  the same PKCE settings; nothing has driven a step-up through it.
- **No revocation path.** Nothing listens for back-channel logout, so a session
  ended at Keycloak keeps working here until the fifteen-minute token expires.

## 26. An adversarial pass over the bid path

Everything up to here was built by asking "does this work?". This section is the
other question, asked of the path that takes money: the signing key in a
browser's heap, the master key it is derived from, the CORS posture, the
validated-token cache, the push channel, and the screen the catcher runs before
it writes to the ledger.

Six findings. Four are fixed below; two are recorded because fixing them
honestly means changing something this stage of the project should not change
unilaterally.

### Fixed: the receipt key had a default, and the default was in the repository

D-21 says a bidder gets back a signed receipt: proof that a bid of this amount
was accepted at this time and landed at this offset. The catcher read
`Catcher:ReceiptKeyHex` from configuration and, when it was unset, fell back to a
32-byte constant written in `Program.cs`.

Nothing set it. Not the chart, not Compose, not an appsettings file — so every
deployment signed every receipt with a key published in git, and anyone could
forge one. The master key beside it has had a production guard since it was
written; the receipt key had none, which is how a security control ends up
load-bearing and unconfigured at the same time.

The guard now matches the master key's: required in production, random in
development, and the chart and Compose both carry it. It is a *separate* secret
from the master key on purpose — the blast radius differs (one forges evidence,
the other forges bids) and one should be rotatable without the other.

Three tests pin it, and the shape of them matters more than the count. The first
reconstructs the signed bytes exactly as a holder of a receipt would have to,
which also pins the receipt as something checkable by someone other than the
catcher. The second asserts the retired constant does *not* verify. The third
asserts two bids get different signatures — without it, a signature that had
stopped covering the frame would pass the other two.

### Fixed: `/dev/seed` could be turned on in production

The catcher has an anonymous seeding endpoint for the load test, behind
`Catcher:EnableDevSeed`, off by default. It takes a bidder's signing secret as a
parameter — so anyone who could reach it could grant themselves the right to bid
as anyone at all.

A flag that a stray environment variable can flip is not a lock. Production now
refuses to start with it set, which is the same two-lock shape the master key
has.

### Fixed: the portals had no Content-Security-Policy

The bidder portal holds the bidder's bid-signing key in the tab's heap. That is
a deliberate choice — the alternative, a server signing on the bidder's behalf,
would mean no bid could ever be attributed to the bidder rather than to the
platform — and `useSigningKey.ts` names a strict CSP as the mitigation. §23
recorded that there was none. There is now.

`shared/vite-csp.ts` builds it and injects it at build time, with `connect-src`
derived from the same `endpoints.ts` table the runtime config reads: a
hand-maintained second list drifts the first time a service moves, and the
failure shows up in a bidder's browser rather than in a build. `script-src` is
`'self'`, which is the directive the key actually depends on; `style-src` keeps
`'unsafe-inline'` because both portals use React `style` attributes throughout,
and a concession on styles is not a concession on scripts. `base-uri 'none'` is
there because without it an injected `<base>` redirects every relative script URL
elsewhere while `script-src 'self'` still passes.

The policy had a verification problem of its own. It is injected only on a build
— the dev server's React Refresh preamble is an inline script — so the browser
walk-through, which drives `vite dev`, would never have seen it. The honest fix
was to make the walk-through able to see it: `run-portals.sh --built` serves the
built bundles instead. A CSP violation surfaces as a console error, which the
page watcher already collects and the test already asserts to be empty. That was
confirmed by building with a deliberately narrowed `connect-src` and watching the
check fail with *Refused to connect to http://localhost:5105/auctions* — a check
nobody has seen fail is not yet a check.

Two things a `<meta>` policy cannot carry: `frame-ancestors` and `report-uri` are
ignored there. Clickjacking protection still needs a response header from
whatever serves the bundles, and nothing in this repository does.

### Fixed: the most dangerous endpoint relied on a policy configured elsewhere

`GET /signing-key` hands over the credential that signs bids. It checked that the
caller is the bidder — correctly — but carried no `RequireAuthorization` of its
own, relying on the `FallbackPolicy` set in `JwtSetup`. It also still carried a
comment saying it was open, which had stopped being true.

The role policy is now on the endpoint. Nothing was reachable that should not
have been, so this is defence in depth rather than a hole closed: an endpoint
that emits bid-signing credentials should not be one edit in another file away
from being open to any authenticated caller.

### Recorded, not fixed: the nonce is not checked by anything

The frame carries a `nonce` and a `clientTimestamp` (§5), and D-20 describes the
signature as giving "replay protection and non-repudiation". Nothing on the
server reads either field. Replay protection is real, but it comes from
somewhere else: the processor keeps the `clientBidId` of every frame it has seen
and rejects a repeat as `DuplicateBidId`.

That difference matters in one place. The processor's check happens *after* the
frame is in the ledger — deliberately, because the ledger records what was
received, not only what won — so a replayed frame costs a ledger entry and a
round trip before it is refused. The per-bidder rate limit bounds how many.

Two things keep this from being worth a server-side freshness check today. A
replay needs the frame, and anyone holding the frame has broken TLS or the
client, in which case they hold the signing key and can mint fresh frames
instead. And the obvious check — reject a `clientTimestamp` far from now — is a
clock dependency on a phone, which in a live auction means locking a bidder out
of their own bidding war because their handset's clock is wrong.

So the finding is honest documentation rather than code: **the nonce is
vestigial**, D-20's replay protection is the processor's `clientBidId` set, and
the next person to read that frame layout should not assume a field is checked
because it is there.

### Recorded, not fixed: three sharp edges that are deployment decisions

- **The rate limit is per pod and runs after signature verification.** Three
  catcher replicas mean three buckets, so the effective per-bidder ceiling is
  three times the configured one; and a flood of frames with bad signatures is
  not rate-limited at all, because the bucket is only reached once the signature
  has passed. Each bad frame costs one HMAC, so this is an ingress concern rather
  than a service one — but the configured number is not the real number, and the
  values file does not say so.
- **The push channel has no per-caller connection limit.** It is anonymous by
  design, a public auction price is public, and the measurement in §24 puts
  10,000 streams at 565 MiB. One client opening ten thousand of them costs the
  same. The defence is a connection limit at the ingress, which nothing
  configures.
- **An auction-admin can rotate any bidder's key.** That is the point — a lost
  phone needs it — but doing it mid-auction silently invalidates the key the
  bidder's tab is holding, and their next bid is refused as `BadSignature` with
  nothing on screen explaining why. It takes one administrator and no second
  factor.

### What the pass found about the tests

One fault was in the test harness rather than in the system, and it would have
quietly weakened any future test of a startup-time setting. The harness
originally overrode configuration with `ConfigureAppConfiguration` — which adds a
source *after* a minimal-API entry point has already read `builder.Configuration`
in its own `Program` body. A test setting a key, a URL or a feature flag would
have seen it ignored by everything read at startup and honoured by everything
resolved later: the same test passing for the wrong reason. The harness uses
`UseSetting`, which lands before the entry point runs.

## 27. Masked or named: the administrator's choice

D-22 masked the leading bidder as `مزايد #4` everywhere, unconditionally. That is
the right default and it is not the only lawful answer. A hall auction is held in
public and everybody in the room can see who raised their paddle, so a masked
onsite auction is a fiction; and the client may have a legal basis for naming
bidders in some sales and not others. So the question is now the
administrator's, per auction.

`BidderVisibility` is `Masked` or `Named`, and three properties shape how it is
built.

**Masked is what happens when nobody decided.** A new draft is masked. An event
with no visibility field is masked. A value this system does not recognise —
`Public`, `true`, `1`, a future spelling — is masked. The only thing that names
a bidder is an administrator who chose to, and the read path parses the value
case-insensitively but will not guess at it.

**It is settable only while the auction is a draft.** A bidder puts down a
hundred thousand riyals having been told who else will see them; changing that
afterwards is not an edit, it is a different auction. The same rule the dates and
the deposit already have, and the bidder portal says which answer applies before
the sign-in button rather than after the deposit.

**A masked auction's names never leave the participant service.** This is the
part worth the plumbing. The obvious design publishes every bidder's name on
`auctions.participants` and lets the read path show it or not. That topic is
compacted and ACL-restricted, but compaction has no deadline and a name on a log
outlives the auction that justified it — the same argument D-23 makes for the
reserve price, applied to a citizen's name. So the participant service holds the
auction's visibility (from `auctions.upcoming`, alongside the deposit and the
dates it already keeps) and simply does not put a name in the event for a masked
auction. The query BFF then drops any name it is sent for a masked auction
anyway: two services would have to fail together for a name to surface.

### One slot, one decision

The live views had a `LeaderAlias` field. Naming bidders could have been a second
field beside it — and that is exactly what D-22's "masking is a property of the
shape" was written to prevent, because two fields mean two things to leave out.

So there is still one slot, renamed `LeaderLabel`, and it carries the complete
string to display: `مزايد #4` or `سارة الحربي`. `LeaderLabels` is the only thing
that fills it. The Arabic noun moved out of the portal and into the alias for the
same reason — a portal that rendered `مزايد {label}` itself would read
`مزايد سارة الحربي` the day an auction named her, and that is a decision living
in a second place.

A named auction whose name has not arrived yet falls back to the pseudonym. The
topics are followed independently, so a price can beat an eligibility; a view
that is briefly less revealing is harmless, and one that throws or renders an
empty label is not.

### What this does not change

Naming the leader is a decision about a label. The view still carries no bidder
id, no winning-bid id for anyone but its owner, and no reserve price — the
`MaskingTests` assertions that enforce D-22's other half and all of D-23 hold
under both answers, and there is a test saying so out loud, because the next
person to change one of these will be reading them together.

### A test that could not fail

Worth recording, because it is the third time this shape of mistake has appeared
here. The first version of the naming test asserted that the serialised view
*contained* `سارة الحربي`. It does not: `JsonSerializerDefaults.Web` escapes
non-ASCII, so every Arabic string reaches the wire as `\uXXXX`. Searching the
JSON for Arabic finds nothing whether the feature works or not — the same trap as
the D-23 leak check that looked for Latin digits on an Arabic-Indic page. The
label is now asserted on the view, and the serialised form is used only for what
it is good for: proving something is absent.

### Still not verified

- **Only the leader is labelled.** "Who is participating" today means "who is
  leading", because that is the only identity any view carries. A full roster of
  registered bidders is a different feature and a larger privacy question.
- **The name-publishing path is covered unit by unit, not end to end.** The
  smoke test asserts the visibility field crosses `auctions.upcoming` to the
  public catalogue; that a *name* crosses `auctions.participants` is proven by
  tests either side of the topic rather than through a real broker.
- **No audit record of who chose `Named`.** The auction records its creator, not
  the administrator who set this particular field, and publishing a citizen's name
  is the kind of decision that should name its author.

## 28. شهادة مزايدة — a certificate the bidder can actually use

D-21 promised a signed receipt, and §26 made its key a real secret. What neither
did was make the receipt *checkable* by the person holding it.

The signature is an HMAC over the recorded frame and the offset it landed at. The
frame is not in the receipt, and the key that would verify it is secret by
design — it has to be, or anybody could forge one. So a bidder holding a receipt
has a string they cannot check, nobody they can take it to, and no way to show
it means anything. That is a screenshot, not evidence.

### Read from the log, not from the request

The certificate is issued by reading the bid back out of the append-only log at
the offset the receipt names. Every field on it — amount, timestamps, channel,
the clerk who entered it — comes from that record and not from the request.

That is a stronger claim than verifying what the caller presents. "This MAC
checks out" says somebody could compute an HMAC. "The bid is in the record at
position N, for this amount, accepted at this time" is what a bidder in a dispute
actually needs, and it is what the ledger is for. `IBidLog.ReadAsync` already
seeks to an exact offset, so this costs one assign-and-read rather than a replay.

The receipt and the certificate then confirm each other: the signature on the
certificate is recomputed from the logged frame and equals the one handed over at
the gavel. One function defines what is signed, used at both ends, because two
copies that drifted would make every certificate disagree with the receipt it
exists to confirm.

### Who may read one

The bidder, for their own bids. And staff — the committee or an auction admin —
but **only by presenting the signature from the receipt**, which is what a
dispute looks like: somebody has handed them a piece of paper and asked whether
it is real.

An unconditional staff permission would have been simpler and wrong. The
certificate carries a bidder id and an amount, so a staff member who could ask
for any offset could read the ladder out of the log one position at a time —
D-22's masking undone by the endpoint next to it. Requiring the signature makes
it a checking tool rather than a browsing one.

Three further details, each of which is a way this could have gone wrong:

- **`PresentedSignatureMatched` is null, not false, when nothing was presented.**
  "You did not show me a receipt" and "the receipt you showed me is wrong" are
  different answers, and a bidder reading their own certificate should not see
  what looks like a failed verification.
- **A malformed signature is a mismatch, not a 500.** `Convert.FromHexString`
  throws on anything that is not hex, and the caller controls that string.
- **It is rate-limited per caller.** Each call opens a consumer, seeks and reads.
  That is cheap but not free, and it sits in the service with a 50ms budget on
  its other endpoint. If this ever carries real traffic it belongs somewhere else.

### Printing, because there is no document service

MinIO is still unused (§2), so the certificate is data and the portal renders it.
The browser prints it to PDF, and a print stylesheet drops the rest of the app.

That stylesheet hides everything by `visibility` rather than `display`, and that
is not a style preference. The certificate is rendered inside the application's
own tree, so `display: none` on any ancestor removes the certificate from the
layout along with everything else and prints a blank page — a mistake that is
completely invisible on screen. `visibility: hidden` is the one form that a
descendant can override. The browser walk-through switches the page to print
media and asserts that the certificate is still visible while the bid button is
not, because nothing else in the suite would have noticed either failure.

### Still not verified

- **A certificate needs an offset, and the portal only has this session's.** A
  bidder who reloads cannot reach yesterday's bids: nothing stores their receipts
  for them. Saving the certificate at the time works, and a "my bids" page backed
  by a reporting read model is the real answer.
- **It is not a sealed document.** No municipal letterhead, no QR code, no
  document id in a store — a printed page with a reference and a verification
  fingerprint. The content is what a sealed PDF would carry.
- **Nothing verifies a certificate from the printed paper alone.** The reference
  is quotable and staff can check the signature, but there is no endpoint that
  takes a reference string; it is auction id plus offset.

## 29. قاعة المزاد — the hall, where a clerk enters the bids

The client's description of an onsite auction: the administrator creates it and
bidders register for it as usual; when it starts a clerk enters the bids for the
people in the room; the clerk can extend it and can close it; and it does not go
through the processing service, because we already know who wins — but all the
data is still needed, so that a winner disqualified by لجنة الترسية can cascade to
the next bidder.

Two of those sentences pull against each other, and resolving them is most of the
design.

### The processor records, and stops judging

"It does not go through the processing service" cannot be taken literally,
because the ladder and the cascade live there: `CascadeCandidates` is what the
committee's disqualification walks down, and a hall auction needs it as much as
an online one. Reimplementing it elsewhere would mean two versions of the single
most consequential piece of logic in the system.

What the sentence is really about is authority. Online, the processor decides
when the auction ends and whether a late bid counts. In a hall there is an
auctioneer doing both, and a clock that closed an auction while the room was
still bidding would record a winner nobody in it had heard. So for an onsite
auction the processor gives up exactly two things:

- **No clock close.** `TickAsync` skips the end-time check entirely. The only
  thing that ends a hall auction is the clerk.
- **No quiet-period extension.** Anti-sniping is a remedy for not having a person
  in charge; moving an end time the auctioneer has just announced to the room is
  worse than useless.

Everything else is unchanged, which is the point: the ladder is built, the
current winner is published, the candidate is offered on close, and the cascade
walks on disqualification. The committee's الترسية workflow cannot tell which
channel sold the land.

### D-36: the clerk signs, and the frame names both of them

The bidder has a paddle, not a keyboard, so D-20's claim — *this bid was signed
by the bidder's own key and nobody else could have made it* — is not available
onsite. Pretending otherwise would be the worst outcome: an onsite record that
looked exactly like an online one would overstate what it proves.

So the clerk signs with the clerk's own derived key, and the frame carries both
parties: `bidderId` says whose bid it is, `enteredByUserId` says who typed it.
Both of those fields have been in the frame layout since §5 for precisely this.
The claim an onsite bid makes is *clerk X recorded this bid for bidder Y at time
T*, with the hall itself — the auctioneer, the attendance, the room — as the
evidence that the paddle went up. That is how it works on paper, and the
certificate (§28) says `Onsite` and names the clerk rather than reading the same
as an online one.

The clerk's key is derived exactly as a bidder's is (D-20): `auctions.participants`
carries an epoch and never a secret, the auction-admin service hands the clerk
their key on request, and the catcher derives the same one from the master it
already holds. Replacing a clerk rotates the epoch, so the outgoing terminal
stops working the moment they leave the floor.

Both the channel and the clerk id are **written by the server**. A client that
could choose its own channel could claim a hall bid was online; one that could
stamp `enteredByUserId` could put another clerk's name on its own work.

### Who may post a frame

The two channels have opposite caller rules, and each exists to stop the other's
failure mode:

| | Online | Onsite |
|---|---|---|
| Caller | the bidder, and only themselves | the auction's assigned clerk |
| Signature | the bidder's key | the clerk's key |
| If the other tries | `BidderMismatch` | `NotTheClerk` |

A bidder cannot bid directly in a hall auction — otherwise they could bid from
their phone while standing in the room and the auctioneer would be calling a
price nobody in front of them had offered. A clerk cannot enter a bid in an
online auction, which is what stops a clerk bidding for somebody who never asked
them to. And an auction the catcher has not replayed yet is refused outright
rather than assumed online, because assuming would let an unknown auction take
the path whose only check is that the caller matches the frame.

The window differs too. A hall auction has no upper bound at the catcher: the end
time is the clerk's to move and the catcher does not follow the lifecycle topic,
so the authoritative cutoff is the engine's. That is the same arrangement the
price floor has had since §6.3 — advisory at the edge, decided by the processor.

### The clerk's two verbs

`POST /auctions/{id}/extend` and `POST /auctions/{id}/close` go to auction-admin,
which owns the human workflow, and ride its outbox onto `auctions.lifecycle`
where the processor already listens for the committee's disqualifications. No new
topic and no new transport.

The auction-admin service enforces *who* may ask: this auction's clerk, not
merely somebody holding the operator role, because a clerk running the hall next
door has a valid token and no business bringing another auction's hammer down.
The engine enforces *how much*: `MaxExtensions` is a term of the auction that
bidders read before paying a deposit, so the auctioneer decides when to extend
but not how many times the published terms allow.

A clerk extension is also the one piece of engine state that is not rebuilt from
the bid log, so the processor replays it from the lifecycle topic on recovery —
one call per extension granted rather than one for the total, or a restart would
restore the right end time while handing the clerk back extensions they had
already spent.

### Two bugs this found

- **The catcher never read the channel.** `AuctionDefinition` has carried a
  `Channel` since the first commit and the control plane simply never populated it
  from `AuctionApproved`, so every auction the catcher knew about was `Online`
  whatever the administrator had chosen. Nothing noticed for five sections,
  because until the hall existed nothing asked the question. Found by the smoke
  test, where a bidder bid directly in a hall auction and was accepted.
- **The smoke test's push-channel assertion depended on a race.** It waited for a
  `BidRejected` for a bid that the catcher *may* refuse at the edge by design —
  so whenever the catcher's eventually-consistent price view had caught up, the
  processor never saw the bid, never published a verdict, and the test failed. It
  now uses a repeated `clientBidId`, which the catcher does not dedupe and the
  processor refuses before it looks at the amount: deterministic, and it exercises
  the idempotency rule as well.

### The terminal

The clerk's screen does three things and deliberately nothing else: enter a bid
for somebody in the room, move the end time, bring the hammer down. It is used
while an auctioneer is calling prices and a room is waiting, so anything else on
it would be in the way.

The clerk works from a **paddle number**, because that is what the room holds up.
The roster behind it is a new, narrow endpoint — the eligible bidders and their
names, no deposit history, no payment references, no national id — restricted to
staff. It names people regardless of the auction's masking setting (§27): that
setting governs what bidders and the public see of each other, and the clerk
typing on their behalf cannot do it from pseudonyms.

The bid is signed in the clerk's browser with the clerk's own key, the same way
the bidder portal signs with a bidder's, and the key is fetched on the first bid
rather than on load. Refusals are translated: `BadSignature` tells a developer
something and tells a clerk nothing, so each one becomes an instruction — *your
key has expired, reload the page*.

Two smaller things the browser walk-through forced into the open. A clerk needs
their own user id to be assigned to an auction, and nothing in this system can
list municipal staff — so the id is in the portal header, where they can read it
out before they have been assigned to anything. And an administrator needs
somewhere to type it, which is a panel outside the draft-only editor, because a
clerk is operational rather than a term of sale and can be put on the floor after
approval.

### What the test harness got wrong, three times

The hall walk-through hung three times on the same mistake in different places:
`isVisible()` answers for the instant it is called, and every one of those calls
was made while a card was still rendering. Each produced a hang thirty seconds to
eight minutes later, in a step that had nothing to do with the cause — a bidder
who never registered, a deposit never confirmed, a click on a button that had
already gone. All three now wait for one of the two states they are choosing
between before reading either. It is worth recording as a shape rather than three
bugs: a conditional branch on UI state is a race unless the wait comes first.

### Still not verified

- **One clerk per auction.** A long sale with a shift change means reassigning,
  which rotates the key and interrupts the terminal. A second clerk, or a
  handover, is not modelled.
- **No live price on the clerk's screen.** It shows what this clerk has entered
  this session, not the auction's current state, so a clerk who reloads loses
  their own list. The auctioneer has the price; the screen does not.
- **Nothing reconciles the room with the record.** If the clerk mistypes an
  amount there is no correction path: the ledger is append-only by design, and a
  wrong bid can only be beaten by a right one or disqualified afterwards.
- **The paddle number is positional.** It is the roster's own ordering, stable
  only for as long as the roster is — a bidder qualifying mid-auction renumbers
  everyone after them. Real paddle numbers are issued at the door and belong in
  the subscription.

---

## 30. المدفوعات — the payment service, and what it replaced

Four kinds of money move through a land auction, and until now none of them
moved at all.

| Arabic | What it is | When | Refundable |
|---|---|---|---|
| كراسة الشروط | the terms booklet fee | before reading the terms | no |
| التأمين | the deposit | before bidding | yes, unless the bidder defaults |
| مبلغ السعي | brokerage, a percentage of the price | on award | no |
| — | the deposit, at the end | on settlement | refunded, forfeited, or applied |

`EAuction.Payments` is a worker with no database and no HTTP surface. It
consumes four events and publishes one.

| It reads | It does |
|---|---|
| `BookletFeeRequested` | charges the booklet fee |
| `DepositRequested` | charges the deposit |
| `AwardConfirmed` | charges brokerage on the price actually won at |
| `DepositsReleasable` | refunds the losers, forfeits the defaulters, applies the winner's |

Everything it does lands on `payments.settlements` as one event type,
`PaymentSettled`, keyed `auction:bidder:purpose`. One type rather than four
because every consumer of that topic asks the same three questions — which
bidder, what for, did it work — and a consumer that had to switch on four names
to find out would get a new case wrong the first time one was added.

### What this replaced

`POST /auctions/{id}/subscriptions/{bidder}/deposit` used to take this:

```json
{ "paymentRef": "DEP-1" }
```

and record it. The caller invented the string; the participant service believed
it. So a bidder could walk the entire admission path — booklet, terms, deposit —
and reach the bid floor of a state land auction without a riyal having moved.
The smoke test did it twice on every run and reported it as a pass, because
there was nothing for it to check against.

Both money endpoints now take no body at all. There is nothing for a caller to
assert: the reference comes back from the gateway, on a topic the participant
service does not write.

### D-37: eligibility follows a settlement, so it is asynchronous

`POST .../deposit` returns **202**, and the subscription stays at
`AwaitingDeposit`. It becomes `Eligible` when `PaymentSettled` arrives with
`Charged`, which travels the outbox → `participants.payments` → the payment
service → the gateway → `payments.settlements` → the participant service.

A 200 would have been a lie about a payment nobody had taken yet. The cost of
telling the truth is real and is paid in three places: the bidder portal shows a
waiting state and polls every two seconds while a payment is in flight, the
smoke test waits for a stage instead of asserting one, and `qualify` in the
Playwright helpers waits for the *next* step to appear rather than for a reply.

This is also why `ChooseDeposit` no longer raises `DepositRequested`. Choosing a
method is an ordinary click, gated as one; charging a hundred thousand riyals
needs a second factor. With the request raised at the choice, the step-up on the
deposit endpoint was guarding the confirmation of a charge that had already been
sent. The booklet fee moved behind the gate for the same reason — it is money,
however little.

### D-38: the replay is proved with a marker, not guessed with a timer

This service holds no database. What it has already charged is rebuilt by
replaying its own output topic from offset 0, and that replay is the only thing
standing between a pod restart and charging every bidder in the auction a second
time.

Every other service here decides it is caught up when a topic has been quiet for
a while — `ProcessorService.DrainUntilQuietAsync` waits two seconds. For the
auction catalogue that is the right call: being a little behind costs a retry.
Here the same technique is a guess about whether money has already been taken,
and the guess is wrong in the expensive direction. A slow broker, or a slow
consumer-group assignment, looks exactly like an empty topic — and an empty
topic means charge everybody.

So the service writes a marker to the end of `payments.settlements` before it
replays, and replays until it reads that marker back. The topic has one
partition, so partition offset order is a total order over it (D-03): seeing the
marker means every settlement written before this process started has already
been applied. If the marker never comes back within two minutes the service
throws and takes the host down with it, which is the correct outcome — a payment
service that cannot tell what it has charged must not charge.

It costs one record per restart on a topic that is never compacted. That is a
fair price, and it doubles as a restart log.

The first version of this used a quiet period with a 30-second initial deadline,
which did not merely risk being wrong: it stalled every fresh deployment for
thirty seconds, because an empty topic is silent and the initial deadline was
the only timer running. The payments tests failed on it immediately.

### Four places a double charge was possible, and what stops each

| Where | What stops it |
|---|---|
| The same request delivered twice | `_settled`, rebuilt by the proved replay |
| A restart between two deliveries | the same, which is why D-38 matters |
| A crash between charging and publishing | the gateway's idempotency key, `auction:bidder:purpose` |
| Two replicas | nothing — `payments.replicas` is 1, and the chart warns if it is not |

The idempotency key is the one this service cannot handle alone. It can be sure
it did not *publish* twice, but a crash between the charge and the publish
leaves no local trace of either. Both PayTabs and SADAD support a
merchant-supplied reference for exactly this; an adapter that drops it is not
finished, and `SimulatedGateway` honours it so the stand-in keeps the promise
the real ones will have to.

### A refusal is not a settlement

`Refused` is published like everything else, but it is deliberately *not*
remembered. The bidder still owes the money, and remembering it would leave them
stuck forever behind a card that was declined once. The subscription records the
reason, clears the "requested at" timestamp so the portal stops waiting, and
lets them try again.

### Brokerage, and the award that arrives first

`AwardConfirmed` carries the price. The percentage comes from `AuctionApproved`
on `auctions.upcoming`, so `BrokerageFeePercent` was added to that event — it
belongs on the public topic anyway, since a bidder deciding what to bid is
entitled to know what the sale costs them on top. D-23 restricts what the land is
worth to the municipality, not the published terms of sale.

The two topics are followed concurrently and replayed independently, so an award
can and does arrive before the definition it needs. The first version logged a
warning and charged nothing, which is the worst shape a money bug can take:
nothing fails, the number is just smaller. It now parks the award and charges
when the percentage lands — under a lock, because without one the two handlers
interleave into a lost update where the award parks itself a moment after the
definition looked for a parked award and found none.

Rounding is `MidpointRounding.AwayFromZero` at the halala, the direction a
cashier rounds. Down, the municipality would be short on every single sale.

### Three outcomes at the end, and none of them is "refund everything"

`DepositsReleasable` is published when the award is **settled**, never at the
gavel: while the cascade can still reach a losing bidder, their deposit is held
through the compliance window of everyone above them (§8.3).

- The defaulters' deposits are **forfeited** — kept. No money moves at this
  instant, so the charge's own reference is kept rather than a refund reference
  invented for it.
- The winner's is **applied to the purchase**, not refunded. Reporting it as a
  refund would overstate what went back to bidders by the largest deposit in the
  auction.
- Everyone else is **refunded**, against the original charge. A refund is always
  against a charge, never a free-standing payment to a person — which is the
  shape of every payments bug that ends up in a newspaper.

The service knows who to release to because it took the deposits itself. Nobody
has to be asked, and no list has to be passed in.

### The gateway is a seam, and the simulator is not a placeholder

PayTabs and SADAD both need merchant accounts, credentials and a sandbox, and
P-3 records that the contract for them is still open. What can be built without
them is the shape of the conversation, the idempotency contract, and everything
upstream — which is most of the risk.

`SimulatedGateway` honours the idempotency key and can refuse: an amount whose
halalas are `13` comes back `InsufficientFunds`, chosen rather than random so a
test can ask for a decline reproducibly on any machine. A gateway that always
said yes would make the failure path unreachable, and the failure path is the one
where a bidder is left awaiting a deposit they think they paid.

Its references are random, not derived from the auction and bidder. References
end up in emails, bank statements and support tickets; one that encodes who paid
what is one that leaks it.

**Production refuses to start on it.** `Payments:AllowSimulatedGateway` must be
set explicitly, and `values-jeddah.yaml` deliberately does not set it. A
deployment that silently settled every deposit without taking a riyal would
qualify every registered bidder in the country to bid on state land, and would
look exactly like success: deposits "paid", bidders eligible, auctions running.

### What the tests cover

`EAuction.Payments.Tests` (12) is mostly about charging *once*: the same request
twice, a restart between deliveries, a release delivered twice, nothing refunded
to a bidder who never paid. `PaymentLoopTests` (2, against Postgres and the
catcher) is the one that proves the point of the whole change — a bidder becomes
eligible only once the gateway has taken the deposit, and a refused deposit
leaves them off the floor with the reason on their subscription. Removing the
payment service from that test times out at the first wait, which is how it was
checked to bite.

### Still not verified

- **No adapter exists.** Everything above is exercised against a simulator. The
  real gateways bring 3-D Secure redirects, settlement that arrives tomorrow,
  partial captures and webhooks, and none of that is modelled.
- **A settled payment that cannot be qualified is logged, not resolved.** If the
  deposit lands and the bidder turns out to have an incomplete profile, the money
  is taken and the subscription stays put. The log says so loudly; nothing
  refunds it.
- **The bank-guarantee path takes no money and is still manual.** An
  administrator verifies a PDF by eye. No bank integration.
- **Nothing reconciles against the gateway.** There is no job that compares what
  `payments.settlements` says to what the gateway's own statement says, which is
  the control a finance department will ask for first.
- **Brokerage is charged, never collected or chased.** There is no invoice, no
  due date, and no consequence for not paying it.
- **One replica, enforced only by a warning.** Sharding by auction is the way to
  scale this and it is not built.

---

## 31. المستندات — the document service, and who may read a file

Five documents exist in this platform and until now all five were a `Guid`
validated against nothing.

| Document | Uploaded by | Read by |
|---|---|---|
| كراسة الشروط, the terms booklet | an administrator | a bidder who paid for it |
| the cover image | an administrator | anyone, including an anonymous citizen |
| the bank guarantee | the bidder | that bidder, and staff who verify it |
| the award letter | the committee | the winner |
| the signed award letter | the committee | the winner |

`EAuction.Documents` stores them on anything that speaks S3 — MinIO, in compose.
It holds **no database**: everything known about a document rides on the object as
S3 user metadata, so there is no migration, pods are disposable, and the bytes and
the facts about them cannot be restored out of step with each other.

### D-39: the document service does not know what an auction is

"May this person read this file" is not a question a document service can answer.
Whether a bidder may read كراسة الشروط depends on whether they paid for it;
whether they may read an award letter depends on whether they won. Those facts
live in the participant service and auction-admin, and a document service that
learned them would have to consume the auction domain — which is the opposite of
what a document service is for.

So a document carries one of three classifications, set by whoever uploads it:

- **`Public`** — anyone, no token. The cover image.
- **`Private`** — the uploader, or staff. The bank guarantee: a bidder uploads it
  and an administrator verifies it, and nobody else has business with a document
  naming a citizen's bank.
- **`Restricted`** — nobody, by identity. No role opens it, *including an
  administrator's*. Only a grant.

A **grant** is `HMAC-SHA256` over `documentId · subject · expiry`, minted by the
service that owns the rule and verified by the document service. The participant
service has `GET .../booklet-grant`, which says yes for exactly one reason — the
booklet fee settled. Auction-admin has `GET .../award/letter-grant`, which says
yes only to the bidder named in the current award. Three services hold the same
key and nothing derived from it is ever published, the same arrangement as the
bidder master key (D-20).

The subject is inside the signature, not written alongside it, so a grant cannot
be re-pointed at another person by editing the part that says who it is for. It
lasts five minutes, which is long enough for a click and a slow connection and
nothing more.

A presigned object-store URL was the obvious alternative and is worse on two
counts: it names the bucket and key in the URL, leaking the storage layout to the
browser, and it cannot be bound to a subject — anyone the link reaches can use it.

### A refusal is a 404

Not a 403. These ids travel: they are in the auction's public event, in award
letters, in support tickets. A 403 answers "that document is real and it is not
yours", and the smoke test asserts that the refusal for a document that exists is
indistinguishable from the one for a document that does not.

### The one thing a document service gets wrong that costs the platform

Serving an uploaded file inline means an uploaded `.html` — or a `.pdf` the
browser decides is really HTML — runs as a page on a domain the platform's own
cookies belong to. That is stored cross-site scripting with an upload form for a
delivery mechanism.

Every download therefore carries `Content-Disposition: attachment` and
`X-Content-Type-Options: nosniff`, asserted by a test that uploads
`<script>alert(document.cookie)</script>` as `innocent.html` and checks both
headers come back.

The filename is the uploader's, so it is treated as hostile: it reaches a
`Content-Disposition` header, and a CR/LF in it would let the uploader append
headers of their own. `DocumentNames.Safe` strips control characters, quotes and
path separators, and keeps Arabic — a booklet is called كراسة الشروط, and renaming
it to `document.pdf` is not a security measure, it is a worse product.

### Three bugs the tests found

**The upload failed in every deployment.** `DisablePayloadSigning = true` looked
like a saving: this service already hashes the stream, so letting the SDK hash it
again is wasted work. But the AWS SDK refuses to send an unsigned payload over
plain HTTP, and MinIO in compose is plain HTTP — so every upload died in the
signer with "the request must be sent over HTTPS". Found only by running against a
real S3 endpoint; `InMemoryDocumentStore` cannot have this bug, and every other
test in the assembly uses it.

**A numeric classification read back as `Public`.** `Enum.TryParse` accepts
numbers, including values outside the enum, so a stored `"0"` parsed to
`DocumentAccess.Public` and `"99"` to `(DocumentAccess)99`. The fix is
`Enum.IsDefined` plus a digit check; stored values are always names, so a number
is never something to honour. The theory case that caught it asserts `"0"`, `"1"`,
`"-1"` and `"99"` all read as `Restricted` — unrecognised means restricted, because
failing open on an unknown classification is how a bank guarantee becomes
anonymously readable after a deployment that renamed an enum member.

**A sanitiser that behaved differently per OS.** `Path.GetFileName` asks the host
what a separator is, so on Linux a Windows browser's `C:\dir\x.pdf` survived it
whole and came out as `C:dirx.pdf`. A function whose job is to distrust input
should not depend on which image the pod runs; it now splits on both separators
explicitly.

### Upload first, attach second

Both portals upload to the document service and then record the id with
auction-admin or the participant service. The other order leaves an auction
pointing at a document that does not exist, and the auction looks complete.

### What the tests cover

55 tests: the access matrix (including that a `Restricted` document refuses its
own uploader), the grant and each thing it must refuse — another person, another
document, an expired grant, a forged signature, an edited expiry, and nine
malformed strings that must be refused rather than throw, since a grant arrives in
a query string. `S3DocumentStoreTests` runs against a real S3 endpoint and skips
without `S3_ENDPOINT`, the same arrangement as the Kafka tests.

### Still not verified

- **Never run against MinIO itself.** The S3 tests ran against a standalone S3
  server; MinIO's image and binary are both unreachable from this environment's
  egress policy. The protocol surface is the same and path-style addressing is
  exercised, but MinIO's own quirks are not.
- **The cover image is uploaded and never shown.** Nothing renders it: the query
  BFF does not carry `coverImageDocumentId` and neither catalogue has an `<img>`.
  Doing so also means widening `img-src` in the CSP, which is a deliberate change
  and not one to make speculatively.
- **No virus scanning.** A government platform that accepts uploads from the
  public will be asked for it, and ClamAV in the upload path is a different shape
  of service — the bytes cannot be stored before the verdict.
- **No deletion, no retention.** Nothing removes a document, ever. PDPL will have
  something to say about a bank guarantee kept indefinitely after an auction
  closes.
- **Uploads are buffered in memory to hash them.** `Documents:MaxUploadBytes`
  bounds it at 64MB and the chart asks for 1GiB of memory, but a streaming
  multipart hash would be better than a bound.
- **The grant is not revocable.** Five minutes is the whole of its lifecycle;
  there is no list of grants and nothing to cancel one with. For a five-minute
  token that is a reasonable trade, and it is a trade.

---

## 32. الإشعارات — telling a bidder what happened

A bidder who closes the tab currently finds out nothing. The live stream tells
whoever is watching the page, in milliseconds; everything else — the deposit
settling, the auction opening, being outbid, winning — reached nobody.

`EAuction.Notifications` follows six topics and produces nine kinds of notice.

| It reads | It tells | Whom |
|---|---|---|
| `auctions.participants` | you are eligible / your registration was revoked | that bidder |
| `payments.settlements` | a payment was refused, and why | that bidder |
| `auctions.lifecycle` | the auction opened, the auction closed | every eligible bidder |
| `auctions.lifecycle` | you won, and comply by *date* | **the winner only** |
| `auctions.lifecycle` | the award was cancelled | the disqualified bidder |
| `auctions.current-winner` | you have been outbid at *price* | whoever just stopped leading |
| `auctions.deposits` | returned / kept / applied to the price | every bidder in the auction |

The rule for what belongs here is narrow: a bidder has to act, or money moved, or
the thing they were waiting for happened. A successful charge gets no notice — the
step completing is the news and the eligibility notice says it better. A channel
people learn to ignore is worse than no channel, because the one notice that
mattered arrives in the same list.

### D-40: once is a unique index, not a check

Every topic here is at-least-once, and the compacted ones are replayed in full on
every start. Without a natural key, a restart tells every bidder in the country
again that they are eligible, that they were outbid, and that they won.

Each notification therefore carries a `Dedup` string, and
`(BidderId, AuctionId, Kind, Dedup)` is unique in the database. The consumer
**inserts and swallows the unique violation** rather than checking first: one
replica reading two topics, or two replicas, reach the same notice at the same
moment and a check would pass for both.

What `Dedup` holds is the interesting part:

- Empty for the things that happen once — eligibility, the close, the deposit's
  fate.
- The **price that beat them** for an outbid notice, so four raises produce four
  notices and a redelivery of the same record produces none.
- The **settlement's own timestamp** for a refusal, which is in the payload and so
  survives a replay. A second genuine decline has a different one and is a second
  notice — the bidder tried twice and failed twice, and being told once would hide
  the second attempt.
- The **amount** for an award, because a disqualification moves the award down the
  cascade and that is a new award, not a repeat.

This was checked by breaking it: with the outbid dedup replaced by a fresh GUID,
the test that trades the lead twice each way and then redelivers every record sees
four notices per bidder instead of two.

### D-42: a watermark per topic, because a notice is not idempotent to a person

`IEventStream` has no offset store on purpose: every consumer gets a unique group
and replays from offset 0, which is what lets the bid catcher and the bid processor
rebuild their state from nothing (D-12). Those services only need the latest value
per key, so replaying costs them nothing.

This one has to tell a person something, once. So it keeps a `topic_watermark` row
per topic — how far it has already read — and that single number does both jobs:

- **Below the watermark**, a record is history. It is absorbed into the roster and
  the auction names, and announced to nobody.
- **Above it**, a record is news — including everything that happened while the
  service was down, which is exactly what a restart should catch up on and send.

On a genuinely new database there are no watermarks, so the service first reads
every topic to a quiet point with announcements suppressed, recording where each
one ended. A quiet period is the right tool here and was the wrong one for the
payment service (D-38): being slightly wrong at this boundary costs one notice
missed or one sent, where there it would have been a second charge on a land
deposit.

This cost three attempts, each wrong in an instructive way:

1. **Dedup alone.** The unique index stops a *redelivery* becoming a second notice
   but says nothing about history: `auctions.participants` is compacted and holds
   an eligibility row for every bidder of every auction there has ever been, so a
   service deployed onto an existing cluster read all of it and sent a notice for
   each. A smoke run against a broker carrying a few earlier runs produced
   **seventeen "you are eligible" notices where four were due** — found by running
   it, not by a test.
2. **A suppressed first-run drain, then follow.** Two independent reads of the same
   topic, both from offset 0, so the follow announced everything the drain had just
   absorbed. No better than (1).
3. **The watermark.** One read per topic, and the offset decides.

### The outbid notice, and why a restart must not send one

`auctions.current-winner` is compacted, so a restart replays it. The service keeps
the previous leader per auction **in memory, deliberately empty on a cold start**:
the first record seen for an auction establishes a baseline and notifies nobody.
Without that, restarting during a live auction would tell whoever led at each
replayed step that they had been outbid — about an auction they are probably still
winning.

That is also why `notifications.replicas` is 1 and the chart warns if it is
raised. The unique index means two replicas cannot duplicate a notice; the
in-memory leader map means they would each hold half the picture and *miss* some
outbid notices instead. Sharding by auction is the fix and it is not built.

### D-41: the inbox is private even from staff

`GET /notifications` returns the caller's own and there is deliberately no
administrator override. One bidder's inbox is a list of which auctions they are
registered for, when they were outbid and what they won — which is the whole of
what D-22 keeps off the public topics, assembled in one place and indexed by
person. Staff who need to know whether a notice was sent have the service's logs.

The same reasoning runs through the notices themselves. `AwardConfirmed` tells the
winner and nobody else: the losers of a masked auction learn that it closed, which
is all they are entitled to. Naming the winner to them would undo the masking by
notification, and the smoke test asserts khalid's inbox contains `Outbid` and not
`Awarded`.

`LogChannel` does not log the body for the same reason. A notification body names
an auction, a price and sometimes an award; a log carrying all of it would be a
readable record of who is bidding on what, retained wherever logs go.

### The text is stored, not re-rendered

Titles and bodies are composed once, in Arabic, and stored. The portal renders
them verbatim — there is no second copy of the wording in the front end to drift
from this one, and a bidder who disputes what they were told is shown the row that
was stored rather than a template re-rendered by a newer build.

Amounts use `ar-SA`, which is why this project alone does not set
`InvariantGlobalization`: ١٢٠٠٠٠٠٫٠٠ ر.س is the number, and `1200000.00` is a
different document.

### What is actually delivered

The in-product inbox, and only that. SMS to Saudi numbers needs a licensed
aggregator and a registered sender name; push needs the mobile app. Neither
exists, and `INotificationChannel` is the seam they drop into (P-7).

Unlike the payment gateway, there is **no production guard** here, and the
difference is deliberate: a simulated gateway that settles everything qualifies
bidders who have not paid, while an unsent SMS leaves the inbox — a real delivered
channel — working exactly as it should.

Note what `INotificationChannel` does not take: a phone number or an email
address. Those live in the participant service with the national ID, and this
service does not hold them. A notification database that accumulated every
bidder's contact details would be a second copy of the PDPL-sensitive data the
participant service exists to confine; an adapter resolves the recipient at send
time, from the service that owns them.

### The bell polls

The auction's price has a dedicated SSE stream because a second matters there. A
notification is something you catch up on, and a second connection held open per
signed-in bidder for a whole session is a real cost for no benefit. It polls every
twenty seconds and backs off while the tab is hidden, like the catalogue.

### What the tests cover

13 tests, and the interesting ones are all about not sending something: the same
eligibility republished three times is one notice, four lead changes are four
notices and a redelivery of all four is none, a loser is never told who won, a
settled payment is not announced at all. Two have their own class and their own
database because their subject is an empty one: a first run absorbs the whole
history and announces none of it, and a restart on the same database catches up on
what it missed. Both were checked by breaking the thing they test — with the
watermark comparison forced to `true` the first-run test sees the history it is
supposed to have swallowed, and with the outbid dedup replaced by a fresh GUID the
redelivery test sees four notices per bidder instead of two.

Every test gets its own database. The watermark is global to a database while
offsets are per stream, so a shared one leaves the previous test's watermark above
the new test's offsets and classifies its records as history — a timeout waiting
for a notice that was deliberately suppressed, which says nothing about the cause.

### Still not verified

- **No SMS, no email, no push.** Everything above reaches a bidder who opens the
  portal. A bidder who does not open the portal is told nothing, which for "comply
  by Sunday or forfeit your deposit" is not good enough and is a contract
  dependency rather than a coding one.
- **No per-bidder preferences.** Everyone gets everything. A bidder in thirty
  auctions gets thirty "auction opened" notices and cannot turn them off.
- **Nothing expires.** Rows accumulate for ever, and they are personal data —
  PDPL will ask about retention, as it will for the documents.
- **The outbid notice can be late.** It is produced by a consumer following a
  topic, so in the last seconds of an auction it may arrive after the close. The
  live stream is what serves a bidder who is actually watching; this is the
  catch-up path and is not a substitute.
- **No delivery receipt.** `DispatchedAt` records that a channel accepted it, not
  that a person saw it. `ReadAt` records a click in the portal, which is the only
  evidence there is.
- **One replica, enforced by a warning.** As above.

---

## 33. Deploying the portals

Both portals were runnable and neither was deployable: a Vite dev server on a
developer's machine, or `vite preview` from `run-portals.sh`, and nothing else.
They are now a static bundle behind nginx, with a chart entry each.

| | bidder | admin |
|---|---|---|
| image | `e-auction/bidder` | `e-auction/admin` |
| replicas | 2, autoscaling to 8 | 2, no autoscaling |
| internet-facing | yes | no |
| resources | 50m / 64Mi | 50m / 64Mi |

The bidder portal faces the internet because it is the one surface a citizen
reaches before signing in — the same reason the query BFF does. The admin portal
reaches staff through whatever the municipality fronts internal applications with.

nginx listens on **8080**, not 80, so the container needs no privileged port and
runs as UID 10001 like every other image here. Its pid and scratch directories are
under `/tmp`, which is the emptyDir the chart already mounts for
`readOnlyRootFilesystem`.

### One health contract

The portal serves `/health/live` and `/health/ready`, the same two paths as every
.NET service, so `e-auction-common` needs no special case for a portal. Both
answer the same thing, because for nginx serving pre-built files they are the same
question: it is ready when it is listening. There is no warm-up, no database, and
no topic to replay.

### Caching, and the one file that must not be cached

The bundle's filename carries its content hash, so `assets/*` is
`max-age=31536000, immutable` — the browser should never ask twice. `index.html`
is `no-cache`, because it is the file that *names* the current bundle, and a
cached one points at a bundle the next deployment deleted.

### Three headers that a meta tag cannot carry

The Content-Security-Policy stays where it was: a meta tag in `index.html`, put
there at build time from the same endpoint table the bundle is built from
(§21). nginx does not repeat it, because two copies of a policy drift and the one
that drifts is the one nobody tested.

What nginx adds is the three things a meta policy cannot express at all:
`frame-ancestors 'none'` (the modern anti-framing directive, ignored in meta),
`X-Content-Type-Options: nosniff`, and `Referrer-Policy`.

### D-43: the image is built per environment, and that is a real cost

The service URLs are **build arguments**. Vite inlines `import.meta.env`
textually, and the Content-Security-Policy's `connect-src` is derived from the
same table in the same build so that it permits exactly the origins the bundle
will call and nothing else.

The consequence is that `e-auction/bidder:0.1.0` built for Jeddah is not the same
artifact as one built for a test environment, and **a tag cannot be promoted
between them**. For a chart that is explicitly one-chart-per-client, that is the
wrong shape, and it is recorded here as a cost rather than a conclusion.

Making one image serve any environment needs both halves to become runtime
concerns:

1. A `config.js` written by the container's entrypoint from environment variables
   and loaded by `index.html` before the bundle, with `config.ts` reading a global
   instead of `import.meta.env` in a production build.
2. The policy moving from the document to a response header, because a CSP built
   at image-build time cannot know origins supplied at container start — and a
   `<meta>` CSP inserted by script is ignored by design.

(2) is what makes it more than an afternoon: `buildPolicy` is the single source of
the directive list and it is TypeScript, while the thing that would have to emit
the header at container start is nginx. Splitting them means two copies of the
policy, which is the failure this design was arranged to prevent, and `csp.test.ts`
exists because that policy is a security control rather than a convenience. The
honest options are a Node entrypoint in the runtime image that calls `buildPolicy`
itself, or templating the origins into a policy whose directives are still
generated at build time. Either is a deliberate piece of work, not a tidy-up.

Until then, the build refuses rather than guesses: the Dockerfile fails if any
`VITE_*` argument is missing, because `config.ts` only throws at runtime and a
missing variable would otherwise put `http://localhost` in the `connect-src` of a
bundle served from a government domain. `csp.test.ts` asserts the same thing from
the other side.

### Not building scripts

`npm ci --ignore-scripts`, for two reasons. It is the right default in any build —
a postinstall script from any transitive dependency runs with the build's
privileges. And the `e2e` workspace depends on `@playwright/test`, whose
postinstall fetches several hundred megabytes of browsers this image will never
run; that is what made the first attempt die with npm's "Exit handler never
called". The whole workspace is installed rather than one member, because the
portals reach `@eauction/shared` through a workspace link and the lockfile covers
every member — leaving one out makes npm refuse the install rather than skip it.

### Still not verified

- **The image was never built.** This environment's network policy denies the
  Docker build a route to `registry.npmjs.org`, so `npm ci` cannot run inside it.
  What *was* verified is the half that is new: the nginx configuration, run against
  a bundle built on the host, serving both health paths, falling back to
  `index.html` for `/auctions/<id>`, and returning the three headers and both
  cache policies above. The bundle itself is built by `run-portals.sh` on every
  Playwright run. The layering in between — `npm ci`, the workspace build, the
  copy into nginx — is plain and unexercised.
- **Never deployed.** As for every other chart here: linted, rendered and
  validated, never applied to a cluster.
- **No `Cache-Control` on the Keycloak redirect.** Signing in leaves a code and
  state in the URL; nothing stops an intermediary caching that response, because
  nothing serves it — Keycloak does.
- **The admin portal has no ingress at all by default.** `expose.enabled: false`
  means a fresh install leaves staff with no way in until someone sets a host. That
  is deliberate — guessing an internal hostname would be worse — and it will
  surprise whoever installs it first.

---

## 34. سجل المراجعة — the staff audit trail

Everything above describes what the platform does. This section is about who did
it.

Every consequential act in the platform is performed by a member of staff: an
administrator sets an auction's terms and its reserve price, a committee member
approves it and confirms the award, a clerk collects the key that signs for the
hall, an administrator accepts a bank guarantee in place of money and makes a
citizen eligible to bid. Each of those was already recorded by the service that
performed it — in its own database, in rows that service can also change.

That is not an audit trail. It is a story that happens to be in a database, told
by the party with the most reason to change it.

### D-44: the record of an action does not live in the service that performed it

`staff.actions` is a one-partition event log. The services that perform audited
actions write to it **through their own transactional outbox**, which is the whole
of the guarantee:

```
BEGIN
  UPDATE auction SET status = 'Approved' WHERE id = …
  INSERT INTO outbox (aggregatetype, aggregateid, type, payload)
       VALUES ('staff-action', 'auction/…', 'StaffActionRecorded', …)
COMMIT
```

An auction cannot be approved without the record of who approved it, and a record
cannot survive a change that was rolled back. The relay — or Debezium; the contract
is the table, not the publisher — carries the row onto the topic, and
`EAuction.Audit` consumes it into a database nothing else writes to.

`StaffActionRecorded` is the one event contract in this codebase that is **shared**
rather than redeclared per service, and it lives in `EAuction.Outbox`. Everywhere
else the dependency points from the consumer to the producer's own type, because
each event belongs to a domain. This one does not: it is not an auction fact or a
participant fact, it is a fact about a person using the platform, and three
services raise the identical shape. Three copies would drift, and the audit
service's whole value is that the entries are comparable.

### The actor comes from the token, never from the body

```csharp
public static StaffActor ActorOf(HttpContext http) => new(
    http.User.SubjectId() ?? Guid.Empty,
    RolesOf(http.User),
    SourceOf(http));
```

A service that read a name out of a request body would produce an audit trail
saying whatever the audited person typed. `AuditTrailTests` asserts this directly:
`POST /auctions` takes a `createdByUserId`, and the entry records the token's
subject instead.

The **roles** are recorded as the token carried them, not looked up later. An
auditor asking "was this person entitled to approve that?" needs what was true
then. `SourceAddress` is the connection's own peer and never `X-Forwarded-For`:
unless ASP.NET's forwarded-headers middleware has been configured to accept it
from a known proxy, that header is whatever the client wrote, and an audit trail
carrying a self-declared address is worse than one carrying none, because it reads
as evidence.

### What is recorded, and what deliberately is not

The rule is narrow: every state change a member of staff makes, and the few reads
that hand over something a citizen would not expect staff to have seen.

| Service | Recorded |
|---|---|
| auction-admin | all 22 state changes — `CreateAuctionDraft`, the 21 that go through `Mutate`, from `UpdateAuctionDetails` to `SettleAuction` |
| auction-admin | `ReadClerkSigningKey` — a read, audited because of what it hands over |
| participant | `VerifyBankGuarantee`, `RevokeEligibility`, `RotateBidderKey` (by staff) |
| documents | `ReadDocument` / `ReadDocumentMetadata`, for a non-public file opened by someone who is not its owner |

The audited reads earn their place. The clerk's key lets whoever holds it sign a bid for
any eligible bidder in that auction (§29), so "who collected it, and when" is
exactly the question asked after a disputed hall auction — and auditing it means
writing on a `GET`, which is the smaller oddity. A bank guarantee names a citizen's
bank account; a signed خطاب ترسية is the winner's instrument.

What is **not** recorded is as deliberate:

- **A bidder's own steps.** Buying a booklet, accepting terms, paying a deposit,
  rotating their own key. One row per bidder per step would be hundreds of
  thousands of entries for an auction of any size, and the handful that matter
  would be unfindable among them. The same endpoint — `rotate-key` — is recorded
  or not depending on who called it.
- **Public document reads.** The cover image on the catalogue, fetched by every
  visitor.
- **An owner reading their own file.** A citizen using the product.
- **Two staff reads of citizens' details** — `GET /bidders/{id}`, which returns a
  name, a phone number and an email, and `GET /auctions/{id}/subscriptions`, the
  clerk's roster, which names every eligible bidder in an auction regardless of
  the masking setting (§29). These are genuine judgement calls rather than
  obvious exclusions, and they are the two most likely to be wrong. Both are left
  out because a portal calls them on every page render — the roster on a clerk's
  terminal, repeatedly, throughout a hall auction — and a trail dominated by
  routine lookups is a trail nobody reads. If that turns out to be the wrong call,
  the fix is a deduplicated or rate-limited entry (one row per staff member per
  auction per session, say), not simply switching them on.
- **Refused attempts.** The audit row shares the transaction a rejection rolls
  back, which is the design and its cost: somebody probing what they are allowed
  to do is invisible here. The service logs have the 403s and 409s. The
  alternative — a second transaction for the attempt — buys a trail that a
  portal's ordinary validation failures would fill.

`AuditTrailTests` and `ParticipantAuditTests` assert the exclusions as well as the
inclusions, because an exclusion nobody checks becomes an omission.

### The reserve price is the one entry where the obvious summary is wrong

An entry reading "reserve changed from 1,200,000 to 1,400,000" would put السعر
الاحتياطي — the single figure the outcome of an auction turns on, kept off every
other topic by D-23 and leaving the auction service only on the ACL-restricted
`auctions.sealed` — onto a second topic, in a row, in a different service's
database, for ever.

So `Details` is composed **at the call site**, not derived centrally, because only
the call site knows what may be said. That site records that the reserve changed
and by whom, and no figures. The smoke test sets a real reserve through the API and
then asserts the number appears nowhere on `staff.actions`; `AuditTrailTests`
asserts the same from the producing side.

### The offset is the primary key

```csharp
public long Offset { get; private set; }
```

Not a generated id, and deliberately on three counts.

Kafka delivers at least once and `IEventStream` replays every topic from the start
on each restart (D-12), so a replay has to be a no-op — with the offset as the key
it is a unique violation the consumer swallows rather than a second copy of the
same action. The topic has **one partition**, so the offset is also a total order,
which is what a hash chain needs; `ControlTopics` fixes the partition count at one
rather than leaving it to the environment, because that number is part of the
design and not a throughput choice. And a missing entry becomes visible rather
than a matter of inference: consecutive offsets are what the trail should hold, so
`verify` walks them and lists every break.

### The chain attests to what the topic said, not to what the service understood

```
hash_n = SHA256( offset ‖ len(eventType) ‖ eventType ‖ len(key) ‖ key
                        ‖ len(payload) ‖ payload ‖ hash_{n-1} )
```

The frame covers the record **verbatim** — the raw payload, stored as `text` rather
than `jsonb`, because `jsonb` normalises: it reorders keys and rewrites numbers,
and a column that quietly rewrote the evidence would make every entry fail
verification for a reason no auditor could be expected to guess.

Three properties fall out of that choice.

**Length-prefixed, not delimited.** A separator would make the frame ambiguous:
`Details` is free text from a call site, and whatever character was chosen could
appear in it. Two different records producing one hash is the one property a chain
must not have.

**Replica-independent.** Every byte hashed comes from the record; `RecordedAt` is
outside the frame. Two instances reading the same topic in the same order compute
identical hashes, so the consumer can adopt another replica's row on a unique
violation instead of having to reconcile with it. `ChainTests` asserts it with two
chains fed from clocks three hours apart.

**A malformed record never breaks the chain.** A payload the service cannot parse
still becomes an entry — `Malformed = true`, the projection empty, the evidence
intact. Dropping it would leave a hole in the offsets and break every hash after
it, so an unreadable payload must never cost the chain. A payload that parses but
is missing a required field counts as malformed too: an entry with an empty actor
would read as an action nobody performed.

The projected columns — actor, action, subject, details — are **not** hashed, and
that is not a gap left open: they are a pure function of the payload, so hashing
them would add nothing. What it does mean is that the stored projection could be
altered without breaking a link, and the projection is what the API returns and
filters on. `ProjectionMatchesPayload()` closes that, and `GET /audit/verify`
checks it on every entry alongside the hashes. This was found by a test that
expected a tampered `Details` to be caught and watched the chain verify
perfectly.

### `GET /audit/verify`

Everything else the service exposes is a convenient view of rows in a database,
which is to say something a sufficiently determined administrator could have
written. This endpoint recomputes every hash from the payload beside it, checks
every link against its predecessor, re-derives every projection, and reports the
first thing that does not follow — with `brokeAt` and a `broke` of `previous-hash`,
`hash` or `projection`.

It walks the table by offset rather than with `Skip`/`Take`, so it does not
degrade over a trail with years in it, and takes `from`/`to` to check a page
seeded from the previous entry's hash.

It reports two things it does **not** fold into `intact`:

```json
{ "storedThrough": 412, "topicEnd": 413, "missingTail": 1 }
```

A hash chain cannot see its own truncation: lop entries off the end and what
remains verifies perfectly. The broker can, because it still holds the records —
which is why `IEventStream.LatestOffsetAsync` exists (§32) and why these are
reported separately. An auditor needs to tell "truncated" from "a second behind",
and only these numbers can. Where the topic's retention has already passed, the
comparison says nothing, which makes `staff.actions` retention a compliance
decision rather than a tuning one.

The response has the same shape whether there was anything to check or not, and
that is not tidiness. The first version returned a shorter object for an empty
range, which meant the one state most worth shouting about — the table emptied
while the topic still holds every record — reported `intact: true` and left out
the two numbers that would have shown it. A test now wipes the table and asserts
`missingTail`.

### Three locks, and an honest account of each

**No write endpoint exists.** Not "a write that is forbidden": none at all.
Entries arrive from Kafka and nowhere else, which makes "the trail cannot be
edited through the API" a property of the code rather than of the authorization
configuration. The smoke test and `ApiTests` both assert that an auditor's own
token gets a 404 or 405 from every write verb.

**The table is append-only by trigger.**

```sql
CREATE TRIGGER audit_entry_append_only
    BEFORE UPDATE OR DELETE ON audit_entry
    FOR EACH ROW EXECUTE FUNCTION audit_entry_append_only();
```

This covers what the code cannot: somebody at a psql prompt with the service's own
credentials. It is **not** a claim that the trail cannot be altered — a superuser
can drop the trigger, and nothing in a database the operator controls can stop the
operator. What it buys is that tampering is no longer a single `UPDATE`: it needs a
privileged, deliberate, separately auditable act, and the chain still shows it
afterwards. The tests that prove an alteration is caught have to disable the
trigger first, which is the honest version of that attack.

**Its own database and its own role.** `eauction_audit` is separate for a
different reason from the participant service's (PDPL) and the notification
service's (D-41): separation of duty. The trail has to survive the compromise of
the services it records, so its credentials reach the audit service and the
migration Job and nothing else. Reading it needs the `auditor` realm role, which
grants nothing anywhere else and is held by nobody who operates the platform — an
auditor who could also approve an auction would be reading their own record. The
dev realm has exactly one such user, with no other role.

### The document service is the exception, deliberately

It has no outbox and no database, so it publishes its audit record straight onto
the topic. There is nothing to join: the "change" being recorded is that bytes
left the building, which has already happened by the time anything could be rolled
back. So that record is **best-effort** — if the broker is unreachable the read
still succeeds and the record is lost to the topic, with a log line as the
remaining copy.

That is the right way round. A document service that refused to hand a winner
their award letter because Kafka was unavailable would be a worse service *and* a
worse audit story, because the pressure would be to turn the auditing off.
`DocumentAuditTests` asserts the degradation with a stream that refuses everything.

### The consumer stops rather than writing out of order

Every other consumer here logs and carries on, because a notice not sent or a
payment not matched is a loss confined to that record. Here the next record's hash
is built on this one, so carrying on would write a chain with a hole in it that
verifies as tampering for ever:

```
LogCritical: Audit: failed to record offset N. The consumer is stopping rather
than writing a record out of order — the trail is hash-chained and a gap cannot
be repaired. Records are still on the topic and will be read when this is fixed.
```

A stalled audit consumer is recoverable; a corrupted trail is not. The same
reasoning sets `audit.replicas: 1` and `autoscaling.enabled: false` in the chart —
not for throughput, which is a few records per auction, but because scaling out a
hash-chained writer on load is how a trail acquires a gap.

**One thing deliberately does not stop it**: an offset that jumps forward, meaning
records existed between the last one written and this one that the service will
never see — the topic's retention passed while it was down, most likely. The first
version threw there, on the reasoning above. That was wrong: the cause of a jump is
usually permanent, so a crash loop meant the trail never recorded anything again,
and anyone who could arrange a retention lapse could switch auditing off. It now
logs critically and carries on, **on the same chain**. Nothing untrue is claimed by
that — the hashes still follow over everything that was written — and the hole in
the offsets is what shows the loss: `verify` lists it as a gap and reports the
trail as not intact. A permanent, visible, bounded gap beats a working chain that
stops growing.

It is also why the projected columns are unbounded `text` rather than capped. A
length this service chose could be exceeded by a producer it does not control — a
rejection reason is 2,000 characters in the auction service — and the result would
be a `22001` on insert, which stops the consumer. Anyone who can produce to
`staff.actions` can already put anything in `Payload`, so capping the projection
beside it buys nothing and risks the one failure mode that costs the trail.

A restart is O(1): the chain resumes from the last entry's stored hash rather than
recomputing a trail that in a few years is the record of every auction the
municipality has held. Whether the stored chain actually holds is a question for
`GET /audit/verify`, asked when an auditor asks it and not on every pod start.

### What the tests cover

**38 tests** in `tests/EAuction.Audit.Tests`, plus the producing side in the three
services that write to the topic — 25 more across `AuditTrailTests`,
`ParticipantAuditTests`, `DocumentAuditTests` and `ControlTopicsTests`.

| Claim | Where |
|---|---|
| The first entry chains onto the zero head; each carries its predecessor's hash | `ChainTests` |
| An entry's hash is recomputable from the payload stored beside it | `ChainTests` |
| Two instances with clocks three hours apart compute identical hashes | `ChainTests` |
| One byte of a payload, or the offset, changes the hash | `ChainTests` |
| Two records differing only in where the fields divide hash differently | `ChainTests` |
| An unparseable payload is recorded and still chains; so is valid JSON missing a required field | `ChainTests` |
| A resumed chain continues where the stored one stopped | `ChainTests` |
| Each record becomes an entry keyed on its offset | `ConsumerTests` |
| A restart replays the topic and writes nothing twice — the same rows, hashes included | `ConsumerTests` |
| A restart continues the chain, including records published while it was down | `ConsumerTests` |
| Two consumers on one database produce one unbroken chain | `ConsumerTests` |
| `UPDATE` and `DELETE` on the trail are refused by the database | `ConsumerTests` |
| An offset jump becomes a visible gap rather than a dead service | `ConsumerTests` |
| Admin, committee, clerk and bidder tokens are all refused | `ApiTests` |
| No write route exists for any verb, even for an auditor | `ApiTests` |
| The trail reads newest first by offset, not by timestamp | `ApiTests` |
| Filters narrow by actor, action and subject prefix | `ApiTests` |
| An altered payload is caught as `hash`; a removed entry as a gap *and* `previous-hash` | `ApiTests` |
| A rewritten `Details` or relabelled `Action` is caught as `projection` | `ApiTests` |
| A truncated tail verifies, and shows against `topicEnd` instead | `ApiTests` |
| A trail wiped entirely still reports `missingTail` against the topic | `ApiTests` |
| A bounded verify checks a page seeded from the previous hash | `ApiTests` |
| The actor is the token's subject, not the request body's | `AuditTrailTests` |
| The reserve change is recorded without the figure, anywhere in the row | `AuditTrailTests` |
| An edit that leaves the reserve alone says nothing about it | `AuditTrailTests` |
| The award workflow's trail names the committee member, not the administrator | `AuditTrailTests` |
| A rejected change leaves no entry | `AuditTrailTests`, `ParticipantAuditTests` |
| Collecting the hall's key is recorded; a refused attempt is not | `AuditTrailTests` |
| A staff action routes to `staff.actions` from both producers' routers | `AuditTrailTests`, `ParticipantAuditTests` |
| A bidder's own steps are not staff actions; the same endpoint by staff is | `ParticipantAuditTests` |
| The two staff lookups left out stay out — the exclusion is pinned, not assumed | `ParticipantAuditTests` |
| A staff read of someone else's non-public file is recorded | `DocumentAuditTests` |
| An owner's own read, a public read and a refused read are not | `DocumentAuditTests` |
| The read still succeeds when the audit topic refuses it | `DocumentAuditTests` |

Section 13 of the smoke walk-through runs the whole of it against real Keycloak,
real Kafka and real Postgres — 82 checks, all passing. The staff actions of the
preceding twelve sections arrive on the topic, four operating roles are refused,
the write verbs are absent, the approval names the committee member who gave it,
the reserve figure appears nowhere, the clerk's key collection is recorded, three
plots leave three entries, and the chain verifies over all 26 of them.

### Still not verified

- **Never run with Debezium instead of the polling relay.** The same gap as every
  other producer here (§16), and the audit trail is the one place where the
  ordering guarantee matters most: the chain is built in offset order, so a
  publisher that reordered rows within an aggregate would produce a trail whose
  hashes are fine and whose sequence is a lie. The EventRouter SMT preserves
  per-aggregate order and every staff action shares an aggregate id only with
  actions on the same subject, so the risk is bounded — but it is unexercised.
- **Retention is undecided.** A government land-sale record is probably kept for
  years or decades, which is a policy question rather than a technical one, and
  `missingTail` is only meaningful as far back as the topic reaches.
- ~~**No portal.**~~ *Superseded by §36.* The admin portal now renders سجل المراجعة
  for the `auditor` role, with the verify button on it — because a tamper-evidence
  claim nobody can check on a screen is a claim nobody believes. The compose stack
  allows the admin portal's origin on this service and only that one.
- **The append-only trigger is not tested under a non-owner role.** It fires for
  every row regardless of who is connected, but the stronger arrangement — a
  Postgres role with `INSERT` and `SELECT` and no `UPDATE`/`DELETE`, so the service
  could not alter the trail even with the trigger gone — needs role names the
  deployment has not chosen yet.
- **One partition is a ceiling nobody has measured.** A few records per auction
  makes it obviously sufficient and the hash chain makes it necessary; if the
  volume ever argues otherwise, the answer is a chain per partition and a
  verification that spans them, not more partitions.

---

## 35. التقارير — the reporting service

§11's service table has listed `reporting | .NET | التقارير` since the first draft
of this document, with no code behind it. This is that service.

The reports are the ones a municipality running a land-sale programme actually
asks for, and the list is short because each one answers a question somebody has:

| Report | The question |
|---|---|
| `GET /reports/auctions` | What did each auction do — opened, closed, sold, for how much, to whom, after how many extensions |
| `GET /reports/revenue` | What did a phase, a month or a channel raise |
| `GET /reports/participation` | How many registrations turned into deposits, and deposits into bidders |
| `GET /reports/plots` | How much of مخطط السعيد is sold, how much is left, at what rate per m² |
| `GET /reports/deposits` | Whose money are we holding, right now |
| `GET /reports/disqualifications` | Who defaulted, why, and what the cascade cost |
| `GET /reports/phases` | How big is the programme — which also settles P-1 |

Every one of them takes `?format=csv`.

### D-45: the reporting service never sees the reserve price

This is the first decision about the service and it constrains everything else.

`EAuction.Reporting` consumes four topics — `auctions.upcoming`,
`auctions.lifecycle`, `auctions.participants`, `payments.settlements` — and
deliberately **not** `auctions.sealed`. The reserve price is the one figure in this
platform whose secrecy is the whole point (D-06), and D-23 moved it onto a
restricted topic precisely so that "the reserve never leaves the processor" is an
ACL rather than a thing every developer has to remember.

A reporting service is exactly where a secret stops being one. Its output is
spreadsheets, and spreadsheets get emailed.

The cost is real and worth stating plainly: **a report can say an auction was
unsold, and cannot say by how much it missed.** A municipality wanting "we set the
reserve 12% too high across the phase" cannot get it here. If that number is
genuinely needed, the right answer is for the auction service — which owns the
figure — to publish a deliberate derived fact, not for this service to be handed
the ACL. `InboundEvents.NotConsumed_AuctionReserveSet` exists as a named constant
so the omission reads as a decision in the code too.

### A read model, and the one service here whose database is disposable

Nothing in this schema is a system of record. Drop `eauction_reporting`, restart,
and the reports come back — the consumer replays all four topics from offset 0 and
rebuilds every row. A test asserts exactly that: it builds a settled auction,
`TRUNCATE`s all four tables, and watches the same figures reappear.

Two things make that true rather than hoped for:

- **Every write is an upsert on a natural key**, so a replay rewrites rather than
  appends. The exception is the settlement ledger, which is keyed on the topic
  offset — the same device the audit trail uses (D-44), for the same reason: a
  money ledger that double-counted a replay would report twice the revenue.
- **`AuctionRecord.Reach` never lets an outcome retreat.** `auctions.lifecycle` is
  an event log replayed in full on every start, so `AuctionStarted` arrives again
  for an auction that settled months ago. Without the guard every completed auction
  would be reported as live on the first restart.

Note what this service does *not* have, which the notification service does: a
watermark. That contrast is the clearest statement of what each is for. A notice is
not idempotent to a person, so the notification service must know what it has
already sent (D-42); a payment is not idempotent to a bank, so the payment service
writes a marker (D-38). A report is pure state. Replaying everything is not merely
safe here — it is the mechanism.

It also means no report ever queries another service's database. A finance query
cannot compete with an approval workflow for the same locks, and the auction
service's load cannot make a report time out.

### Two events it needed, and did not exist

Building this turned up two gaps in the published contracts.

**`AuctionApproved` carried no `Phase`.** Almost every report groups by المخطط — a
municipality asks what a *phase* raised and how much of it is left, not what one
auction did — and the plan name was in the auction entity but on no event. It is
public information: the deed numbers and coordinates on that same event already say
where the land is.

**Nothing published "the sale completed".** `Settle()` emitted only
`DepositsReleasable`, with the winner in `AppliedToPurchaseForBidder`, so a
consumer could *infer* the settlement from a deposit event — and this service did,
until it was clear that inferring a sale from money moving is how a report comes to
disagree with the register. `AuctionSettled` now says it directly.

A third was a bug rather than a gap: `AwardConfirmed` carried
`ComplianceDeadline` but not when the committee confirmed it, so the first version
of this service dated every award from the deadline — five business days late, on
every award in every report. `ConfirmedAt` was added to the event. The committee's
confirmation is a legal act with a date, and the date belongs on the event that
announces it.

### What the reports can and cannot say

Three limits, each a consequence of the architecture rather than an oversight.

**There is no "which bidders bid" column.** Bids are binary frames on a per-auction
topic that nothing here consumes (D-12), so the participation report has the
processor's per-auction total from `AuctionClosed` and no per-bidder breakdown. The
funnel runs booklet → deposit → eligible → won, with the bidding step missing in
the middle. Closing it means either consuming every bid topic — which is the hot
path's volume arriving in a reporting service — or having the processor publish a
per-bidder count. Neither is built.

**A masked auction names nobody.** D-22's setting is enforced upstream: the
participants topic carries no name at all for a masked auction, so there is none to
print. A staff report does not override it, which is a deliberate answer to a
question that could have gone the other way — the committee does see the winner's
name, in the auction service, where it signs the award letter.

**Eligibility and rejection dates are approximate.** `auctions.participants` and
`AuctionRejected` carry no timestamp, so those dates are when this service saw the
record — which on a cold replay is "now" for everyone at once. The counts are
exact; those two dates are not. Fixing it means a timestamp on each event, as
`AwardConfirmed` now has.

The rejection date matters more than it looks, and its absence was a bug before it
was a limitation. A rejected auction has no closing date at all — bidding never
opened — so the reports date it by its schedule, and a placeholder for an auction
rejected before it was ever published had no schedule either. It sorted to
0001-01-01 and fell out of every report with a `from` filter, which defeats the one
thing recording it is for. The date chain is now
`ClosedAt ?? RejectedAt ?? ScheduledStartsAt`, and a test asserts a rejection
appears inside "the last hour".

### The price per square metre is the package's, not the plot's

An auction sells 1..N plots as one indivisible package keyed on `auctionId` (D-02).
Nobody bids on a plot, so a per-plot price does not exist. The inventory report
divides the package price by the package area and prints the same rate against
every plot in it, which is the only honest figure available — and the alternative,
apportioning by area, would invent a number that no bidder ever offered and that a
valuer would then quote back.

### Revenue separates the land's value from the cash this platform took

The payment service takes three things and only three: the booklet fee, the deposit
and brokerage (§30). **The price of the land does not pass through this platform.**

So the revenue report has two halves that are never added together:

```
sale_value          contracted value of land sold (accrual, by settlement date)
brokerage_charged   ┐
booklet_fees        ├─ collected: cash this platform actually moved
deposits_forfeited  ┘
deposits_refunded   money returned to bidders
deposits_held       the municipality's current liability
```

A report that summed `sale_value` into `collected` would claim the platform
received millions of riyals it never touched. A test asserts the two stay apart.

Three more distinctions the money reports are careful about, all of them cases
where the obvious arithmetic is wrong:

- **A disqualified award is not revenue.** When a winner defaults the record's price
  is cleared until the cascade lands, so the figure reported is the price the
  municipality was actually paid — the cascade's, which is lower. A revenue report
  that kept the defaulter's bid would report money nobody ever paid.
- **A forfeited deposit is not a refund**, and the winner's applied deposit is
  neither. No money moves at either instant — the deposit was taken when it was
  paid — and reporting them as refunds would overstate what went back to bidders by
  the largest deposit in the auction.
- **Dates follow the event, not the row.** A sale is dated by its settlement and a
  charge by when it was taken, so a sale settled on the 31st whose brokerage is
  charged on the 1st appears in two months. That is what happened.
- **`deposits_held` is reported by phase and channel, and is zero by month.** The
  figure has no date — it is what is held *now* — and dropping it into a month
  bucket invites somebody to read "March: 2,000,000 held" as money taken in March.
  `GET /reports/deposits` is the point-in-time report and the only place the figure
  is exact.

### `reporting`, a role that cannot change an auction

The third role added to this platform, by the same test as `auditor` (D-44): a role
exists only where its absence would let someone do something they must not.

Without it, the municipality's finance staff need `auction-admin` to read what a
phase raised — and that role can change an auction's reserve price. So
`Policies.Reporting` accepts `reporting`, `auction-admin` or `award-committee`:
the first exists so a finance officer needs none of the others, and the other two
are there because these are management information about work they already do.

Deliberately not `operator` — a clerk running a hall needs the room's roster, not
the programme's revenue — and deliberately not `auditor`, which keeps the promise
made in §34 that the audit role grants nothing anywhere else.

There is no write endpoint. The rows come from the topics and nowhere else, so a
wrong figure is fixed at its source rather than corrected in place, which is what
keeps a report and the register in agreement.

### The CSV is where this service meets a spreadsheet

Four details, none of them optional for this client, and the tests are about all
four.

**A UTF-8 byte-order mark.** Excel on Windows reads a BOM-less UTF-8 file in the
system code page, so مخطط السعيد arrives as mojibake. In a report whose every name
is Arabic that is the whole file ruined.

**Halalas rendered as riyals.** `1200000.00`, not `120000000`. A column of minor
units is a column every reader divides by a hundred in their head and a quarter get
wrong. The JSON API hands back the integer; this is the human-facing edge.

**A formula guard, which is the one that matters for safety.** Excel and
LibreOffice execute a field beginning `=`, `+`, `-`, `@`, a tab or a carriage
return when the file is opened — `=HYPERLINK(...)`, or a DDE call. Every text field
in these reports is staff-entered: an auction name, a rejection reason, a
disqualification reason. This is the one place the platform hands that text to a
spreadsheet.

The guard applies to text and **nothing else**, and that distinction is not
cosmetic. `-` is both a formula lead-in and a minus sign, so guarding rendered
numbers turned every negative figure in the revenue report into the text
`'-5000.00` and broke the column. A test pins it.

**A choice of separator.** The comma is the default because RFC 4180 says so and
every tool reads it — but Excel splits on the *locale's* list separator, which on a
Windows machine set to Arabic (Saudi Arabia) is a semicolon. Such a machine opens a
comma-separated file with every row in one column, which is the single most common
complaint about any CSV export and the one this client would hit first.
`?separator=semicolon` is the answer, rather than the usual `sep=,` preamble that
Excel honours and every RFC 4180 parser reads as a first row of data. The quoting
follows whichever separator was chosen, because quoting against a fixed comma
produces a file that reparses wrongly in exactly the locale the option exists for.

Not a separator: the Arabic comma ، (U+060C). It appears in the middle of rejection
reasons constantly and needs no quoting. Written down because the first version of
the test assumed otherwise.

### What the tests cover

**55 tests** in `tests/EAuction.Reporting.Tests`.

| Claim | Where |
|---|---|
| An approved auction and its plots are recorded | `ConsumerTests` |
| An auction walks approved → live → closed → awarded → settled | `ConsumerTests` |
| The award is dated by the committee's confirmation, not the compliance deadline | `ConsumerTests` |
| A replay does not move a settled auction back to live | `ConsumerTests` |
| A replay does not double-count money | `ConsumerTests` |
| A refund releases the hold; a forfeiture does not read as a refund | `ConsumerTests` |
| A disqualification clears the price until the cascade lands | `ConsumerTests` |
| An unsold auction has no price — and no reserve, by construction | `ConsumerTests` |
| An auction rejected before publication still gets a row | `ConsumerTests` |
| Every plot row carries its own id, and two auctions sharing a name and a deed number are still told apart | `ApiTests` |
| …and stays inside a dated report instead of sorting to 0001-01-01 | `ApiTests` |
| A masked auction names nobody; a named one names its bidders | `ConsumerTests` |
| A revoked eligibility is recorded without erasing that it was granted | `ConsumerTests` |
| A settlement arriving before the eligibility it paid for still counts | `ConsumerTests` |
| The whole model rebuilds from the topics after the database is emptied | `ConsumerTests` |
| No report is readable without a token; a bidder, clerk and auditor are refused | `ApiTests` |
| The three staff roles that should read them can | `ApiTests` |
| There is no write route on any report | `ApiTests` |
| The outcome report tells sold, unsold and cascaded apart | `ApiTests` |
| Brokerage is computed on the price actually awarded, not the defaulter's bid | `ApiTests` |
| Revenue keeps the land's value apart from the cash collected | `ApiTests` |
| A refund is not reported as income | `ApiTests` |
| An unknown `groupBy` is refused rather than silently defaulted | `ApiTests` |
| Deposit exposure is what is held now — including on a settled auction | `ApiTests` |
| `deposits_held` appears by phase and not by month, because it has no date | `ApiTests` |
| Every plot in a package carries the package's rate | `ApiTests` |
| Every report downloads as a CSV with a BOM, a dated filename and `nosniff` | `ApiTests` |
| The BOM, CRLF, riyal rendering, formula guard, quoting and separator | `CsvTests` |
| A negative figure stays a number | `CsvTests` |
| An Arabic comma is not a separator | `CsvTests` |

Section 14 of the smoke walk-through reads the reports back after the thirteen
sections above have run, which is the only way to check the wiring: nothing is
seeded, so whatever the walk-through actually did is what the reports say.

### Still not verified

- ~~**No portal.**~~ *Superseded by §36.* The admin portal now renders all six
  reports with a CSV download on each. P-2 — "report definitions" — is still an
  open product question, so the layouts are a first answer rather than a specified
  one; they are built to be argued with.
- **Never run with Debezium** instead of the polling relay, as with every other
  consumer here (§16).
- **The revenue report groups in memory.** It loads the settlements of every auction
  in range and buckets them in the service rather than in SQL, which is right for a
  phase of a few hundred auctions and wrong for a decade of them. The fix is a
  `GROUP BY` and a date-truncation per dialect; the honest statement today is that
  it has been run against three auctions.
- **No retention.** The same gap as §31 and §32: nothing here expires, and the
  bidder rows are personal data. A read model is the easiest of the three to age
  out — it can be rebuilt — but nothing does it.
- **P-1 is answerable now and not answered.** `GET /reports/phases` returns the
  plot count per phase, which is what settles whether the plan holds 327 or 372.
  Nobody has loaded the real plan data to ask it.

---

## 36. The two screens a stakeholder asks for

> A script for walking somebody through the whole of it is in
> [DEMO.md](DEMO.md), including the answers to give when they ask which parts are
> real.

§34 and §35 each built a service and left it reachable only with a token and
`curl`. That is enough to prove the logic and not enough for anybody to see it, and
"nobody can see it" is indistinguishable from "it does not exist" in a room where a
decision is being made.

Both are now screens in the admin portal, behind a tab strip that appears only when
the signed-in account has more than one screen to choose between.

### The roles decide the tabs, and one of them is absent on purpose

```
canReport = reporting | auction-admin | award-committee
canAudit  = auditor
```

An administrator signing in sees **المزادات** and **التقارير** and no audit tab.
That is §34 working rather than a permission someone forgot: an auditor who could
also approve an auction would be reading the record of their own actions. A
Playwright test asserts the absence, because an absence nobody checks is the kind
of thing a later "just add the tab" quietly removes.

The reverse holds too. `auditor-user` sees **سجل المراجعة** and nothing else — not
التقارير, which keeps §35's promise that the audit role grants nothing outside the
trail — and `reporting-user` is shown a one-line note on the auctions screen rather
than a list that fails to load. A read-only account that looks broken is a
read-only account somebody asks to be upgraded.

This also widened the portal's own gate. It previously admitted
`auction-admin`, `award-committee` or `operator`, so both new roles would have been
locked out of the portal entirely.

### The verify button is the point of the audit screen

Everything else on it is a table of rows in a database, which is to say something a
sufficiently determined administrator could have written. **تحقّق من السلسلة**
recomputes every hash from the payload stored beside it and reports the first link
that does not follow.

Its verdict reports three things separately rather than as one green tick, and the
separation is the same one `GET /audit/verify` makes: whether the chain holds,
whether an entry is missing from the middle, and whether the service is simply a
few seconds behind the topic. A single tick would conflate "nothing was tampered
with" with "nothing is missing", and the second is the one a hash chain cannot see
on its own.

Action names are translated for the reader — `ApproveAuction` becomes
اعتماد المزاد — while the *subject* stays raw, `auction/<id>`. The audit service
does not know what an auction is and should not (§34); it stores the string and
whoever is reading knows one when they see it.

### A download has to be fetched, not linked

`<a download>` and `window.open` cannot carry an `Authorization` header, so a CSV
behind a role policy cannot be a link. `api().download` fetches it with the bearer
token, turns the response into a blob and clicks a synthetic anchor, taking the
filename from `Content-Disposition` when the service sets one — which التقارير do,
dated, because the second thing anybody asks of a downloaded report is which day it
was run on.

The object URL is revoked on a timer rather than immediately: revoking it in the
same turn as the click races the download the click starts, and the file arrives
empty often enough to look intermittent.

The alternative — a signed one-time download URL — is a second authentication
mechanism on an endpoint that already has one, and that is the kind of thing that
ends up being the way in.

### One query string for the table and the file

The filter is built once and used for both the table and the `format=csv`
download. A download that filtered differently from the table above it is the sort
of defect nobody notices until a figure is questioned in a meeting, which is the
worst possible moment to find it.

### The defect only a browser could find

The plot report's rows had no id of their own, so the portal's table keyed on the
deed number plus the auction's name. That is wrong in a way that looks right: a
deed number is unique *within* an auction and not across them, and two phases of
one plan are commonly prepared under the same name. React found the collision on
the first run against real data and refused to render the table properly.

Nothing else would have caught it. The reporting service's own tests gave each
auction in their fixture a distinct name, and a report row's identity is not
something an API test thinks to assert — it is something a list in a UI needs.

The fix is on the API rather than in the portal: `PlotInventoryRow` now carries
`PlotId`, because a report row a client cannot identify is a row the client has to
invent a key for, and the obvious invention was the broken one. A test now builds
two auctions sharing a name and the same deed number and asserts that the old key
collides and the new one does not — which is the condition, written down rather
than remembered.

### What the browser tests cover, and what they deliberately do not

`reports.spec.ts` drives all six report tabs, all three revenue groupings, the CSV
download, and the audit screen's verify button, asserting the verdict comes back
intact. It also asserts the two absences above.

It asserts **no particular figure.** The reports are a read model of whatever the
rest of the suite did, so pinning a number here would make this spec fail whenever
another one changed — and the arithmetic is already tested in
`EAuction.Reporting.Tests` against fixtures that hold still. What this spec is for
is the class of failure only a browser finds: a CORS preflight the service never
allowed, a `connect-src` that forbids the origin, a role that opens the API and not
the tab, a download that cannot carry a token.

Adding the two endpoints to `web/shared/src/endpoints.ts` is what makes the
Content-Security-Policy permit them, because `connect-src` is derived from that
same table (D-43) — so the policy could not drift from the portal's actual calls
even if someone wanted it to.

### Still not verified

- **No screen for a phase's map.** The plot report carries latitude and longitude
  and nothing plots them. A map is the single most persuasive thing a land-sale
  programme could show, and it is also a tile-server dependency and a procurement
  question.
- **The report layouts are a first answer.** P-2 is still open, so the columns are
  what the data supports rather than what anybody asked for. They are built to be
  argued with.
- **No CSV for سجل المراجعة.** Deliberate, for now: an export of the audit trail is
  an export of who-did-what that leaves the system that protects it, and who may
  take that copy is a question for the municipality rather than a default.
