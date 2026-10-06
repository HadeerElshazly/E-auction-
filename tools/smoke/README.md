# End-to-end smoke test

Two complete auctions — one online, one in the hall — through every service,
against real Keycloak, real Kafka, real Postgres and a real object store. No
stubs, and nothing seeded behind the services' backs: every piece of state
arrives the way it would in deployment.

```bash
tools/smoke/run-smoke.sh              # dependencies already running
tools/smoke/run-smoke.sh --with-deps  # start Keycloak too
tools/smoke/run-smoke.sh --keep-data  # do not recreate the databases
```

Needs Postgres on `:5432`, a Kafka broker on `:9092`
(`tools/kafka/run-local-broker.sh start`) and Keycloak with the `eauction` realm
(`deploy/keycloak/run-local.sh`). The script provisions the topics, applies the
migrations, starts the services on `:5101`–`:5109`, runs the walk-through and tears
everything down. Service logs land in `/tmp/eauction-smoke/`.

`SMOKE_S3` points the document service at an object store; without it documents are
held in memory, which is enough for a single-process walk-through and is why this
still runs on a machine with no MinIO.

**Run it with `--with-deps` after a few runs.** Nothing closes an onsite auction on
a clock (§29), so each run leaves another permanently-running auction on the
compacted topics, and the processor ends up driving a crowd of them until a new
auction's `AuctionStarted` arrives too late for the walk-through to see.
`--with-deps` wipes the broker; without it the failure looks like a timeout in
section 12 and has nothing to do with the code.

## What it walks through

| | |
|---|---|
| **1. Identity** | four tokens from Keycloak; a bidder's token refused by auction-admin |
| **2. Prepare** | draft → three plots as one package → booklet → prices → validation |
| **3. Approval** | separation of duties; `AuctionApproved` on `auctions.upcoming`, reserve on `auctions.sealed` and nowhere else |
| **4. Qualify** | register from token claims → booklet → terms → deposit → eligible → published to `auctions.participants` |
| **5. Keys** | each bidder's derived key is their own; nobody else can touch it |
| **6. Bidding** | impersonation refused, forged signature refused, five binary frames accepted, below-floor refused |
| **7. Verdict** | `AuctionStarted`, current winner, `AuctionClosed`, candidate offered above the reserve |
| **8. Award** | committee confirms; letter → signed → notify order enforced; the winner cannot drive it |
| **10. Payments** | brokerage at 2.5% of the price won at; the winner's deposit applied, the loser's refunded, and the winner not refunded as well (§30) |
| **11. Notifications** | each bidder's inbox; the loser is told he was outbid and not who won; one bidder cannot read another's (§32) |
| **12. The hall** | a clerk enters bids from the floor, extends, and brings the hammer down (§29) |
| **13. The audit trail** | the staff actions of the preceding twelve sections, read back by an auditor and verified: four operating roles refused, no write route, the approval named, the reserve figure absent, the chain intact (§34) |

Documents run through sections 2 and 4: a real كراسة الشروط uploaded with an Arabic
filename, the cover image readable with no token, the booklet refused even to the
administrator who uploaded it, and the bidder who paid for it reading it with a
grant that is useless in anyone else's hands (§31).

## Why it exists

It catches what unit tests structurally cannot: the faults that live *between*
services, where each side is separately correct. Everything below was found by
running this, after the suite was green.

- **The catcher applied no price floor at all** until the processor published a
  first winner — so in the opening moments of every auction, a bid of one halala
  on a million-riyal auction was accepted into the append-only ledger that is the
  legal record. The opening price was in the catcher's own state the whole time.
- **`participants.payments` existed only as a string constant** inside the
  participant service's router, so nothing provisioned it. The outbox relay is
  ordered and cannot skip a failing message without losing ordering, so that one
  missing topic stalled the whole relay and eligibility never reached the catcher.
  Both bidders showed `Eligible` in their own service while being unable to bid.
- **Nothing provisioned the control topics at all.** Five of them must be
  compacted; an auto-created topic gets `cleanup.policy=delete`, which works until
  retention expires and then silently drops the state the catcher and the processor
  rebuild from.
- **Nothing applied the database migrations** outside the test fixtures.
- **`min.insync.replicas` was hard-coded to 2** while the replication factor was
  configurable, so any single-broker install rejected every bid with
  `NOT_ENOUGH_REPLICAS`.
- **Enums were asymmetric**: responses returned `"Online"` and `"Eligible"` as
  strings while requests accepted only the ordinal, so a portal could not PUT back
  what it had just read.
- **The pending candidate's amount was not in the API**, which would have asked
  the committee to approve a sum it could not see.

## Reading the output

Each line says what was asserted, not just that something passed. A failure prints
the request, the status and the body, then the tail of all four service logs.

Two things the walk-through deliberately does *not* assert:

- **That the catcher refuses a bid below the current price.** Its price view comes
  from `auctions.current-winner` and is milliseconds stale by design, so it may
  accept one. The check allows either outcome and, when the catcher accepts, waits
  for the processor's `BidRejected` instead. The catcher is a cheap filter; the
  processor is the judge (D-03). A bid below the *opening* price is different — the
  catcher has that from `auctions.upcoming` the moment it is warm, so that one is
  asserted strictly.
- **Timing margins.** The auction opens 12s out and runs 25s, which is comfortable
  on a developer machine and tight on a loaded CI runner. The waits on Kafka events
  allow 45s each.

## Dev-only, and why the databases are recreated

The walk-through signs in with Keycloak's password grant, which the realm enables
for development only (`deploy/keycloak/README.md` lists what must change before
production). `deploy/keycloak/run-local.sh` wipes Keycloak's dev database, so every
run mints new subject ids for `sara` and `khalid` while their national IDs stay the
same — and one national ID is one bidder (D-25). A participant database carried over
from a previous run therefore answers registration with a correct `409`. The identity
store and the participant store are coupled: they are reset together, or `--keep-data`
keeps both and you accept that the walk-through stops there.

## The browser walk-through

`tools/smoke/run-portals.sh` drives the same auction through the two portals'
interfaces with Playwright — a real Keycloak login form for four actors, real
services, and a bid whose frame the browser builds and signs itself. One test with
steps rather than several sharing state: Playwright restarts its worker after a
failure, so module state does not survive one, and an auction cannot be bid on
before it is approved.

Everything found by running it is listed in `docs/ARCHITECTURE.md` §23. The ones
worth repeating here:

- **React StrictMode runs every effect twice**, so the PKCE callback redeemed its
  authorization code twice. The second attempt fails — a code is single-use and the
  verifier is consumed with it — and reported "the login response did not match this
  tab", throwing away a login that had succeeded.
- **Neither portal refreshed.** Both fetched once on mount, so an auction going
  live, a price moving, and the candidate the processor offers after a close were all
  invisible until the user reloaded.
- **Both the bid catcher and the query BFF reported ready before reading anything.**
  "The state stopped changing" is indistinguishable from "the state has not started
  loading" while a Kafka consumer is still joining its group.
- **Nafath supplies identity, not contact details**, and the bidder portal had no
  step to collect them — so the deposit was refused with "the bidder's profile is
  incomplete" and the bidder could not proceed.
- **The engine refuses a leader raising their own bid** (B-04) and publishes that to
  `bids.rejected`, which no browser reads. A refused bid sat on screen marked
  "recorded" for ever.

Two mistakes in the test itself are worth naming, because both were silent:

- The D-23 leak check looked for `1,200,000` on a page that renders Arabic-Indic
  digits, so it could never have failed whether the reserve leaked or not. Assertions
  about rendered money now go through the same formatter the portals use.
- `datetime-local` has minute precision, so a start and end a few seconds apart
  arrive identical and validation rejects them. That sets the floor on how short the
  test's auction can be, and the test takes about three minutes because of it.
