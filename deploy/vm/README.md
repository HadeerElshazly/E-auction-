# The demo on the Azure VM

```
host   auctions.westus2.cloudapp.azure.com  (48.202.57.47)
user   auctionvmuser
size   4 vCPU · 16 GiB
```

Docker Compose, not Kubernetes. One host, twenty containers, one hostname.

## What you have to do

### One GitHub secret

| Secret | Value |
|---|---|
| `VM_SSH_KEY` | the whole `AuctionVM_key.pem`, including both `-----` lines |

Settings → Secrets and variables → Actions. Paste it there and nowhere else.

There is a second, optional one: **`VM_SSH_KNOWN_HOSTS`**. Without it the
workflow accepts the VM's host key on first use, which cannot detect an
impostor on that first connection. The reachability job prints the line to put
in it — set it afterwards and the check becomes strict.

That is the whole list. No registry credential, because the images are built on
the VM itself; no database password, because `deploy.sh` generates one.

### Two firewall rules

| Port | |
|---|---|
| 80 | bidder portal, the eight APIs, Keycloak, sandbox |
| 8081 | admin portal |

**Scope them to your own address range, not `0.0.0.0/0`.** This stack runs a
payment simulator that settles every charge without taking a riyal, and seeded
auctions with real-looking figures.

Compose publishes the other services' ports too (5080, 5090, …) and an override
cannot withdraw them — Compose concatenates `ports` across files rather than
replacing them. The network security group is therefore the only thing keeping
them private. Do not open a range.

## Running it

**From Actions** — `vm-deploy`, with **“Only test SSH”** ticked the first time.
That answers the one open question (whether a GitHub runner can reach port 22)
in twenty seconds instead of after a twenty-minute build.

Then run it again with **“Install Docker”** ticked for the first real deploy.

**From your own terminal**, if the runner cannot reach the VM:

```bash
ssh -i AuctionVM_key.pem auctionvmuser@48.202.57.47
git clone <repo> e-auction && cd e-auction
bash deploy/vm/bootstrap.sh     # once: Docker, kernel limits, log caps
exec newgrp docker              # so the docker group applies without logging out
bash deploy/vm/deploy.sh
```

## Addresses

```
bidder portal   http://auctions.westus2.cloudapp.azure.com/
admin portal    http://auctions.westus2.cloudapp.azure.com:8081/
sandbox         http://auctions.westus2.cloudapp.azure.com/sandbox/
keycloak        http://auctions.westus2.cloudapp.azure.com/auth/
edge health     http://auctions.westus2.cloudapp.azure.com/edge/health
```

**Open the sandbox first.** The payment panel lives there, and it is where a
charge is settled or refused. Step-up is off in this configuration
(`StepUp__Enabled: false`, inherited from the compose file), so no six-digit
code is needed — but the payment side still runs through the simulator.

Accounts come from the realm. The Keycloak admin password is in
`deploy/vm/.env` on the VM:

```bash
grep KEYCLOAK_ADMIN_PASSWORD deploy/vm/.env
```

## How it is put together

**An edge, not eleven ports.** `nginx-edge.conf` serves the bidder portal at
`/`, the eight APIs under `/api/`, Keycloak at `/auth/` and the sandbox at
`/sandbox/`. Eleven public ports would mean eleven firewall rules, eleven
origins in every CORS list, and bundles with port numbers baked in.

**The admin portal gets its own port** rather than a path, because neither
portal sets Vite's `base`: served under `/admin/` a single-page app asks for
`/assets/...` and gets the bidder's `index.html` back. One extra firewall rule
is cheaper than two differently-built bundles.

**The portals are rebuilt for this host.** Vite inlines the API URLs and the
Content-Security-Policy is generated from the same table in the same build
(D-43), so a bundle built for localhost cannot be repointed at runtime. The
image is specific to the environment, by design.

**Keycloak moves to `/auth`.** `KC_HTTP_RELATIVE_PATH` matches the edge, so the
`iss` in every token is the public issuer. Each service still fetches signing
keys over the container network (`Jwt__Authority`) and *also* accepts the public
issuer (`Jwt__Issuer`) — the two are additive, which is what makes this work at
all. It is the same mechanism the local stack uses for `localhost` vs
`keycloak`.

**`.env` is generated once and kept.** `deploy.sh` writes it on first run and
leaves it alone afterwards, and the workflow's rsync excludes it explicitly.
Two of those keys cannot be rotated casually: the document grant key
invalidates every grant already minted, so no bidder could open the booklet
they paid for, and the bidder master key makes every signing secret a browser
holds wrong, so the next bid comes back `BadSignature`.

## What has been checked, and what has not

Checked here, with real tools:

- `nginx -t` on the edge configuration — valid
- `docker compose config` on the merged base + override — valid, and asserted
  to contain no remaining `localhost`, the right issuer/authority split, the
  right CORS origins, and only 80 and 8081 to open
- `bash -n` on both scripts
- the application itself: 659 .NET tests and 43 web tests on this commit

Not checked, because it needs the VM:

- **whether a GitHub runner can reach port 22 at all.** The authoring session
  could not — outbound 22 is blocked there, confirmed against a second host —
  so this is genuinely unknown until the reachability job runs.
- the Keycloak realm import under the `/auth` prefix, and whether the realm's
  clients carry redirect URIs matching this hostname. If login bounces back to
  a `localhost` address, that is the first place to look.
- the build on four vCPUs. Twenty minutes is the estimate, not a measurement.
- disk. Fourteen images plus layers is perhaps 12 GiB; check `df -h` before the
  first run.
