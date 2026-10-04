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
  EAuction.Outbox/        shared transactional-outbox machinery
deploy/
  helm/               portable chart (plain Kubernetes + OpenShift)
  compose/            local development stack
  debezium/           outbox connector configuration
tools/
  loadtest/           k6 harness for the bid hot path
```

## Build and test

Requires the .NET 8 SDK. The auction-admin and participant integration tests
need a PostgreSQL instance (`wal_level=logical`); override the connections
with `ADMIN_TEST_DB` and `PARTICIPANT_TEST_DB` if yours differ.

```bash
dotnet build EAuction.sln
dotnet test EAuction.sln
```

Apply the schemas with `dotnet ef database update --project src/EAuction.AuctionAdmin`
and the same for `src/EAuction.Participant`.

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
  republishes handled bids. 103 tests green overall.
- **Helm charts** — portable across plain Kubernetes and OpenShift behind one
  `platform` value, with a test script that verifies the portability holds.
  See [deploy/helm](deploy/helm/README.md).
- **Participant service + catcher wiring** — registration through to
  eligibility, and the catcher now fills its state from the control topics
  rather than from nothing.
- **Kafka verified against a real broker** — 151 tests green with one running,
  135 without. Found and fixed a silent data-loss bug no in-memory test could
  have caught.

The Kafka clients now run against a real single-node broker. Still outstanding:
the Debezium connector has never been registered against a Connect cluster, the
broker here is one node at replication factor 1 rather than the three
deployment uses, and the end-to-end p99 ≤ 50 ms @ 10k bids/sec target is not
yet measured with Kafka in the path. See
[Validation status](docs/ARCHITECTURE.md#12-validation-status) for the full
list of what is and is not verified.
