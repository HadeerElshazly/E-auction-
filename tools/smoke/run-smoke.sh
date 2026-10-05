#!/usr/bin/env bash
# End-to-end smoke test: one complete auction through all four services, against
# real Keycloak, real Kafka and real Postgres.
#
#   tools/smoke/run-smoke.sh
#
# Expects Postgres on :5432 and a Kafka broker on :9092 (tools/kafka/run-local-broker.sh
# starts one), and Keycloak with the eauction realm (deploy/keycloak/run-local.sh).
# Pass --with-deps to have this script start Keycloak itself.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RUN="${SMOKE_RUN_DIR:-/tmp/eauction-smoke}"
KAFKA="${SMOKE_KAFKA:-127.0.0.1:9092}"
ISSUER="${SMOKE_ISSUER:-http://localhost:8080/realms/eauction}"
PG="${SMOKE_PG:-Host=localhost;Username=eauction;Password=eauction}"
WITH_DEPS=0
for a in "$@"; do [ "$a" = "--with-deps" ] && WITH_DEPS=1; done

# One master key for both the participant and the catcher. They derive the same
# per-bidder secret from it independently, so no secret is ever published (D-18).
MASTER_KEY="${SMOKE_MASTER_KEY:-$(openssl rand -hex 32)}"

mkdir -p "$RUN"
PIDS=()

cleanup() {
  for pid in "${PIDS[@]:-}"; do
    [ -n "$pid" ] && kill "$pid" 2>/dev/null
  done
  wait 2>/dev/null
}
trap cleanup EXIT INT TERM

say() { printf '\033[1m%s\033[0m\n' "$*"; }
die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

# --- dependencies ----------------------------------------------------------

if [ "$WITH_DEPS" = 1 ]; then
  say "Starting Keycloak…"
  "$REPO/deploy/keycloak/run-local.sh" || die "Keycloak did not start"
fi

say "Checking dependencies…"
curl -fsS --noproxy '*' -o /dev/null "$ISSUER/.well-known/openid-configuration" \
  || die "No Keycloak realm at $ISSUER. Run deploy/keycloak/run-local.sh (or pass --with-deps)."
echo "  Keycloak   $ISSUER"

pg_isready -q || die "No Postgres on :5432."
echo "  Postgres   ${PG%%;*}"

# A broker that is listening but has no metadata yet fails the first produce, so
# check the port rather than assume.
(exec 3<>/dev/tcp/${KAFKA%%:*}/${KAFKA##*:}) 2>/dev/null \
  || die "No Kafka on $KAFKA. Run tools/kafka/run-local-broker.sh."
echo "  Kafka      $KAFKA"

# Fresh databases, unless --keep-data.
#
# Not just hygiene: deploy/keycloak/run-local.sh wipes Keycloak's dev database, so
# every run mints new subject ids for sara and khalid. Their national IDs do not
# change, and one national ID is one bidder (D-25) — so a participant database
# carried over from a previous run answers the registration with a correct 409 and
# the walk-through cannot proceed. The identity store and the participant store are
# coupled; they are reset together or not at all.
KEEP_DATA=0
for a in "$@"; do [ "$a" = "--keep-data" ] && KEEP_DATA=1; done

PSQL="postgresql://eauction:eauction@localhost/postgres"
for db in eauction_admin eauction_participant; do
  if [ "$KEEP_DATA" = 0 ]; then
    psql -qtAX "$PSQL" -c "DROP DATABASE IF EXISTS $db WITH (FORCE)" >/dev/null
  fi
  psql -qtAX "$PSQL" -c "SELECT 1 FROM pg_database WHERE datname='$db'" | grep -q 1 \
    || psql -qtAX "$PSQL" -c "CREATE DATABASE $db" >/dev/null
done
[ "$KEEP_DATA" = 1 ] && echo "  databases  kept (--keep-data)" \
                     || echo "  databases  recreated"

# --- build -----------------------------------------------------------------

say "Building…"
dotnet build "$REPO/EAuction.sln" -c Release -v q --nologo >"$RUN/build.log" 2>&1 \
  || { tail -30 "$RUN/build.log"; die "build failed"; }

# --- topics ----------------------------------------------------------------

# The broker has auto.create.topics.enable=false, as a real one should: an
# auto-created topic gets cleanup.policy=delete, and five of these must be
# compacted or the catcher's state silently ages out from under it.
say "Provisioning control topics…"
dotnet "$REPO/tools/topics/bin/Release/net8.0/EAuction.Topics.dll" \
  --bootstrap "$KAFKA" --replication 1 --partitions 1 || die "topic provisioning failed"

# --- schema ----------------------------------------------------------------

# The services do not migrate at startup, on purpose: several replicas would race
# and a schema change would run while the previous version is still serving. This
# is the step a Helm hook or a Kubernetes Job runs.
say "Applying migrations…"
dotnet "$REPO/tools/migrate/bin/Release/net8.0/EAuction.Migrate.dll" \
  --admin "$PG;Database=eauction_admin" \
  --participant "$PG;Database=eauction_participant" || die "migrations failed"

# --- services --------------------------------------------------------------

start() {
  local name="$1" project="$2" port="$3"; shift 3
  say "Starting $name on :$port"
  env "${@}" \
    ASPNETCORE_URLS="http://127.0.0.1:$port" \
    ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_ENVIRONMENT=Development \
    Jwt__Authority="$ISSUER" \
    Jwt__Audience=eauction \
    Jwt__RequireHttpsMetadata=false \
    Kafka__BootstrapServers="$KAFKA" \
    Kafka__ReplicationFactor=1 \
    dotnet "$REPO/src/$project/bin/Release/net8.0/$project.dll" \
      >"$RUN/$name.log" 2>&1 &
  PIDS+=($!)
}

start auction-admin EAuction.AuctionAdmin 5101 \
  ConnectionStrings__Admin="$PG;Database=eauction_admin"

start participant EAuction.Participant 5102 \
  ConnectionStrings__Participant="$PG;Database=eauction_participant" \
  Participant__BidderMasterKeyHex="$MASTER_KEY"

start bid-catcher EAuction.BidCatcher 5103 \
  Catcher__BidderMasterKeyHex="$MASTER_KEY" \
  Catcher__CeilingGraceSeconds=120

start bid-processor EAuction.BidProcessor 5104 \
  Processor__CloseGraceSeconds=5 \
  Processor__TickIntervalMs=250 \
  Processor__RecoveryQuietSeconds=2

# --- wait for health -------------------------------------------------------

for probe in "auction-admin 5101" "participant 5102"; do
  set -- $probe
  for _ in $(seq 1 60); do
    curl -fsS --noproxy "*" -o /dev/null "http://127.0.0.1:$2/health/ready" 2>/dev/null && break
    sleep 1
  done
  curl -fsS --noproxy '*' -o /dev/null "http://127.0.0.1:$2/health/ready" \
    || { tail -25 "$RUN/$1.log"; die "$1 never became ready"; }
done
say "Services are up. Logs in $RUN/"

# --- the walk-through ------------------------------------------------------

echo
SMOKE_ISSUER="$ISSUER" \
SMOKE_KAFKA="$KAFKA" \
SMOKE_ADMIN_URL=http://127.0.0.1:5101 \
SMOKE_PARTICIPANT_URL=http://127.0.0.1:5102 \
SMOKE_CATCHER_URL=http://127.0.0.1:5103 \
NO_PROXY='*' no_proxy='*' \
  dotnet "$REPO/tools/smoke/bin/Release/net8.0/EAuction.Smoke.dll"
RESULT=$?

if [ "$RESULT" != 0 ]; then
  echo
  say "Service logs (tail):"
  for log in "$RUN"/*.log; do
    echo "--- $(basename "$log")"
    tail -12 "$log"
  done
fi

exit $RESULT
