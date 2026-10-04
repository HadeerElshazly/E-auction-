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
src/                  services (.NET) and front-ends
deploy/
  helm/               umbrella chart + per-service subcharts
  compose/            local development stack
tools/
  loadtest/           k6 harness for the bid hot path
```

## Build and test

Requires the .NET 8 SDK.

```bash
dotnet build EAuction.sln
dotnet test EAuction.sln
```

Load harness and its measured results: [tools/loadtest](tools/loadtest/README.md).

## Status

Architecture document is in review. The bid-path vertical slice is implemented
and tested — 37 tests green, 31.5k req/s at p99 13.8 ms on four shared cores.

The Kafka log implementation has **not** been run against a live broker (no
Docker daemon in the build environment), so the end-to-end p99 ≤ 50 ms @ 10k
bids/sec target is not yet met. See
[Validation status](docs/ARCHITECTURE.md#12-validation-status) for the full
list of what is and is not verified.
