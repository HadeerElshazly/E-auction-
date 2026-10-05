/**
 * The PKCE callback's idempotence.
 *
 * Run with:
 *   node --experimental-strip-types --test web/shared/src/auth.test.ts
 */
import assert from 'node:assert/strict'
import { afterEach, beforeEach, test } from 'node:test'
import {
  completeLogin,
  rememberPendingAction,
  resetAuthStateForTests,
  sessionFrom,
  silentLoginWasRefused,
  takePendingAction,
} from './auth.ts'

// A browser's worth of globals, just enough for the callback path. Lighter than a
// DOM library and it keeps what the module actually touches visible.
const store = new Map<string, string>()

function installBrowser(search: string) {
  const g = globalThis as unknown as Record<string, unknown>
  g.sessionStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  }
  g.location = { search, pathname: '/', origin: 'http://localhost:3001', assign() {} }
  g.history = { replaceState() {} }
}

let tokenCalls = 0

beforeEach(() => {
  store.clear()
  tokenCalls = 0
  resetAuthStateForTests()

  const header = Buffer.from(JSON.stringify({ alg: 'RS256' })).toString('base64url')
  const payload = Buffer.from(
    JSON.stringify({
      sub: '11111111-1111-1111-1111-111111111111',
      exp: Math.floor(Date.now() / 1000) + 900,
      preferred_username: 'sara',
      realm_access: { roles: ['bidder'] },
    }),
  ).toString('base64url')
  const jwt = `${header}.${payload}.signature`

  globalThis.fetch = (async (input: string | URL) => {
    const url = String(input)
    if (url.includes('.well-known')) {
      return new Response(
        JSON.stringify({
          authorization_endpoint: 'http://kc/auth',
          token_endpoint: 'http://kc/token',
        }),
        { status: 200 },
      )
    }
    if (url.includes('/token')) {
      tokenCalls++
      // An authorization code is single-use: the second attempt is refused, which
      // is what makes a double exchange destructive rather than merely wasteful.
      if (tokenCalls > 1) {
        return new Response(JSON.stringify({ error: 'invalid_grant' }), { status: 400 })
      }
      return new Response(JSON.stringify({ access_token: jwt }), { status: 200 })
    }
    throw new Error(`unexpected fetch: ${url}`)
  }) as typeof fetch
})

afterEach(() => {
  const g = globalThis as unknown as Record<string, unknown>
  delete g.sessionStorage
  delete g.location
  delete g.history
})

const config = {
  issuer: 'http://kc/realms/eauction',
  clientId: 'admin-web',
  redirectUri: 'http://localhost:3001/',
}

test('a normal page load with no code does nothing', async () => {
  installBrowser('')
  assert.equal(await completeLogin(config), null)
  assert.equal(tokenCalls, 0)
})

test('two concurrent callbacks share one token exchange', async () => {
  // StrictMode runs every effect twice. Before the in-flight guard, the second call
  // found the verifier already consumed and reported "the login response did not
  // match this tab", throwing away a login that had in fact succeeded.
  installBrowser('?code=the-code&state=the-state')
  store.set('eauction.pkce.verifier', 'the-verifier')
  store.set('eauction.pkce.state', 'the-state')

  const [first, second] = await Promise.all([completeLogin(config), completeLogin(config)])

  assert.equal(tokenCalls, 1, 'the code must be redeemed exactly once')
  assert.equal(first?.subject, '11111111-1111-1111-1111-111111111111')
  assert.deepEqual(second, first, 'both callers get the same session')
  assert.deepEqual(first?.roles, ['bidder'])
})

test('a sequential second callback on the same code does not re-exchange', async () => {
  installBrowser('?code=the-code&state=the-state')
  store.set('eauction.pkce.verifier', 'the-verifier')
  store.set('eauction.pkce.state', 'the-state')

  const first = await completeLogin(config)
  const second = await completeLogin(config)

  assert.equal(tokenCalls, 1)
  assert.deepEqual(second, first)
})

test('a mismatched state is refused', async () => {
  // Without this check a third party could hand someone a link carrying their own
  // authorization code and have the tab sign in as them.
  installBrowser('?code=the-code&state=not-the-state')
  store.set('eauction.pkce.verifier', 'the-verifier')
  store.set('eauction.pkce.state', 'the-state')

  await assert.rejects(() => completeLogin(config), /did not match this tab/)
  assert.equal(tokenCalls, 0, 'a mismatched state must not reach the token endpoint')
})

test('the claims Keycloak nests are read from where it puts them', async () => {
  const header = Buffer.from(JSON.stringify({ alg: 'RS256' })).toString('base64url')
  const payload = Buffer.from(
    JSON.stringify({
      sub: '22222222-2222-2222-2222-222222222222',
      exp: 1800000000,
      name: 'Sara Al-Harbi',
      national_id: '1012345678',
      name_ar: 'سارة الحربي',
      realm_access: { roles: ['bidder', 'operator'] },
    }),
  ).toString('base64url')

  const session = sessionFrom(`${header}.${payload}.sig`)

  // Flat `roles` would be the obvious guess and would be wrong: Keycloak nests
  // realm roles, and the services project them from the same place.
  assert.deepEqual(session.roles, ['bidder', 'operator'])
  assert.equal(session.nationalId, '1012345678')
  assert.equal(session.nameAr, 'سارة الحربي')
  assert.equal(session.subject, '22222222-2222-2222-2222-222222222222')
})

test('a refused silent login is an answer, not an error', async () => {
  // Keycloak's reply to prompt=none when it has no session for this browser. The
  // portal must show its sign-in button, not an error banner saying login_required.
  installBrowser('?error=login_required')

  assert.equal(await completeLogin(config), null)
  assert.equal(silentLoginWasRefused(), true)
  assert.equal(tokenCalls, 0)
})

test('a real authorization error is still an error', async () => {
  installBrowser('?error=invalid_scope&error_description=bad+scope')

  assert.equal(silentLoginWasRefused(), false)
  await assert.rejects(() => completeLogin(config), /invalid_scope/)
})

test('the pending action survives being read twice', () => {
  // React's StrictMode invokes a state initialiser twice, and the natural place to
  // read this is a state initialiser. A plain read-and-clear would hand the label
  // to one invocation and null to the other, and the portal would come back from a
  // step-up with no sign that anything had happened.
  installBrowser('')
  rememberPendingAction('سداد التأمين')

  assert.equal(takePendingAction(), 'سداد التأمين')
  assert.equal(takePendingAction(), 'سداد التأمين')

  // Read once from storage, and cleared there, so the next page load starts clean.
  assert.equal(store.get('eauction.stepup.pending'), undefined)
})

test('a page load that did not follow a step-up has nothing pending', () => {
  installBrowser('')

  assert.equal(takePendingAction(), null)
  assert.equal(takePendingAction(), null)
})

test('a second step-up in the same page load is remembered again', () => {
  // The label is read when the portal mounts; a step-up started afterwards has to
  // replace it rather than be swallowed by the answer already given.
  installBrowser('')
  rememberPendingAction('التسجيل بالهوية الوطنية')
  assert.equal(takePendingAction(), 'التسجيل بالهوية الوطنية')

  rememberPendingAction('تأكيد الترسية')
  assert.equal(takePendingAction(), 'تأكيد الترسية')
})
