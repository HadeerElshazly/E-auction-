# Keycloak realm — `eauction`

`eauction-realm.json` is the identity configuration for the platform. It is the
*shape* the services depend on, not a production realm: the dev users and the
direct-grant switch in it exist so the test suite and the smoke test can get a
token without a browser. Section "Before production" below lists everything
that must change.

## Why Keycloak and not Nafath directly

Nafath is the national identity provider. It authenticates a citizen and tells
us who they are; it does not issue tokens our services can validate, carry our
roles, or hold a session. Keycloak sits in front of it as the broker:

```
  bidder ──► Nafath (OIDC)  ──► Keycloak (eauction realm) ──► access token ──► our services
                                      │
  staff   ──► Keycloak local user ─────┘
```

Every service validates the same token offline against the realm's JWKS. No
service calls Keycloak per request, which is what keeps the bid catcher inside
its latency budget.

## What the realm defines

### Roles (realm roles, not client roles)

| Role | Who | What it opens |
|---|---|---|
| `bidder` | a citizen brokered from Nafath | subscribe, pay the deposit, fetch a signing key, bid |
| `auction-admin` | municipality staff | prepare an auction, publish it for approval |
| `award-committee` | the award committee | confirm an award, disqualify a winner, offer the next candidate |
| `operator` | platform operations, and the clerk on the floor | health and operational endpoints; a hall auction's signing key and hammer (§29) |
| `reporting` | municipality finance staff | التقارير, and nothing else — it cannot touch an auction (§35) |
| `auditor` | internal audit | the staff audit trail, and nothing else (§34) |

`auditor` is the one role here that must not be combined with another. It grants
no write anywhere, and the audit service has no endpoint that writes — but an
auditor who also holds `auction-admin` or `award-committee` is reading the record
of their own actions, which is most of the value of keeping it. Grant it to the
people who review the trail and to nobody who operates the platform.

They are realm roles because a bidder is a bidder across every client —
web, mobile, and the onsite terminal. Keycloak puts them under
`realm_access.roles`, and `EAuction.Security/JwtSetup.cs` projects that nesting
into .NET role claims. If you move these to client roles the projection stops
matching and every `[Authorize]` fails open-to-403.

### The extra claims, and why they live on the clients

Three protocol mappers are attached **directly to each client**, not to a
realm-level client scope:

| Mapper | Claim | On | Why |
|---|---|---|---|
| `oidc-audience-mapper` | `aud: eauction` | all three clients | the services set `ValidAudience = "eauction"`, so a token minted for another audience in the same realm is refused |
| `oidc-usermodel-attribute-mapper` | `national_id` | bidder clients | `POST /bidders/register` — the identity a bidder registers under |
| `oidc-usermodel-attribute-mapper` | `name_ar` | bidder clients | award letters, which are issued in Arabic |

Only the bidder clients carry the identity mappers. Staff are local Keycloak
users, not brokered citizens, so there is no national ID to map.

> **A realm-level `clientScopes` array replaces Keycloak's built-in scopes
> instead of adding to them.** An earlier version of this file defined the two
> custom scopes that way, and the realm came up without `basic` or `roles` — so
> tokens carried no `sub` and no `realm_access.roles`, and every `[Authorize]`
> in the platform would have failed. The import succeeded and said nothing; only
> decoding a real token showed it. Putting the mappers on the clients keeps
> Keycloak's own defaults intact.

`national_id` arrives as a claim and nowhere else. The registration endpoint
reads it from the token only — it used to accept one from the request body as a
fallback, which meant any valid token could register under any national ID.

### Clients

| Client | Type | Redirect URIs | Claims |
|---|---|---|---|
| `bidder-web` | public, PKCE S256 | `http://localhost:3000/*`, `https://mazad.jeddah.gov.sa/*` | audience + Nafath identity |
| `bidder-mobile` | public, PKCE S256 | `eauction://callback`, `http://localhost:19006/*` | audience + Nafath identity |
| `admin-web` | public, PKCE S256 | `http://localhost:3001/*`, `https://mazad-admin.jeddah.gov.sa/*` | audience |

All three are public with PKCE required (`S256`). A public client cannot hold a
secret, so PKCE is what stops an intercepted authorization code from being
redeemed by someone else.

### Dev users

`sara`, `khalid` (`bidder`, each with `national_id` and `name_ar`),
`admin-user` (`auction-admin`), `committee-user` (`award-committee`),
`clerk-user` (`operator`), `reporting-user` (`reporting`), `auditor-user` (`auditor`).
Password `dev-only-password`.

`auditor-user` holds `auditor` and nothing else, on purpose: the separation §34
asks for is only real if the seeded accounts respect it too, and an audit trail
that the dev administrator could read would make the smoke test's refusal
assertions pass for the wrong reason.

### Changing the realm means restarting Keycloak

The realm is imported on startup into a database `run-local.sh` wipes first, so an
edit to `eauction-realm.json` has no effect on an instance that is already running —
and `tools/smoke/run-smoke.sh --with-deps` deliberately leaves a running one alone,
because wiping it resets every bidder's subject id mid-session. A new role or dev
user therefore needs:

```bash
pkill -f "[k]c.sh start-dev"
```

before the next run. The symptom otherwise is `invalid_grant` for a user that is
plainly in the file.

## Running it locally

```bash
KEYCLOAK_HOME=/path/to/keycloak deploy/keycloak/run-local.sh
```

Download a distribution from
<https://github.com/keycloak/keycloak/releases> and extract it to
`$KEYCLOAK_HOME` first. The script wipes the dev database before importing,
because **a realm import is not idempotent**: against an existing realm
Keycloak logs "already exists", keeps the old one, and a change to the realm
file silently does not take effect. It also rejects a realm file carrying any
field Keycloak does not know (a key added as a comment, for instance) before
spending 40 seconds on a boot that would fail anyway.

Discovery document:

```
http://localhost:8080/realms/eauction/.well-known/openid-configuration
```

Get a token the way the smoke test does:

```bash
curl -s -X POST http://localhost:8080/realms/eauction/protocol/openid-connect/token \
  -d grant_type=password -d client_id=bidder-web \
  -d username=sara -d password=dev-only-password | python3 -m json.tool
```

Point the services at it:

```
EAuction__Jwt__Authority=http://localhost:8080/realms/eauction
EAuction__Jwt__Audience=eauction
```

## In a cluster

The chart does **not** deploy Keycloak. On-prem, identity is a shared service
that outlives any one application, and an auction platform that owns the realm
also owns every outage of it. The chart points at whatever issuer the
municipality runs:

```yaml
# deploy/helm/e-auction/values.yaml
config:
  jwt:
    authority: https://id.example.sa/realms/eauction
    audience: eauction
```

`eauction-realm.json` is the artifact an operator imports into that Keycloak
once, through the admin console or `kcadm.sh`, after working the
"Before production" list below. The three services then read the realm's JWKS
and validate offline; nothing calls Keycloak per request, so its availability
is not the auction's availability.

## Wiring Nafath in

Nafath is **not** in the committed realm, because configuring it needs a client
id and secret issued by Elm/NIC under a signed contract, and a secret must not
land in git. Add it as an OIDC identity provider once you hold those:

```json
{
  "alias": "nafath",
  "providerId": "oidc",
  "enabled": true,
  "trustEmail": false,
  "firstBrokerLoginFlowAlias": "first broker login",
  "config": {
    "clientId":     "<from Elm/NIC>",
    "clientSecret": "<from Elm/NIC — a sealed secret, never this file>",
    "authorizationUrl": "<Nafath authorize endpoint>",
    "tokenUrl":         "<Nafath token endpoint>",
    "jwksUrl":          "<Nafath JWKS endpoint>",
    "useJwksUrl": "true",
    "validateSignature": "true",
    "defaultScope": "openid profile national_id",
    "pkceEnabled": "true",
    "pkceMethod": "S256"
  }
}
```

It then needs identity-provider mappers so a brokered login populates the two
user attributes the `nafath-identity` scope reads:

| Nafath claim | → user attribute | mapper |
|---|---|---|
| the national ID claim Nafath issues | `national_id` | `oidc-user-attribute-idp-mapper` |
| the Arabic full-name claim | `name_ar` | `oidc-user-attribute-idp-mapper` |

Set both mappers to **force** sync mode so a name or ID corrected at the
national registry propagates on the next login instead of staying frozen at
first login.

Also attach the `bidder` role to brokered users — either with a
`oidc-hardcoded-role-idp-mapper` on the provider, or as a realm default role.
Without it a citizen authenticates successfully and is then refused by every
bidder endpoint.

### The two-factor step-up

Nafath's 2-digit confirmation is a separate, stronger assertion than the
redirect login. The design uses the redirect for ordinary login and requires the
2-digit step-up wherever money or land moves: binding a national identity, paying
the deposit, lodging a bank guarantee, and awarding an auction. The flow, the
services' half of it and the four measured facts behind both are in
`docs/ARCHITECTURE.md` §25.

**In the realm.** `acr.loa.map` is `{"low": 1, "high": 2}`, so a token says
`"high"`, not `2`. The browser flow `eauction-browser` is Keycloak's documented
step-up shape: `auth-cookie` and a forms subflow as alternatives, and inside the
forms subflow two CONDITIONAL subflows, each guarded by a
`conditional-level-of-authentication` condition — level 1 holds the password
form, level 2 holds the second factor. A client asks for a level with
`acr_values=high`.

**The second factor is an OTP authenticator, standing in for Nafath.** It has the
same shape — an out-of-band confirmation of a login already in progress — and it
plugs into the same slot once the Elm/NIC contract exists. `sara`, `khalid` and
`committee-user` are seeded with the TOTP secret `eauctiondevsecret1234567890`,
which is how the tests drive a second factor without a device. It is a dev
credential in a dev realm; see the production checklist below.

Three behaviours are worth knowing before you debug this realm:

- **A password grant is always level 1.** `grant_type=password` ignores
  `acr_values`, and sending the one-time code does not raise the level. A
  stepped-up token can only be minted through a browser — `web/e2e/stepup-token.mjs`
  does it.
- **A user holding a TOTP credential must send `otp` on every grant**, direct
  grants included, and a code is single-use: two authentications for the same
  user inside one 30-second window cannot both succeed.
- **Stepping up re-prompts for the password.** The conditional condition compares
  against the level reached by the authentication in progress, not the level the
  SSO session holds, so a signed-in user is asked for their password (username
  hidden) and then the second factor. This is Keycloak's behaviour on its own
  documented flow, and it is left alone: a stolen session cookie cannot step
  itself up.

## Before production

- [ ] `directAccessGrantsEnabled: false` on all three clients. The password
      grant exists only so the smoke test and the integration tests can get a
      token without driving a browser. Leaving it on in production means a
      stolen password is a token, with no Nafath in the path.
- [ ] Delete every dev user (`sara`, `khalid`, `admin-user`, `committee-user`,
      `clerk-user`, `reporting-user`, `auditor-user`).
      They carry a known password, and three of them carry a known TOTP secret —
      which, until Nafath is wired in, is the whole of the second factor guarding
      deposits and awards.
- [ ] Narrow the redirect URIs and web origins. The committed ones include
      `localhost` entries for development and a trailing `/*` wildcard on the
      production hosts, and `webOrigins` is `+`. Pin each to the exact callback
      path the portal uses: a wildcard redirect URI is an open redirect that
      leaks authorization codes.
- [ ] `sslRequired: all` (the file ships `external`, which permits plain HTTP
      from inside the cluster).
- [ ] Nafath identity provider configured, with its secret from a sealed secret
      or the cluster's secret store — never from this file.
- [ ] Staff accounts (`auction-admin`, `award-committee`, `operator`, `reporting`,
      `auditor`) federated
      to the municipality's own directory. The award committee already needs a
      second factor to award, but it is the dev OTP credential; a real one has to
      come from the directory.
- [ ] A production-grade database behind Keycloak. `start-dev` keeps the realm
      in an embedded H2 file that is not meant to survive.
- [ ] `registrationAllowed` stays `false`. Self-registration would let a bidder
      assert their own national ID, which is exactly what Nafath exists to
      prevent.
