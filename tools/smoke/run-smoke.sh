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

KEEP_DATA=0
for a in "$@"; do [ "$a" = "--keep-data" ] && KEEP_DATA=1; done

# One master key for both the participant and the catcher. They derive the same
# per-bidder secret from it independently, so no secret is ever published (D-18).
MASTER_KEY="${SMOKE_MASTER_KEY:-$(openssl rand -hex 32)}"

mkdir -p "$RUN"
PIDS=()

cleanup() {
  # With --services-only the caller owns the services' lifetime, so leave them.
  [ "${SERVICES_ONLY:-0}" = 1 ] && return
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
  # Each is started only if it is not already answering, so --with-deps is safe to
  # pass every time. Starting Keycloak is not idempotent in the way the other two
  # are -- run-local.sh wipes its database to make the realm import take effect --
  # so it is skipped when the realm is already up, and the databases are then left
  # alone too (see the note on why they are coupled, below).
  if ! curl -fsS --noproxy '*' -o /dev/null "$ISSUER/.well-known/openid-configuration" 2>/dev/null; then
    say "Starting Keycloak…"
    "$REPO/deploy/keycloak/run-local.sh" || die "Keycloak did not start"
  else
    echo "  Keycloak already up"
  fi

  # The broker is wiped on the same terms as the databases, and for a stronger
  # reason than hygiene.
  #
  # Auctions live on compacted topics, so every run's auctions are still there on
  # the next one and the processor replays all of them. An ONSITE auction makes
  # that cumulative rather than merely untidy: nothing closes one on a clock (§29),
  # so each run leaves another permanently-running auction behind, and after a
  # handful of runs the processor is driving a crowd of them and a new auction's
  # AuctionStarted arrives too late for the walk-through to see. That is a real
  # failure with a confusing message, and it took wiping the broker by hand to see
  # what it was.
  #
  # run-local-broker.sh clears its data directory on start but refuses to restart a
  # running broker, so stopping first is what actually resets it.
  if [ "$KEEP_DATA" = 0 ]; then
    "$REPO/tools/kafka/run-local-broker.sh" stop >/dev/null 2>&1 || true
    sleep 2
  fi

  if ! (exec 3<>/dev/tcp/${KAFKA%%:*}/${KAFKA##*:}) 2>/dev/null; then
    say "Starting Kafka…"
    "$REPO/tools/kafka/run-local-broker.sh" start || die "Kafka did not start"
  else
    echo "  Kafka already up (--keep-data)"
  fi

  if ! pg_isready -q 2>/dev/null; then
    say "Starting Postgres…"
    # Whichever of these the host provides.
    pg_ctlcluster 16 main start 2>/dev/null \
      || service postgresql start >/dev/null 2>&1 \
      || true
    for _ in $(seq 1 30); do pg_isready -q && break; sleep 1; done
    pg_isready -q || die "Postgres did not start"
  else
    echo "  Postgres already up"
  fi
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
# run-portals.sh uses this: bring everything up, then stop short of the API
# walk-through and leave the services running for the browser to drive.
SERVICES_ONLY=0
for a in "$@"; do [ "$a" = "--services-only" ] && SERVICES_ONLY=1; done

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

  # Replace anything already on the port. --services-only deliberately leaves
  # services running, so the next ordinary run would otherwise fail to bind and
  # then be tested against the previous run's processes — which is confusing in
  # exactly the way that wastes an afternoon.
  local squatter
  squatter=$(pgrep -f "bin/Release/net8.0/$project.dll" 2>/dev/null | head -1)
  if [ -n "$squatter" ]; then
    echo "  replacing $name already running as pid $squatter"
    kill "$squatter" 2>/dev/null
    for _ in $(seq 1 20); do
      kill -0 "$squatter" 2>/dev/null || break
      sleep 0.5
    done
  fi

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
    setsid dotnet "$REPO/src/$project/bin/Release/net8.0/$project.dll" \
      >"$RUN/$name.log" 2>&1 < /dev/null &
  PIDS+=($!)
}

# The portals run on :3000 and :3001 and call these from a browser, so every
# service they talk to needs those origins. Default is no origins at all.
PORTAL_ORIGINS="http://localhost:3000,http://localhost:3001"

start auction-admin EAuction.AuctionAdmin 5101 \
  ConnectionStrings__Admin="$PG;Database=eauction_admin" \
  Admin__BidderMasterKeyHex="$MASTER_KEY" \
  Cors__AllowedOrigins="$PORTAL_ORIGINS"

start participant EAuction.Participant 5102 \
  ConnectionStrings__Participant="$PG;Database=eauction_participant" \
  Participant__BidderMasterKeyHex="$MASTER_KEY" \
  Cors__AllowedOrigins="$PORTAL_ORIGINS"

start bid-catcher EAuction.BidCatcher 5103 \
  Catcher__BidderMasterKeyHex="$MASTER_KEY" \
  Catcher__CeilingGraceSeconds=120 \
  Cors__AllowedOrigins="$PORTAL_ORIGINS"

start bid-processor EAuction.BidProcessor 5104 \
  Processor__CloseGraceSeconds=5 \
  Processor__TickIntervalMs=250 \
  Processor__RecoveryQuietSeconds=2

start query-bff EAuction.QueryBff 5105 \
  Cors__AllowedOrigins="$PORTAL_ORIGINS"

# Without this nothing charges the booklet fee or the deposit, and every bidder
# stalls at AwaitingDeposit — which is correct behaviour and a confusing failure,
# so the walk-through names this service when it times out.
#
# The simulated gateway settles everything, so Payments__AllowSimulatedGateway is
# how it is allowed to; in Production the service refuses to start without it.
start payments EAuction.Payments 5106 \
  Payments__AllowSimulatedGateway=true

# --- wait for health -------------------------------------------------------

for probe in "auction-admin 5101" "participant 5102" "query-bff 5105"; do
  set -- $probe
  for _ in $(seq 1 60); do
    curl -fsS --noproxy "*" -o /dev/null "http://127.0.0.1:$2/health/ready" 2>/dev/null && break
    sleep 1
  done
  curl -fsS --noproxy '*' -o /dev/null "http://127.0.0.1:$2/health/ready" \
    || { tail -25 "$RUN/$1.log"; die "$1 never became ready"; }
done
say "Services are up. Logs in $RUN/"

if [ "$SERVICES_ONLY" = 1 ]; then
  # setsid so the services outlive this script; the caller stops them.
  exit 0
fi

# --- the walk-through ------------------------------------------------------

# --- stepped-up tokens -----------------------------------------------------
#
# Paying a deposit, registering a national identity and confirming an award all
# require a second factor confirmed in the last few minutes. Keycloak only issues
# that through the authorization-code flow — a password grant is always level 1 —
# so these are obtained by completing a real browser login, which is also the
# honest model: an API client cannot confirm that a human is present, and that is
# the entire point of the gate.
say "Minting stepped-up tokens…"
[ -d "$REPO/web/node_modules" ] || (cd "$REPO/web" && npm install --no-audit --no-fund >/dev/null)

# Lives in the web workspace because node resolves its imports relative to the
# script's own directory, and @playwright/test is only installed there.
mint() {
  (cd "$REPO/web/e2e" && SMOKE_ISSUER="$ISSUER" NO_PROXY='*' no_proxy='*' \
    node ./stepup-token.mjs "$1" "$2" 2>"$RUN/stepup-$1.log")
}

SARA_STEPUP="$(mint sara bidder-web)"
KHALID_STEPUP="$(mint khalid bidder-web)"
COMMITTEE_STEPUP="$(mint committee-user admin-web)"

for pair in "sara:$SARA_STEPUP" "khalid:$KHALID_STEPUP" "committee-user:$COMMITTEE_STEPUP"; do
  if [ -z "${pair#*:}" ]; then
    tail -5 "$RUN/stepup-${pair%%:*}.log" 2>/dev/null
    die "could not mint a stepped-up token for ${pair%%:*}"
  fi
done
echo "  three stepped-up tokens"

echo
SMOKE_SARA_STEPUP="$SARA_STEPUP" \
SMOKE_KHALID_STEPUP="$KHALID_STEPUP" \
SMOKE_COMMITTEE_STEPUP="$COMMITTEE_STEPUP" \
SMOKE_ISSUER="$ISSUER" \
SMOKE_KAFKA="$KAFKA" \
SMOKE_ADMIN_URL=http://127.0.0.1:5101 \
SMOKE_PARTICIPANT_URL=http://127.0.0.1:5102 \
SMOKE_CATCHER_URL=http://127.0.0.1:5103 \
SMOKE_BFF_URL=http://127.0.0.1:5105 \
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
