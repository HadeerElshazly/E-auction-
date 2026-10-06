import { expect, type Browser, type Locator, type Page } from '@playwright/test'
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
const STEP_UP_GATED = [
  /\/bidders\/register$/,
  // The booklet fee joined this list when it stopped being a string the caller
  // made up and became a charge the payment service takes (§30).
  //
  // Scoped to the subscription path on purpose: auction-admin has a /booklet of
  // its own for attaching the PDF, which is role-gated and not stepped up. A bare
  // /booklet$ would have quietly reclassified a genuine 403 there as a challenge.
  /\/subscriptions\/[0-9a-f-]+\/booklet$/,
  /\/deposit$/,
  /\/guarantee$/,
  /\/award$/,
]

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

/**
 * Attaches a document in the admin portal by setting files on the hidden input.
 *
 * Not by pressing the button: that opens the operating system's file dialog,
 * which Playwright cannot drive. Setting files on the input fires the same change
 * handler the button's click would have produced, which is the whole point of
 * driving it through an input rather than a bespoke dialog.
 */
export async function attachDocument(
  page: Page,
  inputLabel: string,
  fileName: string,
  body: string,
  mimeType: string,
): Promise<void> {
  await page.getByLabel(inputLabel).setInputFiles({
    name: fileName,
    mimeType,
    buffer: Buffer.from(body, 'utf8'),
  })
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

// ---------------------------------------------------------------------------
// The journeys both walk-throughs share.
//
// Lifted out of the online spec when the hall got one of its own: a bidder
// qualifies the same way whichever channel will sell the land, and two copies of
// a journey that crosses two step-up gates would drift on the first change to
// either.
// ---------------------------------------------------------------------------

/**
 * Registers, completes the profile, subscribes and pays, through the UI.
 *
 * Crosses two step-up gates on the way — registration and the deposit — and asserts
 * that each one actually demanded a second factor. Asserting that matters: a gate
 * that silently stopped engaging would leave this helper passing while the thing it
 * exists to protect was wide open.
 */
export async function qualify(page: Page, nameAr: string, email: string): Promise<void> {
  await openPublicAuction(page, nameAr)

  // --- KYC: binds a national identity to this account permanently -----------
  //
  // Done once per person, ever: D-25 makes one national ID one bidder, so a bidder
  // who registered in an earlier auction — or an earlier walk-through — has no
  // registration step here at all. Branching on the button rather than on a flag
  // keeps this usable from both walk-throughs in either order.
  const register = page.getByRole('button', { name: 'التسجيل بالهوية الوطنية' })
  const subscribe = page.getByRole('button', { name: 'الاشتراك في المزاد' })

  // Which half to run is decided by waiting for the "already registered" signal,
  // not by asking whether a button happens to be on screen at this instant.
  //
  // `isVisible` answers for the moment it is called. Asking while the card is still
  // rendering gets whichever answer the race produced — and both wrong answers are
  // a hang: skip registration and the subscribe step never comes, or run it and the
  // registration button is gone before the click lands.
  let registered = true
  try {
    await subscribe.waitFor({ state: 'visible', timeout: 15_000 })
  } catch {
    registered = false
  }

  if (!registered) {
    await expect(register).toBeVisible({ timeout: 45_000 })
  }

  const needsRegistration = !registered

  if (needsRegistration) {
    await register.click()

    expect(
      await completeStepUp(page),
      'registration should have demanded a second factor',
    ).toBe(true)

    // Back on the portal, with the confirmation acknowledged.
    await expect(page.getByText('تم التحقق من هويتك')).toBeVisible({ timeout: 30_000 })

    // The redirect landed back on the catalogue, so pick the auction up again and
    // retry the action — which is what a real user does after confirming. Without a
    // reload: the token carrying the confirmation is in this page's memory.
    await openPublicAuctionInPlace(page, nameAr)
    await page.getByRole('button', { name: 'التسجيل بالهوية الوطنية' }).click()

    // Nafath establishes identity, not contact details, and the deposit cannot be
    // confirmed without somewhere to send an award letter.
    await page.getByLabel('رقم الجوال').fill('+966500000001')
    await page.getByLabel('البريد الإلكتروني').fill(email)
    await page.getByRole('button', { name: 'حفظ بيانات التواصل' }).click()
  }

  // --- the step that commits nothing: no second factor expected -------------
  await page.getByRole('button', { name: 'الاشتراك في المزاد' }).click()

  // --- the booklet fee: money, and asynchronous -----------------------------
  //
  // Both money steps now only *ask* the payment service. The button returns 202 and
  // the step completes when a settlement arrives on payments.settlements, which the
  // portal picks up by polling — so each one is a click followed by a wait for the
  // next step to appear, never a click whose reply is the answer.
  await payAndWait(
    page,
    nameAr,
    'شراء كراسة الشروط',
    page.getByRole('button', { name: 'أوافق على الشروط والأحكام' }),
    'the booklet fee',
  )

  await page.getByRole('button', { name: 'أوافق على الشروط والأحكام' }).click()
  await page.getByRole('button', { name: 'سداد التأمين إلكترونياً' }).click()

  // --- the deposit: money, and asynchronous ---------------------------------
  await payAndWait(
    page,
    nameAr,
    /دفع مبلغ التأمين/,
    page.getByRole('heading', { name: 'مؤهّل للمزايدة ✓' }),
    'the deposit',
  )
}

/**
 * Presses one of the two buttons that spend money, carries the second factor if the
 * gate asks for one, and waits for the payment service to settle.
 *
 * The waiting is the part worth getting right. The click returns 202 and nothing on
 * the page changes for a second or two; the outcome arrives through Kafka, two
 * services and a poll. So the thing waited for is the *next* step appearing, with a
 * timeout sized for that round trip — and a refusal from the gateway is caught
 * explicitly, because its message is the one useful thing on screen and a bare
 * timeout would throw it away.
 */
async function payAndWait(
  page: Page,
  nameAr: string,
  button: string | RegExp,
  next: Locator,
  what: string,
): Promise<void> {
  await page.getByRole('button', { name: button }).click()

  // The confirmation from registration is a minute old at most, so the gate often
  // lets this through on the same token and no form appears. Either outcome is
  // correct — what matters is that the money was not taken on a token that never
  // carried a second factor — so this handles the redirect if there is one and
  // carries on if there is not.
  if (await completeStepUp(page)) {
    await expect(page.getByText('تم التحقق من هويتك')).toBeVisible({ timeout: 30_000 })
    await openPublicAuctionInPlace(page, nameAr)

    // Re-rendered from scratch, so the button may be back: the 202 was never sent
    // if the gate refused the first click. Waited for rather than sampled — asking
    // whether a button is on screen the instant after a re-render gets an answer
    // about a page that has not finished rendering, which is how three earlier
    // hangs in this file started.
    const retry = page.getByRole('button', { name: button })
    await expect(retry.or(next).first()).toBeVisible({ timeout: 60_000 })
    if (await retry.isVisible()) await retry.click()
  }

  const refused = page.getByText('تعذّر إتمام الدفع')
  await expect(next.or(refused).first()).toBeVisible({ timeout: 90_000 })

  if (await refused.isVisible()) {
    throw new Error(
      `the payment gateway refused ${what}: ${await refused.textContent()}`,
    )
  }
}

export interface Actor {
  page: Page
  problems: PageProblems
  close: () => Promise<void>
}

export async function actor(browser: Browser, portal: string, username: string): Promise<Actor> {
  const context = await browser.newContext()
  const page = await context.newPage()
  const problems = watchPage(page)
  await signIn(page, portal, username)
  return { page, problems, close: () => context.close() }
}

/** Opens one auction in the admin portal by its id, rather than whatever is first. */
export async function openAuction(page: Page, auctionId: string): Promise<void> {
  await page.goto(ADMIN_URL)
  await openAuctionInPlace(page, auctionId)
}

/**
 * Opens the auction without reloading the page.
 *
 * Reaching for `page.goto` after a step-up would undo it. The token that carries the
 * second factor lives in the tab's memory and nowhere else — deliberately, so that
 * nothing on the page can read a committee member's token out of storage — so a
 * reload throws it away, and the portal quietly re-acquires a *level 1* token from
 * Keycloak's session. The action that demanded the confirmation is then refused
 * again, and the user is in a loop they cannot get out of by pressing harder.
 *
 * Staying inside the single-page application is both what a real user does after
 * confirming and the only thing that works.
 */
export async function openAuctionInPlace(page: Page, auctionId: string): Promise<void> {
  // المزادات is a grid of cards, and the card itself is the target — there is no
  // separate open button, the same as the screens this portal is built from. Found
  // by test id rather than by its Arabic label: the label is the auction's name,
  // which changes every run, and matching on wording makes a rename look like a
  // broken list.
  const card = page.getByTestId('auction-card')
  await expect(card.first()).toBeVisible({ timeout: 30_000 })

  // The list shows no ids, so open the first and assert which one it was. The
  // newest is first and that is this run's auction, but asserting the id means a
  // stale auction from an earlier run cannot quietly stand in for it.
  await card.first().click()
  await expect(page.locator('code.muted.small').first()).toHaveText(auctionId, {
    timeout: 20_000,
  })
}

/** Opens one auction in the bidder portal by its Arabic name. */
export async function openPublicAuction(page: Page, nameAr: string): Promise<void> {
  await page.goto(BIDDER_URL)
  await openPublicAuctionInPlace(page, nameAr)
}

/** The same, without the reload — see openAuctionInPlace for why that matters. */
export async function openPublicAuctionInPlace(page: Page, nameAr: string): Promise<void> {
  // `.auction-card`, not `.card`: the catalogue is a grid of auction cards now. A
  // generic `.card` would also match the notice above the grid and whatever else
  // frames the page — `hasText` narrows it, but matching the wrong kind of box and
  // being saved by its contents is how a locator starts passing for the wrong
  // reason.
  const card = page.locator('.auction-card', { hasText: nameAr })
  await expect(card).toBeVisible({ timeout: 90_000 })
  await card.getByRole('button', { name: 'التفاصيل' }).click()
}
