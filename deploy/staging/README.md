# Staging deploy — namespace `e-auctions`

A demo of the whole system on the shared k3s node, driven from GitHub Actions
because the cloud session that wrote it has outbound port 22 blocked and cannot
reach the cluster itself.

Run it from **Actions → staging-deploy → Run workflow**. Tick
**“Only test the tunnel”** for the first run.

## What the owner has to add

### Five repository secrets

| Secret | What it is |
|---|---|
| `STAGING_SSH_KEY` | the private key from the access note, whole, including both `-----` lines |
| `STAGING_SSH_KNOWN_HOSTS` | the one `known_hosts` line for 40.64.120.210 |
| `STAGING_KUBECONFIG` | the kubeconfig, **base64-encoded** (`base64 -w0 < kubeconfig`) |
| `ACR_USERNAME` | a registry identity that may push and pull |
| `ACR_PASSWORD` | its password or token |

Settings → Secrets and variables → Actions → New repository secret. Paste the
values there and nowhere else: not in a chat, not in an issue, not in a file in
this repository.

`ACR_USERNAME`/`ACR_PASSWORD` are a long-lived credential, which is the one
weak point in this setup. A federated OIDC identity (`azure/login`, then
`az acr login`) removes it for pushing, and is worth doing once the first
deploy has proved the rest works. The pull secret inside the cluster still
needs a username and password, so that part stays.

### Four firewall rules

The services answer on NodePorts, and nothing outside the VM reaches them until
the Azure network security group allows it:

| Port | |
|---|---|
| 30080 | bidder portal |
| 30081 | admin portal |
| 30082 | Keycloak |
| 30083 | API gateway |

Source should be the office or VPN range, not `0.0.0.0/0` — this demo has a
payment simulator in it.

## What the workflow does

1. **`tunnel`** — a bare TCP probe of port 22, then the tunnel, then asks the
   API server its version, checks we can create deployments, prints the quota,
   and confirms `sdaia-archive` answers `Forbidden`. It fails if that namespace
   is *readable*, because a token wider than the access note describes is a
   problem to report rather than to use.
2. **`build`** — `dotnet test`, then `npm ci` + typecheck + unit tests, then
   fourteen images to ACR tagged with the commit sha.
3. **`deploy`** — `helm lint` and a full `helm template` first, with two
   assertions on the rendered output: no cluster-scoped object, and no Ingress.
   Then secrets, the realm, the dependencies, the pull secret, and
   `helm upgrade --install --wait`.

## Why it is shaped like this

**The quota is the design.** `requests.cpu` is capped at 3 and `limits.cpu` at
6 for everything we run. Every container declares its own limits, because the
LimitRange hands an undeclared one a 1-CPU limit and sixteen of those against a
6-core cap means nothing schedules at all. The arithmetic is a table in
`values-staging.yaml`: 2 500m requests, 5 500m limits, 500m of headroom each
way. Adding a service means redoing that table.

**Five NodePorts, eight APIs.** Hence `api-gateway.yaml`: one nginx on path
prefixes in front of all eight, so the total is four ports. Keycloak keeps its
own, because the `iss` in a token is whatever host Keycloak was called on, and
putting it behind a path would make the issuer a gateway URL the services would
then have to dial from inside the cluster.

**No operator, no CRD.** The archive tenant runs a Strimzi operator and a second
one would collide with their CRDs, so Kafka here is a plain single-broker KRaft
deployment.

**Secrets are generated in the cluster, once.** `secrets.sh` creates only what
is missing. Three of the keys cannot be rotated casually: the document grant key
invalidates every grant already issued, the bidder master key makes every
browser-held signing key wrong, and the Postgres password is baked into five
connection strings. A re-run is a no-op.

**The payment simulator is on.** It settles every charge without taking a
riyal. That is correct for a firewalled demo with seeded data and catastrophic
anywhere else — on the Jeddah install the flag is deliberately absent and the
service refuses to start without a real gateway. It must never be copied into
`values-jeddah.yaml`.

## What has never been tested

Honestly: the deploy path, end to end. The charts have been linted and rendered
structurally, but **no `helm` has ever run against them** — the authoring
session could not install one, which is why `helm lint` and `helm template`
are the first two things the deploy job does.

Specifically unproven:

- whether a GitHub runner can open port 22 to the VM at all (hence the
  tunnel-only run);
- the Keycloak realm import under `start-dev`, and whether the clients in it
  carry redirect URIs matching the NodePort origins;
- Kafka's advertised listener — set to the in-cluster DNS name, which is right
  for in-cluster clients and means nothing outside the cluster can reach it
  (fine: nothing should);
- whether 15 minutes is enough for `--wait` on a cold single node pulling
  fourteen images.

Expect the first run to surface something. The ordering is built so that it
surfaces cheaply.
