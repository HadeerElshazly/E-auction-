/**
 * Mints a stepped-up access token by completing a real browser login.
 *
 *   node web/e2e/stepup-token.mjs sara bidder-web
 *
 * Prints the access token on stdout and nothing else, so a shell can capture it.
 *
 * This exists because a stepped-up token cannot be obtained any other way. Keycloak
 * always issues level 1 for a password grant — `acr_values` is ignored on it and an
 * OTP does not raise the level (measured; see docs/ARCHITECTURE.md §25) — so the
 * authorization-code flow is the only route to a second factor. That is not a gap in
 * Keycloak: an API client genuinely cannot confirm that a human is present, which is
 * the entire point of the gate. Handing the API walk-through a token obtained the way
 * a human obtains one is the honest model of that.
 */
import { chromium } from '@playwright/test'
import { createHash, createHmac, randomBytes } from 'node:crypto'

const [user = 'sara', client = 'bidder-web'] = process.argv.slice(2)

const ISSUER = process.env.SMOKE_ISSUER ?? 'http://localhost:8080/realms/eauction'
const PASSWORD = process.env.SMOKE_PASSWORD ?? 'dev-only-password'
const SECRET = process.env.SMOKE_TOTP_SECRET ?? 'eauctiondevsecret1234567890'
const CHROMIUM = process.env.CHROMIUM_PATH ?? '/opt/pw-browsers/chromium'

// Must be a redirect URI registered on the client, but nothing needs to serve it:
// the page is intercepted below, because all that is wanted is the code in the URL.
const REDIRECT = client === 'admin-web' ? 'http://localhost:3001/' : 'http://localhost:3000/'

function b64u(buf) {
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function totp(secret) {
  const buf = Buffer.alloc(8)
  buf.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30_000)))
  const mac = createHmac('sha1', Buffer.from(secret, 'ascii')).update(buf).digest()
  const offset = mac[mac.length - 1] & 0x0f
  return String((mac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0')
}

const browser = await chromium.launch({
  executablePath: CHROMIUM,
  args: ['--no-sandbox', '--disable-dev-shm-usage'],
})

try {
  const context = await browser.newContext()
  const page = await context.newPage()

  // Nothing serves the redirect target, and a failed navigation loses the code.
  await page.route(`${REDIRECT}**`, (route) =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<html></html>' }),
  )

  const verifier = b64u(randomBytes(32))
  const params = new URLSearchParams({
    client_id: client,
    redirect_uri: REDIRECT,
    response_type: 'code',
    scope: 'openid profile email',
    state: b64u(randomBytes(8)),
    code_challenge: b64u(createHash('sha256').update(verifier).digest()),
    code_challenge_method: 'S256',
    // The whole point: ask for the level the services require.
    acr_values: process.env.SMOKE_STEPUP_ACR ?? 'high',
  })

  await page.goto(`${ISSUER}/protocol/openid-connect/auth?${params}`)

  await page.locator('#username').fill(user)
  await page.locator('#password').fill(PASSWORD)
  await page.locator('#kc-login, input[type="submit"]').first().click()

  // The second factor. Required here by the realm's level-2 subflow; absent would
  // mean the step-up flow is not wired, so this fails loudly rather than returning
  // a token that only looks stepped up.
  const otp = page.locator('#otp, input[name="otp"]')
  await otp.waitFor({ state: 'visible', timeout: 15_000 })
  await otp.fill(totp(SECRET))
  await page.locator('#kc-login, input[type="submit"]').first().click()

  await page.waitForURL((url) => url.href.startsWith(REDIRECT), { timeout: 20_000 })
  const code = new URL(page.url()).searchParams.get('code')
  if (!code) throw new Error('no authorization code came back')

  const response = await fetch(`${ISSUER}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code',
      client_id: client,
      redirect_uri: REDIRECT,
      code,
      code_verifier: verifier,
    }),
  })

  const body = await response.json()
  if (!body.access_token) throw new Error(`token exchange failed: ${JSON.stringify(body)}`)

  // Verified here rather than left to the caller: a token silently at the wrong
  // level would make the smoke test's step-up assertions pass for the wrong reason.
  const claims = JSON.parse(
    Buffer.from(body.access_token.split('.')[1], 'base64url').toString(),
  )
  const wanted = process.env.SMOKE_STEPUP_ACR ?? 'high'
  if (claims.acr !== wanted) {
    throw new Error(`token came back with acr "${claims.acr}", wanted "${wanted}"`)
  }
  if (typeof claims.auth_time !== 'number') {
    throw new Error('token carries no auth_time, so its freshness cannot be checked')
  }

  process.stdout.write(body.access_token)
} finally {
  await browser.close()
}
