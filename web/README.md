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
it argues for a strict CSP in front of this bundle.

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
