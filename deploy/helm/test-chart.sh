#!/usr/bin/env bash
# Verifies the chart renders correctly on both Kubernetes flavours.
#
# The portability claim is only worth something if it is checked: a template
# that hard-codes runAsUser passes `helm lint` happily and is then rejected by
# OpenShift's SCC at deploy time, which is the worst place to find out.
#
#   ./deploy/helm/test-chart.sh
set -euo pipefail

CHART="$(cd "$(dirname "$0")" && pwd)/e-auction"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "  ok   $*"; }

echo "==> lint"
for platform in kubernetes openshift; do
  helm lint "$CHART" --set platform="$platform" >/dev/null || fail "lint failed for $platform"
  pass "lint $platform"
done

echo "==> render"
# helm template executes NOTES.txt too, so a broken reference in it fails here
# even though the notes are not printed.
helm template eauction "$CHART" > "$OUT/k8s.yaml"
helm template eauction "$CHART" --set platform=openshift > "$OUT/ocp.yaml"
pass "both platforms render"

echo "==> exposure object"
grep -q "kind: Ingress" "$OUT/k8s.yaml" || fail "kubernetes produced no Ingress"
grep -q "kind: Route"   "$OUT/ocp.yaml" || fail "openshift produced no Route"
# A Route on plain Kubernetes is rejected by the API server; an Ingress on
# OpenShift has no controller watching it by default.
grep -q "kind: Route"   "$OUT/k8s.yaml" && fail "kubernetes must not produce a Route"
grep -q "kind: Ingress" "$OUT/ocp.yaml" && fail "openshift must not produce an Ingress"
pass "Ingress on kubernetes, Route on openshift, neither crosses over"

echo "==> UID handling"
grep -q "runAsUser" "$OUT/k8s.yaml" || fail "kubernetes must pin a UID; nothing else will"
grep -q "runAsUser" "$OUT/ocp.yaml" && fail "openshift must let the SCC assign the UID"
grep -q "fsGroup"   "$OUT/ocp.yaml" && fail "openshift must let the SCC assign fsGroup"
pass "UID pinned on kubernetes, delegated on openshift"

echo "==> hardening (both platforms)"
for f in "$OUT/k8s.yaml" "$OUT/ocp.yaml"; do
  grep -q "runAsNonRoot: true"            "$f" || fail "$f: missing runAsNonRoot"
  grep -q "readOnlyRootFilesystem: true"  "$f" || fail "$f: missing readOnlyRootFilesystem"
  grep -q "allowPrivilegeEscalation: false" "$f" || fail "$f: privilege escalation not disabled"
  grep -q "type: RuntimeDefault"          "$f" || fail "$f: missing seccomp profile"
done
pass "non-root, read-only rootfs, no privilege escalation, seccomp"

echo "==> no secrets in rendered output"
for f in "$OUT/k8s.yaml" "$OUT/ocp.yaml"; do
  grep -Eq "ConnectionStrings__Admin: *[^ ]" "$f" && fail "$f: a connection string was templated in"
  grep -q "secretKeyRef" "$f" || fail "$f: expected the admin DB to come from a Secret"
done
pass "credentials referenced, never rendered"

echo "==> package"
helm package "$CHART" --destination "$OUT" >/dev/null || fail "packaging failed"
pass "$(basename "$(ls "$OUT"/e-auction-*.tgz)")"

echo
echo "chart is portable across both platforms"
