# The two portals

```
web/
├── shared/   OIDC with PKCE, the API client, money, the bid frame codec
├── admin/    :3001  auction preparation, committee approval, the award workflow
├── bidder/   :3000  the public catalogue, qualification, bidding
└── e2e/      a browser walk-through of both, against the real stack
```

React + Vite + TypeScript, one npm workspace. Arabic-first and RTL by default: the
realm's default locale is `ar`, the documents are Arabic and the award letters are
issued in Arabic, so LTR is the exception.

```bash
tools/smoke/run-portals.sh --with-deps --keep-up   # everything up, nothing driven
#   bidder  http://localhost:3000
#   admin   http://localhost:3001
# sara / khalid / admin-user / committee-user, password dev-only-password

tools/smoke/run-portals.sh --with-deps             # the same, then drive it
cd web && npm run build                            # typecheck + build both
node --experimental-strip-types --test web/shared/src/*.test.ts
```

## The parts worth knowing about

**The bid frame is built and signed in the browser.** `shared/src/bidFrame.ts` is a
second implementation of `src/EAuction.Core/BidFrame.cs`, which is a deliberate cost:
the signing key is the bidder's own, and a server that signed on their behalf would
destroy the evidential value of a signed bid — nobody could later tell a bid the
bidder made from one the platform made for them.

Both implementations are pinned to one committed vector,
`shared/src/__fixtures__/bid-frame-vector.json`, asserted from C#
(`tests/EAuction.Tests/FrameVectorTests.cs`) and from TypeScript
(`shared/src/bidFrame.test.ts`). The trap it exists to catch: `Guid.TryWriteBytes`
writes .NET's **mixed-endian** layout, not RFC 4122 order. Get it wrong and every
browser bid is refused as `InvalidSignature` with nothing in either codebase looking
wrong. The vector's GUIDs are chosen so no group equals its own reverse, and a test
asserts that property so nobody replaces them with palindromic ones.

**The signing key never touches storage.** `bidder/src/useSigningKey.ts` holds it in
a ref, fetches it on the first bid rather than at page load, and scopes it to one
auction. It is still a secret in a browser heap: an XSS on that page can bid as the
user while the tab lives. That is the cost of the bidder holding their own key, and
it is why the bundle ships a strict Content-Security-Policy.

**The CSP is built, not written.** `shared/vite-csp.ts` injects it into `index.html`
at build time, deriving `connect-src` from the same `shared/src/endpoints.ts` table
the runtime config reads — a hand-maintained second list would drift the first time
a service moved, and the failure would appear in a bidder's browser rather than in a
build. `script-src` is `'self'` with no `'unsafe-inline'` and no `'unsafe-eval'`,
which is the directive the signing key actually depends on. `style-src` keeps
`'unsafe-inline'` because both portals use React `style` attributes throughout; a
concession on styles is not a concession on scripts.

Two things a `<meta>` policy cannot do, and the portals are static bundles with no
server of ours in front of them:

- **`frame-ancestors` is ignored in a meta element.** Clickjacking protection needs
  a response header from whatever serves `dist/` — `Content-Security-Policy:
  frame-ancestors 'none'`, or `X-Frame-Options: DENY`. Not yet configured anywhere,
  because nothing in this repository serves these bundles.
- **`report-uri` is ignored too**, so violations are visible in a browser console
  and nowhere else.

The policy is injected only on a build, because the dev server's React Refresh
preamble is an inline script and a policy strict enough to be worth shipping would
stop `npm run dev` working. That leaves a gap the walk-through would not see, so
`tools/smoke/run-portals.sh --built` serves the built bundles instead of the dev
server and drives the same walk-through against them. A CSP violation surfaces as a
console error, which `watchPage` already collects and the test already asserts to be
empty — verified by building with a deliberately narrowed `connect-src` and watching
it fail.

**The access token never touches storage either.** `shared/src/useSession.ts` keeps
it in React state. A refresh therefore loses it, so a cold load tries `prompt=none`
once: Keycloak answers from its own session with no interaction, or says
`login_required` and the sign-in button is shown. The attempt is marked in
sessionStorage so a refusal cannot become a redirect loop.

**Both portals poll.** `bidder/src/useLivePrice.ts` reads the authoritative price
from the query BFF — every second in the last minute of a live auction, every ten
otherwise, every fifteen when the auction is not live. The push channel is not built
(`docs/ARCHITECTURE.md` §7.2); the shape here is what a push channel would deliver
unchanged, so swapping it means replacing the body of that one hook.

**What the portals cannot show.** The reserve price is absent from every read path by
design (D-23), so the admin editor's reserve box is write-only — blank means
"unchanged". The leading bidder is masked everywhere except the committee's award
panel (D-22), which needs the real identity because it signs a letter to a named
person.
