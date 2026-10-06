# Debezium outbox connector

Streams `public.outbox` from the auction-admin database to Kafka.

## What it replaces

The service also ships `OutboxRelay`, a polling publisher. The two are
interchangeable because the contract is the **table**, not the publisher —
which is the point of shaping the outbox to Debezium's EventRouter defaults.

Run one or the other, never both: both are at-least-once, and running both
doubles every event for no benefit.

| | Polling relay | Debezium |
|---|---|---|
| Latency | up to ~500 ms | milliseconds |
| Load on Postgres | a query every 500 ms | WAL tail, negligible |
| Moving parts | none | Kafka Connect + a replication slot |
| Use for | local dev, small deployments | production |

## Topic mapping

`aggregatetype` selects the destination, so each destination has its own
aggregate type rather than one `auction` bucket consumers would filter by
header:

| aggregatetype | topic | carries |
|---|---|---|
| `auction-upcoming` | `auctions.upcoming` | public auction definition — **no reserve price** |
| `auction-sealed` | `auctions.sealed` | reserve price only, restricted ACL |
| `auction-lifecycle` | `auctions.lifecycle` | rejection, award, disqualification, unsold |
| `auction-deposits` | `auctions.deposits` | deposit release and forfeit |
| `auction-clerk` | `auctions.participants` | which clerk signs for a hall auction (§29) |
| `staff-action` | `staff.actions` | who did it — the audit trail (§34) |

`staff-action` is the one aggregate type that is **not** an auction fact, and the
one that also appears in the participant service's router: it is a fact about a
person using the platform, and both services raise the identical shape. Its
destination has one partition, because the audit service hash-chains the entries
and a chain needs a total order. The EventRouter SMT keys on `aggregateid`, which
for a staff action is the subject acted on, so per-aggregate order is preserved —
but see §34's "Still not verified": this connector has never been registered
against a Connect cluster, and the audit trail is where that gap matters most.

`auctions.sealed` must be ACL'd to the bid processor alone. That split is what
makes "the reserve never reaches the public read path" an infrastructure
guarantee rather than a thing every developer has to remember (D-06).

## Prerequisites

```
wal_level = logical
max_replication_slots >= 4
max_wal_senders >= 4
```

The connector's database user needs `REPLICATION`, plus `SELECT` on
`public.outbox`.

## Register

```bash
curl -X POST http://kafka-connect:8083/connectors \
  -H 'Content-Type: application/json' \
  -d @deploy/debezium/auction-admin-outbox.json
```

Credentials come from the environment (`ADMIN_DB_*`) so this file holds no
secrets and can live in the repository.

## Housekeeping

An unconsumed replication slot makes Postgres retain WAL indefinitely and will
eventually fill the disk. If the connector is removed, **drop its slot**:

```sql
SELECT pg_drop_replication_slot('eauction_admin_outbox');
```

Relayed rows are not cleaned up by Debezium, which never revisits a row after
reading its insert from the WAL. Prune on a schedule:

```sql
DELETE FROM outbox WHERE created_at < now() - interval '30 days';
```

Keep the window comfortably longer than the connector's worst expected outage,
or a slow restart will skip events that were deleted before it caught up.
