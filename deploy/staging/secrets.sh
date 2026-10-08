#!/usr/bin/env bash
#
# Creates the Secrets the demo needs, once, and never prints one.
#
# Generate-if-absent rather than create-or-replace, and that is the whole point
# of the script. Three of these keys cannot be rotated casually:
#
#   * Documents__GrantKeyHex signs the grants that let a bidder open the booklet
#     they paid for and a winner open their award letter. Rotate it and every
#     grant already issued stops verifying.
#   * Participant__BidderMasterKeyHex is what both the participant service and
#     the catcher derive each bidder's signing secret from. Rotate it and every
#     key a bidder's browser holds becomes wrong, so their next bid is refused
#     with BadSignature.
#   * The Postgres password is baked into five connection strings. Rotate it in
#     one place and the other five stop connecting.
#
# So a re-run must be a no-op for anything that exists. `kubectl create secret`
# fails on a secret that is already there, and that failure is the desired
# behaviour, not an error to paper over — hence the explicit existence check
# rather than `--dry-run=client | kubectl apply`, which would overwrite.
#
# Values are generated here and never leave the cluster: nothing is echoed, no
# value is passed on a command line that gets logged, and `set +x` is asserted.
set -euo pipefail
set +x   # belt and braces: a trace here would put keys in the Actions log

NS="${NAMESPACE:-e-auctions}"

have() { kubectl -n "$NS" get secret "$1" >/dev/null 2>&1; }

# A note on randomness: openssl rand is used rather than $RANDOM, which is a
# 15-bit PRNG seeded from the pid and would make these guessable.
hex()  { openssl rand -hex "$1"; }
pass() { openssl rand -base64 24 | tr -d '\n/+=' | head -c 24; }

made=0
skipped=0

note_made()    { echo "  created  $1"; made=$((made + 1)); }
note_skipped() { echo "  kept     $1 (already present)"; skipped=$((skipped + 1)); }

# --- the Postgres password, and the five connection strings derived from it ---
#
# Derived rather than independent: five separately generated passwords for one
# server is five chances for one of them to be wrong.
if have eauction-postgres; then
  note_skipped eauction-postgres
  PGPASS="$(kubectl -n "$NS" get secret eauction-postgres -o jsonpath='{.data.password}' | base64 -d)"
else
  PGPASS="$(pass)"
  kubectl -n "$NS" create secret generic eauction-postgres \
    --from-literal=username=eauction \
    --from-literal=password="$PGPASS" >/dev/null
  note_made eauction-postgres
fi

PGHOST="postgres.${NS}.svc.cluster.local"

db_secret() {   # $1 = secret name, $2 = database
  if have "$1"; then note_skipped "$1"; return; fi
  kubectl -n "$NS" create secret generic "$1" \
    --from-literal=connection-string="Host=${PGHOST};Database=$2;Username=eauction;Password=${PGPASS}" \
    >/dev/null
  note_made "$1"
}

db_secret eauction-admin-db         eauction_admin
db_secret eauction-participant-db   eauction_participant
db_secret eauction-notifications-db eauction_notifications
db_secret eauction-audit-db         eauction_audit
db_secret eauction-reporting-db     eauction_reporting

# --- the keys ---------------------------------------------------------------

if have eauction-document-grants; then note_skipped eauction-document-grants; else
  # One key, three services. auction-admin and participant mint grants with it
  # and documents verifies them; if they ever disagree, no bidder can open the
  # booklet they paid for.
  kubectl -n "$NS" create secret generic eauction-document-grants \
    --from-literal=grant-key="$(hex 32)" >/dev/null
  note_made eauction-document-grants
fi

if have eauction-bidder-keys; then note_skipped eauction-bidder-keys; else
  kubectl -n "$NS" create secret generic eauction-bidder-keys \
    --from-literal=master-key="$(hex 32)" >/dev/null
  note_made eauction-bidder-keys
fi

if have eauction-bidder-master-key; then note_skipped eauction-bidder-master-key; else
  # The catcher reads the same master key under its own secret name, so it is
  # the same value in both. Sharing one secret across the two would have been
  # tidier; the chart names them separately and the chart is what ships.
  MK="$(kubectl -n "$NS" get secret eauction-bidder-keys -o jsonpath='{.data.master-key}' | base64 -d)"
  kubectl -n "$NS" create secret generic eauction-bidder-master-key \
    --from-literal=master-key-hex="$MK" >/dev/null
  note_made eauction-bidder-master-key
fi

if have eauction-bid-receipt-key; then note_skipped eauction-bid-receipt-key; else
  kubectl -n "$NS" create secret generic eauction-bid-receipt-key \
    --from-literal=receipt-key-hex="$(hex 32)" >/dev/null
  note_made eauction-bid-receipt-key
fi

# --- MinIO and Keycloak -----------------------------------------------------

if have eauction-object-store; then note_skipped eauction-object-store; else
  # MinIO reads these as its root credentials and the document service as its
  # client credentials: one secret, both sides, so they cannot drift.
  kubectl -n "$NS" create secret generic eauction-object-store \
    --from-literal=access-key="eauction" \
    --from-literal=secret-key="$(pass)" >/dev/null
  note_made eauction-object-store
fi

if have eauction-keycloak; then note_skipped eauction-keycloak; else
  kubectl -n "$NS" create secret generic eauction-keycloak \
    --from-literal=admin-username=admin \
    --from-literal=admin-password="$(pass)" >/dev/null
  note_made eauction-keycloak
fi

echo
echo "secrets: $made created, $skipped kept"
echo
echo "To read one back (do this in a terminal, not in CI):"
echo "  kubectl -n $NS get secret eauction-keycloak -o jsonpath='{.data.admin-password}' | base64 -d"
