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

Apply the auction-admin schema with
`dotnet ef database update --project src/EAuction.AuctionAdmin`.

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
  rather than from nothing. 135 tests green.

The Kafka implementations have **not** been run against a live broker, and the
Debezium connector has not been registered against a live Connect cluster (no
Docker daemon in the build environment). The end-to-end p99 ≤ 50 ms @ 10k
bids/sec target is therefore not yet met. See
[Validation status](docs/ARCHITECTURE.md#12-validation-status) for the full
list of what is and is not verified.
