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
    --built) ;;  # handled below; run-smoke.sh has no use for it
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
  pkill -f '[v]ite preview --port 300' 2>/dev/null
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

# The Content-Security-Policy is injected at build and not by the dev server, so a
# policy that broke the portals would pass a walk-through driven against `vite dev`.
# --built builds both bundles and serves those instead, which is the only way this
# suite sees the thing that actually ships.
BUILT=0
for a in "$@"; do [ "$a" = "--built" ] && BUILT=1; done

# vite build bakes these in, and config.ts refuses to start a production bundle
# without them. They are the same addresses the dev server's fallbacks use.
export VITE_ISSUER="${SMOKE_ISSUER:-http://localhost:8080/realms/eauction}"
export VITE_ADMIN_API="${VITE_ADMIN_API:-http://localhost:5101}"
export VITE_PARTICIPANT_API="${VITE_PARTICIPANT_API:-http://localhost:5102}"
export VITE_CATCHER_API="${VITE_CATCHER_API:-http://localhost:5103}"
export VITE_QUERY_API="${VITE_QUERY_API:-http://localhost:5105}"
export VITE_DOCUMENTS_API="${VITE_DOCUMENTS_API:-http://localhost:5107}"
export VITE_NOTIFICATIONS_API="${VITE_NOTIFICATIONS_API:-http://localhost:5108}"
export VITE_AUDIT_API="${VITE_AUDIT_API:-http://localhost:5109}"
export VITE_REPORTING_API="${VITE_REPORTING_API:-http://localhost:5110}"

if [ "$BUILT" = 1 ]; then
  say "Building the portals…"
  (cd "$REPO/web" && npm run build --silent) > "$RUN/web-build.log" 2>&1 \
    || { tail -30 "$RUN/web-build.log"; die "the portals did not build"; }

  for portal in bidder admin; do
    grep -q 'Content-Security-Policy' "$REPO/web/$portal/dist/index.html" \
      || die "$portal built without a Content-Security-Policy"
  done
  echo "  both bundles carry a Content-Security-Policy"
fi

say "Starting the portals…"
start_portal() {
  local name="$1" port="$2"
  if [ "$BUILT" = 1 ]; then
    # --strictPort for the same reason the dev server uses it: the port is a
    # registered redirect URI, so drifting to another one breaks every login.
    (cd "$REPO/web/$name" && npx vite preview --port "$port" --strictPort) \
      > "$RUN/$name-web.log" 2>&1 &
  else
    (cd "$REPO/web" && npm run "dev:$name" --silent) > "$RUN/$name-web.log" 2>&1 &
  fi
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
echo "  bidder  http://localhost:3000$([ "$BUILT" = 1 ] && echo '  (built)')"
echo "  admin   http://localhost:3001$([ "$BUILT" = 1 ] && echo '  (built)')"

# --- the walk-through ------------------------------------------------------

if [ "$KEEP_UP" = 1 ]; then
  say "Everything is up. Password for every account: dev-only-password"
  echo "  bidder  :3000  sara, khalid"
  echo "  admin   :3001  admin-user (prepare)  committee-user (approve + award)"
  echo "                 clerk-user (قاعة المزاد)  reporting-user (التقارير)"
  echo "                 auditor-user (سجل المراجعة — and nothing else, by design)"
  echo
  echo "  sandbox :5111  صندوق التجارب — open this one FIRST and keep it open."
  echo "                 Registering and paying a deposit are behind a second factor,"
  echo "                 and this is the only place the code can be read. It also has"
  echo "                 the payment gateway's switch, for the non-payment path."
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
