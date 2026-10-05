import { expect, type Page } from '@playwright/test'
import { createHmac } from 'node:crypto'

export const ADMIN_URL = process.env.ADMIN_URL ?? 'http://localhost:3001'
export const BIDDER_URL = process.env.BIDDER_URL ?? 'http://localhost:3000'
export const PASSWORD = process.env.SMOKE_PASSWORD ?? 'dev-only-password'

/**
 * The TOTP secret seeded on the dev users in `deploy/keycloak/eauction-realm.json`.
 *
 * It stands in for Nafath's two-digit confirmation, which has the same shape — an
 * out-of-band confirmation of a login already in progress — and plugs into the same
 * slot in the realm's step-up flow once the Elm/NIC contract exists. A known secret
 * means the second factor is drivable from a test without a device.
 */
export const TOTP_SECRET = process.env.SMOKE_TOTP_SECRET ?? 'eauctiondevsecret1234567890'

/** RFC 6238, the six-digit flavour Keycloak's OTP authenticator expects. */
export function totp(secret: string = TOTP_SECRET, at: number = Date.now()): string {
  const counter = Math.floor(at / 30_000)
  const buf = Buffer.alloc(8)
  buf.writeBigUInt64BE(BigInt(counter))

  const mac = createHmac('sha1', Buffer.from(secret, 'ascii')).update(buf).digest()
  const offset = mac[mac.length - 1]! & 0x0f
  const code = mac.readUInt32BE(offset) & 0x7fffffff
  return String(code % 1_000_000).padStart(6, '0')
}

/**
 * Completes an identity provider's step-up challenge, and says whether there was one.
 *
 * Returns whether a second factor was actually demanded, so a test can assert that
 * the gate engaged rather than silently passing when it failed to — which is how a
 * security test quietly stops testing anything.
 *
 * Keycloak asks for *two* things here, not one, and it took a measurement to find
 * out. Requesting a higher `acr_values` on an existing session re-runs the whole
 * ladder: the level reached by a previous authentication does not satisfy the
 * conditional Level-of-Authentication condition, which compares against the level
 * reached in the authentication *now in progress* — starting at zero. So the user
 * is asked to re-enter their password (the username field is absent: Keycloak knows
 * who they are) and only then for the second factor.
 *
 * That is more friction than the documentation implies and it is left alone, because
 * it is the safer behaviour: a stolen session cookie cannot step itself up. In
 * production the second factor is Nafath's two-digit confirmation in this same slot.
 */
export async function completeStepUp(page: Page): Promise<boolean> {
  // The challenge only arrives after the service has refused the action, so the
  // portal is still on its own origin for a moment after the click. Waiting for the
  // redirect first is the difference between "no second factor was demanded" and
  // "the demand had not arrived yet" — and reading the second as the first is how
  // this assertion would pass while the gate was wide open.
  try {
    await page.waitForURL((url) => url.pathname.includes('/realms/'), { timeout: 15_000 })
  } catch {
    return false
  }

  const deadline = Date.now() + 30_000
  let demandedSecondFactor = false

  while (Date.now() < deadline) {
    const otp = page.locator('#otp, input[name="otp"]')
    const password = page.locator('#password')

    if (await otp.count()) {
      await otp.fill(totp())
      await submitKeycloakForm(page)
      demandedSecondFactor = true
      continue
    }

    // Keycloak's re-authentication page, which carries no username field.
    if (await password.count()) {
      if (await page.locator('#username').count()) {
        throw new Error(
          'the identity provider asked who this is: the session was lost, not stepped up',
        )
      }
      await password.fill(PASSWORD)
      await submitKeycloakForm(page)
      continue
    }

    // No form on screen. Either the challenge is still arriving, or it is over.
    if (!onIdentityProvider(page)) return demandedSecondFactor
    await page.waitForTimeout(200)
  }

  return demandedSecondFactor
}

function onIdentityProvider(page: Page): boolean {
  return page.url().includes('/realms/')
}

async function submitKeycloakForm(page: Page): Promise<void> {
  const submit = page.locator('#kc-login, input[type="submit"]').first()
  await submit.click()
  // The click navigates. Waiting for the button to go stops the next iteration
  // reading the form that was just submitted and filling it a second time.
  await submit.waitFor({ state: 'detached', timeout: 15_000 }).catch(() => {})
}

/**
 * Signs in through Keycloak's own login form.
 *
 * Deliberately not a token injected into the page: the redirect, the PKCE exchange
 * and the redirect URI registered on the client are three of the things most likely
 * to be wrong, and none of them is exercised by a test that skips the form.
 */
export async function signIn(page: Page, portal: string, username: string): Promise<void> {
  await page.goto(portal)
  await page.getByRole('button', { name: /الدخول|تسجيل الدخول/ }).first().click()

  await page.waitForURL(/\/realms\/eauction\/protocol\/openid-connect\/auth/, { timeout: 30_000 })
  await page.locator('#username').fill(username)
  await page.locator('#password').fill(PASSWORD)
  await page.locator('#kc-login, input[type="submit"]').first().click()

  // An ordinary sign-in asks for no acr_values and Keycloak demands no second
  // factor for it — measured, see completeStepUp. A step-up is a separate journey,
  // started by a service refusing an action, not by the sign-in button.

  await page.waitForURL((url) => url.origin === new URL(portal).origin, { timeout: 30_000 })
  await expect(page.locator('header.bar .who')).toBeVisible({ timeout: 20_000 })
}

/**
 * Watches a page for the three kinds of problem a portal can have, and reports them
 * in a form that says what went wrong.
 *
 * `page.on('console')` alone is not enough. A browser logs "Failed to load resource:
 * ... 404" with no URL in the message text, and the bidder portal legitimately gets
 * 404s — "has this person registered yet?" is a question whose answer is sometimes
 * no. Asserting on console text therefore either fails on expected 404s or has to
 * ignore all of them, which would also hide a 404 on a bundle.
 *
 * So HTTP failures are collected from responses, with their URLs and statuses, and
 * an expected 404 is one on a resource the portal is checking the existence of.
 * Uncaught exceptions come from `pageerror`, which is the signal a thrown render
 * error or a bad claim actually produces.
 */
export interface PageProblems {
  /** Uncaught JavaScript errors. Never expected. */
  exceptions: string[]
  /** HTTP failures, excluding the existence checks named below. */
  httpFailures: string[]
  /** Console errors that are not the browser's own report of an HTTP failure. */
  consoleErrors: string[]
  /**
   * Step-up challenges: a 403 from a gated endpoint, which is the gate working.
   *
   * Kept out of `all()` but kept, rather than dropped, so a test can assert the
   * gate engaged in the browser as well as in the API — a challenge that silently
   * stopped arriving would otherwise look exactly like a clean run.
   */
  stepUpChallenges: string[]
  all: () => string[]
}

/**
 * A 404 here means "not yet", not "broken": the bidder portal reads these to decide
 * which step of registration to show.
 */
const EXISTENCE_CHECKS = [/\/bidders\/[0-9a-f-]+$/, /\/subscriptions\/[0-9a-f-]+$/]

/**
 * The endpoints behind the second factor. A 403 from one of these is the system
 * asking the user to confirm who they are, not a fault — it is the first half of
 * every step-up, and the portal answers it by redirecting to the identity provider.
 */
const STEP_UP_GATED = [/\/bidders\/register$/, /\/deposit$/, /\/guarantee$/, /\/award$/]

export function watchPage(page: Page): PageProblems {
  const problems: PageProblems = {
    exceptions: [],
    httpFailures: [],
    consoleErrors: [],
    stepUpChallenges: [],
    all: () => [...problems.exceptions, ...problems.httpFailures, ...problems.consoleErrors],
  }

  page.on('pageerror', (error) => {
    problems.exceptions.push(`uncaught: ${error.message}`)
  })

  page.on('response', (response) => {
    const status = response.status()
    if (status < 400) return

    const url = response.url()
    const path = new URL(url).pathname

    if (status === 404 && EXISTENCE_CHECKS.some((pattern) => pattern.test(path))) return

    if (status === 403 && STEP_UP_GATED.some((pattern) => pattern.test(path))) {
      problems.stepUpChallenges.push(`${status} ${url}`)
      return
    }

    problems.httpFailures.push(`${status} ${url}`)
  })

  page.on('console', (message) => {
    if (message.type() !== 'error') return
    const text = message.text()
    // The browser's own line about a response; the response handler above has it
    // with a URL and a status, which is far more useful.
    if (text.startsWith('Failed to load resource')) return
    problems.consoleErrors.push(text)
  })

  return problems
}

/** A datetime-local value, in the browser's own local time. */
export function localInput(at: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return (
    `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}` +
    `T${pad(at.getHours())}:${pad(at.getMinutes())}`
  )
}

/**
 * A riyal amount as the portals render it.
 *
 * Both portals format money with the ar-SA locale, so 1,200,000.00 appears as
 * ١٬٢٠٠٬٠٠٠٫٠٠ with Arabic-Indic digits and an Arabic decimal separator. A test
 * asserting Latin digits does not merely fail — in a negative assertion it silently
 * passes, which is how the D-23 leak check here was looking for a string that could
 * never appear on the page whether the reserve leaked or not.
 */
export function arabicRiyals(minorUnits: number): string {
  return new Intl.NumberFormat('ar-SA', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(minorUnits / 100)
}
