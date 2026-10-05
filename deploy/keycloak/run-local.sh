#!/usr/bin/env bash
# Start Keycloak locally with the eauction realm imported.
#
# A realm import is NOT idempotent against an existing realm -- Keycloak logs
# "realm eauction already exists" and keeps the old one, so a change to the realm
# file would silently not take effect. This script wipes the dev database first,
# which is safe precisely because start-dev's H2 file is not meant to survive.
#
# Usage: KEYCLOAK_HOME=/path/to/keycloak deploy/keycloak/run-local.sh [--port 8080]
set -euo pipefail

KEYCLOAK_HOME="${KEYCLOAK_HOME:-/tmp/keycloak}"
PORT=8080
while [ $# -gt 0 ]; do
  case "$1" in
    --port) PORT="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ ! -x "$KEYCLOAK_HOME/bin/kc.sh" ]; then
  echo "No Keycloak at $KEYCLOAK_HOME. Set KEYCLOAK_HOME, or download a distribution from" >&2
  echo "https://github.com/keycloak/keycloak/releases and extract it there." >&2
  exit 1
fi

REALM="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/eauction-realm.json"

# Keycloak's import rejects any field it does not know, including a key added as a
# comment, and fails the whole server start. Catch that here rather than 40 seconds
# into a boot.
python3 - "$REALM" <<'PY'
import json, sys
def walk(node, path="$"):
    if isinstance(node, dict):
        for k, v in node.items():
            if k.startswith("_"):
                sys.exit(f"{path}.{k}: Keycloak's realm import rejects unknown fields. "
                         "Put the explanation in README.md instead.")
            walk(v, f"{path}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, f"{path}[{i}]")
walk(json.load(open(sys.argv[1])))
PY

# The bracket keeps the pattern from matching this script's own command line, which
# otherwise makes the script kill the shell running it.
pkill -f "[k]eycloak.*QuarkusEntryPoint" 2>/dev/null || true
pkill -f "[k]c.sh start-dev" 2>/dev/null || true
sleep 2

rm -rf "$KEYCLOAK_HOME/data/h2"
mkdir -p "$KEYCLOAK_HOME/data/import"
cp "$REALM" "$KEYCLOAK_HOME/data/import/"

cd "$KEYCLOAK_HOME"
KEYCLOAK_ADMIN="${KEYCLOAK_ADMIN:-admin}" \
KEYCLOAK_ADMIN_PASSWORD="${KEYCLOAK_ADMIN_PASSWORD:-admin}" \
JAVA_TOOL_OPTIONS= \
  setsid nohup bin/kc.sh start-dev --import-realm \
    --http-port="$PORT" --hostname-strict=false \
    > "$KEYCLOAK_HOME/kc.log" 2>&1 < /dev/null &

echo "Starting Keycloak on :$PORT (log: $KEYCLOAK_HOME/kc.log)"
DISCOVERY="http://localhost:$PORT/realms/eauction/.well-known/openid-configuration"
for _ in $(seq 1 60); do
  if curl -fsS --noproxy '*' -o /dev/null "$DISCOVERY" 2>/dev/null; then
    echo "Realm eauction is up: $DISCOVERY"
    exit 0
  fi
  if grep -q 'Failed to start server' "$KEYCLOAK_HOME/kc.log" 2>/dev/null; then
    echo "Keycloak failed to start:" >&2
    grep -A3 'ERROR' "$KEYCLOAK_HOME/kc.log" | head -20 >&2
    exit 1
  fi
  sleep 2
done
echo "Keycloak did not come up within 120s; see $KEYCLOAK_HOME/kc.log" >&2
exit 1
