#!/usr/bin/env bash
# Browser walk-through of both portals, against the real stack.
#
#   tools/smoke/run-portals.sh [--with-deps] [--keep-up]
#
# Brings up the services the way run-smoke.sh does, starts both Vite dev servers,
# then drives them with Playwright. --keep-up leaves everything running so you can
# open http://localhost:3000 and http://localhost:3001 yourself.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RUN="${SMOKE_RUN_DIR:-/tmp/eauction-smoke}"
KEEP_UP=0
PASS_THROUGH=()
for a in "$@"; do
  case "$a" in
    --keep-up) KEEP_UP=1 ;;
    *) PASS_THROUGH+=("$a") ;;
  esac
done

say() { printf '\033[1m%s\033[0m\n' "$*"; }
die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

PIDS=()
cleanup() {
  if [ "$KEEP_UP" = 1 ]; then
    say "Left running (--keep-up). Stop with: pkill -f 'EAuction\.|[v]ite'"
    return
  fi
  for pid in "${PIDS[@]:-}"; do [ -n "$pid" ] && kill "$pid" 2>/dev/null; done
  # The dev servers and the services that run-services.sh started.
  pkill -f '[v]ite --port 300' 2>/dev/null
  pkill -f 'bin/Release/net8.0/EAuction\.' 2>/dev/null
  wait 2>/dev/null
}
trap cleanup EXIT INT TERM

mkdir -p "$RUN"

# --- the stack -------------------------------------------------------------
#
# run-smoke.sh already knows how to bring everything up in the right order, and
# --services-only stops it short of the API walk-through so the browser can do it.
say "Bringing up the stack…"
"$REPO/tools/smoke/run-smoke.sh" --services-only "${PASS_THROUGH[@]:-}" \
  || die "the stack did not come up; see $RUN/"

# --- the portals -----------------------------------------------------------

[ -d "$REPO/web/node_modules" ] || {
  say "Installing web dependencies…"
  (cd "$REPO/web" && npm install --no-audit --no-fund) || die "npm install failed"
}

say "Starting the portals…"
start_portal() {
  local name="$1" port="$2"
  (cd "$REPO/web" && npm run "dev:$name" --silent) > "$RUN/$name-web.log" 2>&1 &
  PIDS+=($!)

  for _ in $(seq 1 60); do
    curl -fsS --noproxy '*' -o /dev/null "http://localhost:$port" 2>/dev/null && return 0
    sleep 1
  done
  tail -20 "$RUN/$name-web.log"
  die "$name portal did not start on :$port"
}
start_portal bidder 3000
start_portal admin 3001
echo "  bidder  http://localhost:3000"
echo "  admin   http://localhost:3001"

# --- the walk-through ------------------------------------------------------

if [ "$KEEP_UP" = 1 ]; then
  say "Everything is up. Sign in as sara / admin-user / committee-user, password dev-only-password."
  say "Ctrl-C to stop."
  wait
  exit 0
fi

echo
say "Driving the portals…"
cd "$REPO/web"
CHROMIUM_PATH="${CHROMIUM_PATH:-/opt/pw-browsers/chromium}" \
NO_PROXY='*' no_proxy='*' \
  npm run e2e --silent
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
