#!/usr/bin/env bash
# Puts demo auctions into المزادات through auction-admin's own API: a clerk
# prepares each one, the award committee approves it. Run on the VM, after
# deploy.sh has brought the stack up.
#
#   deploy/vm/seed-auctions.sh [--count 6]
#
# Every auction is approved and opens in the future, so they sit there as upcoming
# and nothing closes on its own during the demo. History for التقارير and سجل
# المراجعة is a separate step: tools/seed.
#
# auction-admin has no delete, so this refuses to run while demo auctions exist
# rather than doubling them.
set -euo pipefail

COUNT=6
while [ $# -gt 0 ]; do
  case "$1" in
    --count) COUNT="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

BASE="${SEED_BASE:-http://localhost}"
PASSWORD="${DEMO_PASSWORD:-dev-only-password}"
# The realm's seeded second factor for committee-user (deploy/keycloak/README.md).
TOTP_SECRET="eauctiondevsecret1234567890"

say() { printf '%s\n' "$*"; }

totp() {
  python3 -c '
import hashlib, hmac, struct, sys, time
key = sys.argv[1].encode()
counter = struct.pack(">Q", int(time.time()) // 30)
digest = hmac.new(key, counter, hashlib.sha1).digest()
offset = digest[-1] & 15
print("%06d" % ((struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000))
' "$TOTP_SECRET"
}

token() {
  local user="$1" otp="${2:-}"
  local args=(-d grant_type=password -d client_id=admin-web -d "username=$user" --data-urlencode "password=$PASSWORD")
  [ -n "$otp" ] && args+=(-d "totp=$otp")
  curl -fsS "$BASE/auth/realms/eauction/protocol/openid-connect/token" "${args[@]}" \
    | python3 -c 'import json, sys; print(json.load(sys.stdin)["access_token"])'
}

subject() {
  python3 -c '
import base64, json, sys
payload = sys.argv[1].split(".")[1]
payload += "=" * (-len(payload) % 4)
print(json.loads(base64.urlsafe_b64decode(payload))["sub"])
' "$1"
}

# api METHOD PATH TOKEN [JSON] -> response body on stdout; any HTTP error fails the script.
api() {
  local method="$1" path="$2" tok="$3" body="${4:-}"
  if [ -n "$body" ]; then
    curl -fsS -X "$method" "$BASE$path" -H "Authorization: Bearer $tok" \
      -H 'Content-Type: application/json' -d "$body"
  else
    curl -fsS -X "$method" "$BASE$path" -H "Authorization: Bearer $tok"
  fi
}

say "Signing in: admin-user (preparation) and committee-user (approval)"
ADMIN_TOKEN="$(token admin-user)"
COMMITTEE_TOKEN="$(token committee-user "$(totp)")"
ADMIN_SUB="$(subject "$ADMIN_TOKEN")"

EXISTING="$(api GET "/api/admin/auctions?q=Demo&take=200" "$ADMIN_TOKEN" | python3 -c '
import json, sys
data = json.load(sys.stdin)
items = data if isinstance(data, list) else next(v for v in data.values() if isinstance(v, list))
print(sum(1 for a in items if str(a.get("nameEn", "")).startswith("Demo ")))
')"
if [ "$EXISTING" != "0" ]; then
  say "Refusing: $EXISTING demo auction(s) already exist. auction-admin cannot delete them, so this would double them."
  exit 1
fi

PDF="$(mktemp)"
trap 'rm -f "$PDF"' EXIT
printf '%%PDF-1.4\n%% Demo booklet placeholder, not a real conditions document.\n%%%%EOF\n' > "$PDF"

say "Preparing $COUNT auctions"
for i in $(seq 1 "$COUNT"); do
  body="$(python3 -c '
import json, sys
i = int(sys.argv[1])
print(json.dumps({"createdByUserId": sys.argv[2], "nameAr": f"تجريبي — قطعة سكنية {i}", "nameEn": f"Demo residential plot {i}"}))
' "$i" "$ADMIN_SUB")"
  id="$(api POST "/api/admin/auctions" "$ADMIN_TOKEN" "$body" | python3 -c 'import json, sys; print(json.load(sys.stdin)["id"])')"

  plot="$(python3 -c '
import json, sys
i = int(sys.argv[1])
print(json.dumps({
    "plotNumber": f"DEMO-{i:02d}", "areaSqm": 600 + i * 45.5,
    "latitude": "21.5433", "longitude": "39.1728",
    "descriptionAr": f"قطعة سكنية تجريبية رقم {i}", "descriptionEn": f"Demo residential plot {i}",
    "streetWidthMeters": 15.0, "frontageMeters": 20.0,
}))
' "$i")"
  api POST "/api/admin/auctions/$id/plots" "$ADMIN_TOKEN" "$plot" >/dev/null

  doc="$(curl -fsS -X POST "$BASE/api/documents/documents" -H "Authorization: Bearer $ADMIN_TOKEN" \
    -F "file=@$PDF;type=application/pdf;filename=demo-booklet-$i.pdf" -F "access=Restricted")"
  doc_id="$(printf '%s' "$doc" | python3 -c 'import json, sys; print(json.load(sys.stdin)["id"])')"
  api POST "/api/admin/auctions/$id/booklet" "$ADMIN_TOKEN" "{\"documentId\": \"$doc_id\"}" >/dev/null

  terms="$(python3 -c '
import json, sys
from datetime import datetime, timedelta, timezone
i = int(sys.argv[1])
starts = datetime.now(timezone.utc).replace(microsecond=0) + timedelta(days=2 * i + 1)
opening = (900_000 + i * 150_000) * 100
print(json.dumps({
    "nameAr": f"تجريبي — قطعة سكنية {i}", "nameEn": f"Demo residential plot {i}",
    "channel": "Online", "bidderVisibility": "Masked",
    "startsAt": starts.isoformat().replace("+00:00", "Z"),
    "endsAt": (starts + timedelta(days=2)).isoformat().replace("+00:00", "Z"),
    "openingPriceMinorUnits": opening, "reservePriceMinorUnits": int(opening * 0.9),
    "minIncrementMinorUnits": 25_000_00, "depositMinorUnits": 50_000_00,
    "brokerageFeePercent": 2.5, "bookletPriceMinorUnits": 1_000_00,
    "quietPeriodSeconds": None, "maxExtensions": 0, "phase": "phase-1",
}))
' "$i")"
  api PUT "/api/admin/auctions/$id" "$ADMIN_TOKEN" "$terms" >/dev/null

  problems="$(api GET "/api/admin/auctions/$id/validation" "$ADMIN_TOKEN" | python3 -c '
import json, sys
print("; ".join(json.load(sys.stdin)["problems"]))
')"
  if [ -n "$problems" ]; then
    say "  auction $i left as a draft: $problems"
    continue
  fi

  api POST "/api/admin/auctions/$id/submit" "$ADMIN_TOKEN" >/dev/null
  api POST "/api/admin/auctions/$id/approve" "$COMMITTEE_TOKEN" >/dev/null
  say "  auction $i approved ($id)"
done

say "Done. The auctions are in المزادات as upcoming."
