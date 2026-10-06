# E-Auction Platform

Electronic public auction (المزاد العلني) platform for government land plot sales.
Multi-client product; first deployment is the Al-Saeed plan (مخطط السعيد) sale
for Jeddah Municipality.

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — domain model, service map, event
  contracts, auction lifecycle, security, deployment, open items.

## Repository layout

```
docs/                 architecture and design documents
src/
  EAuction.Core/          bid frame, bid log, auction engine, ledger
  EAuction.BidCatcher/    bid ingestion HTTP endpoint
  EAuction.BidProcessor/  winner determination, auction supervision
  EAuction.AuctionAdmin/  auction data, workflows, transactional outbox
  EAuction.Participant/   registration, subscription, deposit, eligibility
  EAuction.Payments/      booklet fee, deposit, brokerage, refunds (§30)
  EAuction.Documents/     كراسة الشروط, guarantees, award letters, on S3 (§31)
  EAuction.Notifications/ what a bidder is told, and the inbox they read it in (§32)
  EAuction.Audit/         who did it — the staff audit trail, hash-chained (§34)
  EAuction.Reporting/     التقارير — a read model and the reports off it (§35)
  EAuction.QueryBff/      the public catalogue and the live price fan-out
  EAuction.Security/      roles, policies, the second factor, document grants
  EAuction.Outbox/        shared transactional-outbox machinery
web/
  bidder/             the bidder portal (React)
  admin/              the administration and committee portal (React)
  shared/             endpoints, auth, the bid frame, the CSP plugin
  e2e/                Playwright, driving both portals against the real stack
deploy/
  helm/               portable chart (plain Kubernetes + OpenShift)
  compose/            local development stack
  debezium/           outbox connector configuration
tools/
  loadtest/           k6 harness for the bid hot path
  smoke/              one auction through every service, against the real stack
  topics/             provisions the control topics with the policy each needs
  migrate/            applies the EF Core migrations, as a deployment step
```

## Build and test

Requires the .NET 8 SDK. The auction-admin and participant integration tests
need a PostgreSQL instance (`wal_level=logical`); override the connections
with `ADMIN_TEST_DB` and `PARTICIPANT_TEST_DB` if yours differ.

```bash
dotnet build EAuction.sln
dotnet test EAuction.sln
```

Apply the schemas with `tools/migrate`, which is the step a Helm hook runs:

```bash
dotnet run --project tools/migrate -- \
  --admin         "Host=localhost;Database=eauction_admin;Username=eauction;Password=eauction" \
  --participant   "Host=localhost;Database=eauction_participant;Username=eauction;Password=eauction" \
  --notifications "Host=localhost;Database=eauction_notifications;Username=eauction;Password=eauction" \
  --audit         "Host=localhost;Database=eauction_audit;Username=eauction;Password=eauction" \
  --reporting     "Host=localhost;Database=eauction_reporting;Username=eauction;Password=eauction"
```

The document service's tests need something that speaks S3; without `S3_ENDPOINT`
they skip, like the Kafka ones.

The Kafka integration tests need a broker. Without `KAFKA_BOOTSTRAP` they skip:

```bash
./tools/kafka/run-local-broker.sh start      # KRaft, no Docker needed
KAFKA_BOOTSTRAP=127.0.0.1:9092 dotnet test EAuction.sln
./tools/kafka/run-local-broker.sh stop
```

Load harness and its measured results: [tools/loadtest](tools/loadtest/README.md).

## Status

Architecture document is in review.

- **Bid path** — implemented and tested. 37 tests green, 31.5k req/s at
  p99 13.8 ms on four shared cores.
- **Auction administration + outbox** — implemented and tested. 41 tests green
  against real PostgreSQL, covering both workflows, the reserve-price cascade
  and the transactional outbox.
- **Bid processor wiring** — implemented and tested. The loop closes: an
  approved auction runs itself through bidding, a close, a candidate for the
  committee, and a cascade if that candidate fails. A restart no longer
  republishes handled bids.
- **Helm charts** — portable across plain Kubernetes and OpenShift behind one
  `platform` value, with a test script that verifies the portability holds.
  See [deploy/helm](deploy/helm/README.md).
- **Participant service + catcher wiring** — registration through to
  eligibility, and the catcher now fills its state from the control topics
  rather than from nothing.
- **Kafka verified against a real broker** — found and fixed a silent data-loss
  bug no in-memory test could have caught.
- **One auction end to end** — `tools/smoke` drives every service against real
  Keycloak, Kafka, PostgreSQL and an S3 endpoint, and `tools/smoke/run-portals.sh`
  drives both portals through a browser with Playwright.

- **Payments** — the booklet fee, the deposit, brokerage on the award, and the
  three different fates of a deposit at the end. Eligibility now follows a
  settlement rather than a string the caller invented (§30).
- **Documents** — the five documents are real files on an S3 object store, and
  who may read one is decided by the service that owns the rule rather than by a
  role (§31).
- **Notifications** — eligible, outbid, awarded, deposit returned, in an
  in-product inbox. SMS needs an aggregator contract that does not exist (§32).
- **Both portals deployable** — a static bundle behind nginx, with a chart entry
  each. The image is built per environment, which is a recorded cost (§33).
- **A staff audit trail** — every consequential act by a member of staff, written
  through the acting service's outbox in the same transaction as the change it
  describes, hash-chained into a service with its own database, its own role and no
  write endpoint (§34).
- **التقارير** — auction outcomes, revenue by phase, the participation funnel, plot
  inventory, deposit exposure and disqualifications, as a read model rebuilt from
  the topics, with a CSV export Excel reads correctly in Arabic. It never sees the
  reserve price, and §35 says what that costs.

**555 tests green** with a broker and an S3 endpoint running, 531 without — the
Kafka and object-store integration tests skip rather than fail when their
dependency is absent, so the suite runs anywhere.

The Kafka clients now run against a real single-node broker. Still outstanding:
the Debezium connector has never been registered against a Connect cluster, and
the broker here is one node at replication factor 1 rather than the three
deployment uses.

The 50 ms budget at 10k bids/sec **is** met with Kafka and authentication in the
path — 11,308 req/s at p99 35.4 ms, measured, with the caveat that validating an
RS256 signature on every request rather than once per token missed it by 40 ms.
What is still unmeasured is the longer loop: a bid through the processor to a
winner published and fanned out to the other bidders. See
[Validation status](docs/ARCHITECTURE.md#12-validation-status) for the full
list of what is and is not verified.
