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

## Status

Design phase. Architecture document is in review; the bid-path vertical slice
is being built to validate the p99 ≤ 50ms @ 10k bids/sec target.
