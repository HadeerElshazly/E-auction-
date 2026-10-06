/**
 * The Content-Security-Policy, which nothing else can test.
 *
 * The browser walk-through drives the dev server, and the policy is injected only
 * at build — so a policy that was wrong, or missing, or quietly permitted
 * `'unsafe-inline'` would leave every other test in this repository green. These
 * assertions are the only thing standing between the portals and a policy that
 * looks strict and is not.
 *
 * Run with:
 *   node --experimental-strip-types --test web/shared/src/csp.test.ts
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  buildPolicy,
  connectOrigins,
  contentSecurityPolicy,
  injectPolicy,
  originOf,
} from '../vite-csp.ts'

const PRODUCTION_ENV = {
  VITE_ISSUER: 'https://id.jeddah.gov.sa/realms/eauction',
  VITE_ADMIN_API: 'https://api.jeddah.gov.sa/auctions',
  VITE_PARTICIPANT_API: 'https://api.jeddah.gov.sa/participants',
  VITE_CATCHER_API: 'https://bids.jeddah.gov.sa',
  VITE_QUERY_API: 'https://api.jeddah.gov.sa/public',
  VITE_DOCUMENTS_API: 'https://api.jeddah.gov.sa/documents',
  VITE_NOTIFICATIONS_API: 'https://api.jeddah.gov.sa/notifications',
  VITE_AUDIT_API: 'https://api.jeddah.gov.sa/audit',
  VITE_REPORTING_API: 'https://api.jeddah.gov.sa/reports',
}

function directive(policy: string, name: string): string {
  const found = policy
    .split(';')
    .map((part) => part.trim())
    .find((part) => part === name || part.startsWith(`${name} `))

  assert.ok(found, `the policy has no ${name} directive: ${policy}`)
  return found
}

test('scripts may come only from the bundle itself', () => {
  // The directive the signing key depends on. 'unsafe-inline' here would let an
  // injected <script> read the key out of the heap and sign bids with it, which is
  // the exact attack useSigningKey.ts names and defers to this policy.
  const policy = buildPolicy(PRODUCTION_ENV)

  assert.equal(directive(policy, 'script-src'), "script-src 'self'")
  assert.ok(!policy.includes("'unsafe-eval'"))
})

test("only styles are allowed to be inline, and nothing else is", () => {
  const policy = buildPolicy(PRODUCTION_ENV)

  // One deliberate concession, because React style attributes are everywhere in
  // both portals. If a second one ever appears it should be a decision, not a
  // surprise, so this counts them.
  const inline = policy
    .split(';')
    .map((part) => part.trim())
    .filter((part) => part.includes("'unsafe-inline'"))

  assert.deepEqual(inline, ["style-src 'self' 'unsafe-inline'"])
})

test('connect-src is exactly the origins this build calls', () => {
  const origins = connectOrigins(PRODUCTION_ENV)

  assert.ok(origins.includes("'self'"))
  assert.ok(origins.includes('https://id.jeddah.gov.sa'))
  assert.ok(origins.includes('https://api.jeddah.gov.sa'))
  assert.ok(origins.includes('https://bids.jeddah.gov.sa'))

  // Five services behind one gateway are one origin. A policy that repeated it
  // would still work; one that failed to deduplicate would be read less carefully.
  assert.equal(origins.length, 4)

  // And no development fallback. A variable missing from a production build takes
  // its dev value from endpoints.ts, which would paste http://localhost into the
  // policy of a bundle served from a government domain — this is the assertion
  // that caught exactly that when the document service was added.
  assert.ok(!origins.some((origin) => origin.includes('localhost')))

  // And nothing else. An attacker's host is what connect-src exists to exclude.
  assert.ok(!origins.some((origin) => origin.includes('evil')))
})

test('a development build still names the development services', () => {
  // No environment at all: the fallbacks in endpoints.ts are what the portal will
  // call, so they are what the policy must permit. A policy derived from an empty
  // environment that still said 'self' only would break every local build silently.
  const origins = connectOrigins({})

  assert.ok(origins.includes('http://localhost:8080'))
  assert.ok(origins.includes('http://localhost:5103'))
})

test('a malformed endpoint is dropped rather than injected into the policy', () => {
  // A stray value would otherwise be pasted into the header, where a space turns
  // one directive into two and a semicolon ends the policy early.
  assert.equal(originOf('not a url'), null)
  assert.equal(originOf(undefined), null)
  assert.equal(originOf('https://x.example/a/b?c=d'), 'https://x.example')

  const origins = connectOrigins({ ...PRODUCTION_ENV, VITE_QUERY_API: 'nonsense; script-src *' })
  assert.ok(!origins.some((origin) => origin.includes('script-src')))
})

test('the fallback is closed', () => {
  const policy = buildPolicy(PRODUCTION_ENV)

  // default-src 'none' means a directive nobody thought of is refused, not allowed.
  assert.equal(directive(policy, 'default-src'), "default-src 'none'")

  // base-uri in particular: without it an injected <base> redirects every relative
  // script URL to another host while script-src 'self' still passes.
  assert.equal(directive(policy, 'base-uri'), "base-uri 'none'")
  assert.equal(directive(policy, 'object-src'), "object-src 'none'")
  assert.equal(directive(policy, 'form-action'), "form-action 'none'")
})

test('the policy is the first thing in the head', () => {
  const html = [
    '<!doctype html>',
    '<html lang="ar" dir="rtl">',
    '  <head>',
    '    <meta charset="UTF-8" />',
    '    <title>مزادات الأراضي</title>',
    '  </head>',
    '  <body><div id="root"></div></body>',
    '</html>',
  ].join('\n')

  const out = injectPolicy(html, "default-src 'none'")

  // Before the charset and the title: a meta policy governs only what the parser
  // meets after it, so one injected lower down protects less than it appears to.
  assert.ok(out.indexOf('Content-Security-Policy') < out.indexOf('charset'))
  assert.ok(out.includes('<meta http-equiv="Content-Security-Policy"'))
})

test('a page with no head is a build failure, not a page with no policy', () => {
  assert.throws(() => injectPolicy('<html><body></body></html>', "default-src 'none'"), /head/)
})

test('the plugin only applies to a build', () => {
  // The dev server's React Refresh preamble is an inline script. If this ever
  // applied in dev, `npm run dev` would stop working — and the temptation would
  // then be to add 'unsafe-inline' to script-src and ship it.
  const plugin = contentSecurityPolicy({ env: PRODUCTION_ENV })

  assert.equal(plugin.apply, 'build')
  assert.equal(plugin.transformIndexHtml.order, 'pre')

  const out = plugin.transformIndexHtml.handler('<html><head></head><body></body></html>')
  assert.ok(out.includes("script-src 'self'"))
})
