# The demo on the Azure VM

```
host   auctions.westus2.cloudapp.azure.com  (48.202.57.47)
user   auctionvmuser
size   4 vCPU · 16 GiB
```

Docker Compose, not Kubernetes. One host, twenty containers, one hostname.

## What you have to do

### Install a self-hosted runner on the VM, once

This replaces SSH entirely. The runner polls GitHub outbound, so **no inbound
port is opened**, and **no SSH key goes into GitHub secrets** — that credential
stops existing.

On GitHub: **Settings → Actions → Runners → New self-hosted runner**, Linux x64.
It shows you a `curl` and a `./config.sh` with a registration token. Then on the
VM:

```bash
ssh -i AuctionVM_key.pem auctionvmuser@48.202.57.47
mkdir ~/actions-runner && cd ~/actions-runner
# ... the curl and ./config.sh lines GitHub gave you ...

sudo ./svc.sh install
sudo usermod -aG docker "$USER"     # the runner must reach the Docker socket
sudo ./svc.sh start
```

The `usermod` matters: without it every build fails on a permission error
against `/var/run/docker.sock`. The preflight step checks for it and says so
plainly rather than letting a build discover it.

**Secrets required: none.** No registry credential (images build on the VM), no
database password (`deploy.sh` generates one), no SSH key (there is no SSH).

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

Port 22 does **not** need opening for the deploy. Keep it closed, or scoped to
your own address for your own use.

## Running it

**From Actions** — `vm-deploy`. Run it the first time with **“Only check the
VM”** ticked: it reports the VM's size, free disk, Docker version and whether
the runner can reach the socket, and deploys nothing. Thirty seconds to find
out whether the host is ready.

Then run it with **“Install Docker”** ticked for the first real deploy.

**From your own terminal**, which needs no runner at all:

```bash
ssh -i AuctionVM_key.pem auctionvmuser@48.202.57.47
git clone <repo> e-auction && cd e-auction
git checkout feat/portal-redesign
bash deploy/vm/bootstrap.sh     # once: Docker, kernel limits, log caps
exec newgrp docker              # so the docker group applies without logging out
bash deploy/vm/deploy.sh
```

Both paths run the same `deploy.sh`, so neither is a special case of the other.

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

Accounts come from the realm; the Keycloak admin password is in
`~/.config/eauction/.env` on the VM.

## Two pieces of state that must not move

**`~/.config/eauction/.env`**, outside the checkout. A self-hosted runner cleans
its workspace between runs, so a `.env` inside it would be regenerated on every
deploy — and regenerating the document grant key invalidates every grant already
minted, while regenerating the bidder master key makes every signing secret a
browser holds wrong, so the next bid returns `BadSignature`. Keeping it in the
home directory means it survives the checkout, the runner and a re-clone.

**`COMPOSE_PROJECT_NAME=eauction`**, pinned rather than derived. Compose takes
the project name from the working directory by default, and the named volumes
carry it as a prefix: `postgres-data` becomes `<project>_postgres-data`. A
runner's workspace path is not something this repository controls, so a path
that changed would silently create a *second* set of volumes — an empty
database, an empty object store, and the old data still on disk under a name
nothing refers to.

To read the Keycloak admin password:

```bash
grep KEYCLOAK_ADMIN_PASSWORD ~/.config/eauction/.env
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

- the Keycloak realm import under the `/auth` prefix, and whether the realm's
  clients carry redirect URIs matching this hostname. **This is the most likely
  thing to fail.** If login bounces to a `localhost` address, that is where to
  look — `deploy/keycloak/eauction-realm.json`.
- the build on four vCPUs. Twenty minutes is an estimate, not a measurement.
- disk. Fourteen images plus layers is roughly 12 GiB; the preflight step
  refuses to start below 20 GiB free rather than dying part-way through.

Why none of this was run from the authoring session: outbound TCP is blocked
there for everything except proxied HTTP(S) to allow-listed hosts. Port 22
timed out identically to the VM, to GitHub and to Microsoft, and
`management.azure.com` is blocked too, so neither SSH nor the Azure API was
available. Opening port 22 on the VM would not have changed that — the
restriction is on this end.
