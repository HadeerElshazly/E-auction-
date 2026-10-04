# Deployment charts

One chart, two Kubernetes flavours.

```bash
# plain Kubernetes / Rancher / RKE2 / Tanzu
helm upgrade --install eauction deploy/helm/e-auction \
  -f deploy/helm/e-auction/values-jeddah.yaml

# OpenShift / OKD
helm upgrade --install eauction deploy/helm/e-auction \
  -f deploy/helm/e-auction/values-openshift.yaml \
  -f deploy/helm/e-auction/values-jeddah.yaml
```

Verify before you ship:

```bash
./deploy/helm/test-chart.sh
```

## Why portable

The on-prem Kubernetes distribution was not known when the charts were
written, and guessing wrong is not a small problem: a chart written for plain
Kubernetes does not degrade on OpenShift, it is **rejected**. So the chart
carries both paths and a single switch.

```yaml
platform: kubernetes   # or: openshift
```

Two things differ, and nothing else:

| | plain Kubernetes | OpenShift |
|---|---|---|
| External traffic | `Ingress` | `Route` |
| Pod UID | pinned to 10001 | assigned by the SCC |

**Exposure.** OpenShift ships no Ingress controller by default; it has Routes,
served by its own router. Plain Kubernetes has no `Route` type at all, so a
chart that ships one is refused by the API server before anything starts.

**UID.** OpenShift's `restricted-v2` SCC gives each namespace a UID range and
admits a pod only if it does not request a UID outside it. A chart that
hard-codes `runAsUser` therefore fails to schedule — so on OpenShift the chart
states the requirement (`runAsNonRoot`) and lets the platform pick the number.
Plain Kubernetes assigns nothing, so there the chart must pin it.

Everything else is identical and equally locked down on both: no privilege
escalation, all capabilities dropped, read-only root filesystem, RuntimeDefault
seccomp, `/tmp` as an emptyDir so an arbitrary assigned UID still has somewhere
to write.

## Why the test script exists

`helm lint` passes a chart that hard-codes `runAsUser` without complaint. The
failure shows up at deploy time, on the client's cluster, as a pod that will
not schedule — the worst possible place to discover it.

`test-chart.sh` renders both platforms and asserts the invariants: that an
Ingress never appears on OpenShift, that a Route never appears on plain
Kubernetes, that the UID is pinned on one and delegated on the other, that the
hardening holds on both, and that no credential is ever templated into the
output. It is checked to actually fail when the guard is removed.

## Layout

```
e-auction/
├── Chart.yaml              version 0.1.0 = the deployment version
├── values.yaml             defaults
├── values-openshift.yaml   platform overlay
├── values-jeddah.yaml      client overlay
├── charts/
│   └── e-auction-common/   library chart: shared templates
└── templates/
    ├── bid-catcher.yaml
    ├── bid-processor.yaml
    ├── auction-admin.yaml
    └── NOTES.txt
```

The library chart is **vendored** under `charts/` rather than pulled from a
repository. It holds nothing but templates for this chart, and vendoring means
`helm package` needs no network and CI needs no `helm dependency build`.

### Not subcharts

The architecture document originally proposed an umbrella chart with one
subchart per service. Building it, that turned out to be the wrong shape:
subcharts buy independent versioning, and here the chart version *is* the
deployment version — a client names one number for the whole release. Three
near-identical subcharts would have been duplication in exchange for a
property we deliberately do not want.

A library chart plus one application chart gives the same deduplication with
one version and one `helm package`. `enabled: false` per service still works.

## Versioning

`Chart.yaml` `version` is the deployment version; `appVersion` is the image
tag. They move together, so a release is one coordinate.

```bash
helm package deploy/helm/e-auction
helm push e-auction-0.1.0.tgz oci://eauctionacr.azurecr.io/helm
```

## Secrets

No credential appears in any values file. `secretEnv` references a Secret the
cluster already holds — from External Secrets Operator, sealed-secrets, or
created out of band. The test script asserts nothing was templated in.

## Known constraints

- **`bidProcessor.replicas` must stay 1.** Auctions are not sharded across
  processor instances, so a second replica would drive every auction in
  parallel and publish duplicate winners. `NOTES.txt` warns if it is raised.
- **Reactive autoscaling is a floor, not a plan.** Auction load arrives in the
  final thirty seconds of a hot lot, and CPU-driven pods appear after the
  spike. KEDA with a cron trigger derived from auction end times is the real
  answer (architecture §7.4).
- **Never deployed to a real cluster.** The charts are linted, rendered,
  structurally validated and packaged, but no cluster was available here. The
  first `helm upgrade --install` is part of the work.
