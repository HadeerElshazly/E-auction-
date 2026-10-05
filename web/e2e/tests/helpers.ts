import { expect, type Page } from '@playwright/test'

export const ADMIN_URL = process.env.ADMIN_URL ?? 'http://localhost:3001'
export const BIDDER_URL = process.env.BIDDER_URL ?? 'http://localhost:3000'
export const PASSWORD = process.env.SMOKE_PASSWORD ?? 'dev-only-password'

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
  all: () => string[]
}

/**
 * A 404 here means "not yet", not "broken": the bidder portal reads these to decide
 * which step of registration to show.
 */
const EXISTENCE_CHECKS = [/\/bidders\/[0-9a-f-]+$/, /\/subscriptions\/[0-9a-f-]+$/]

export function watchPage(page: Page): PageProblems {
  const problems: PageProblems = {
    exceptions: [],
    httpFailures: [],
    consoleErrors: [],
    all: () => [...problems.exceptions, ...problems.httpFailures, ...problems.consoleErrors],
  }

  page.on('pageerror', (error) => {
    problems.exceptions.push(`uncaught: ${error.message}`)
  })

  page.on('response', (response) => {
    const status = response.status()
    if (status < 400) return

    const url = response.url()
    if (status === 404 && EXISTENCE_CHECKS.some((pattern) => pattern.test(new URL(url).pathname))) {
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
