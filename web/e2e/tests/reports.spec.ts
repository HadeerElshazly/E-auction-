import { expect, test } from '@playwright/test'
import { ADMIN_URL, actor } from './helpers'

/**
 * التقارير and سجل المراجعة, through the browser.
 *
 * These two screens are the stakeholder's half of the demo, and until now both
 * services were reachable only with a token and `curl` — so nothing proved a
 * person could actually see them. What this covers is specifically the browser's
 * problems: a CORS preflight the service never allowed, a Content-Security-Policy
 * that forbids the origin, a role that opens the API and not the tab.
 *
 * It runs after the other specs have produced auctions, and asserts nothing about
 * particular figures: the reports are a read model of whatever the rest of the
 * suite did, and pinning a number here would make this spec fail whenever another
 * one changed. The arithmetic is tested in EAuction.Reporting.Tests, against
 * fixtures that hold still.
 */

test('التقارير open in the admin portal and download as a spreadsheet', async ({ browser }) => {
  // The dedicated role rather than an administrator's, because it is the one whose
  // whole purpose is to open these and nothing else (§35). If it works, the two
  // staff roles that are a superset of it work too.
  const finance = await actor(browser, ADMIN_URL, 'reporting-user')

  try {
    await finance.page.getByTestId('nav-reports').click()
    await expect(finance.page.getByTestId('reports')).toBeVisible()

    // Every tab, because each is a different query and a different projection — and
    // a tab that threw would otherwise be found by a stakeholder.
    for (const tab of [
      'revenue',
      'auctions',
      'participation',
      'plots',
      'deposits',
      'disqualifications',
    ]) {
      await finance.page.getByTestId(`report-tab-${tab}`).click()

      // Either rows or the empty state — never a spinner that never resolves and
      // never an error. `deposits` and `disqualifications` are legitimately empty
      // on a run where nothing defaulted.
      await expect(
        finance.page.getByTestId('report-table').or(finance.page.getByTestId('report-empty')),
      ).toBeVisible({ timeout: 15_000 })
    }

    // The revenue report's three groupings, which is where the one piece of
    // arithmetic worth seeing on screen lives.
    await finance.page.getByTestId('report-tab-revenue').click()
    for (const grouping of ['phase', 'month', 'channel']) {
      await finance.page.getByTestId('report-groupby').selectOption(grouping)
      await expect(
        finance.page.getByTestId('report-table').or(finance.page.getByTestId('report-empty')),
      ).toBeVisible({ timeout: 15_000 })
    }

    // The download. An <a download> cannot carry a bearer token, so this goes
    // through fetch and a blob — which means a broken CORS or CSP shows up here and
    // nowhere else.
    const download = await Promise.all([
      finance.page.waitForEvent('download', { timeout: 20_000 }),
      finance.page.getByTestId('report-csv').click(),
    ]).then(([event]) => event)

    expect(download.suggestedFilename()).toMatch(/^eauction-.*\.csv$/)

    const problems = finance.problems.all()
    expect(problems, `التقارير produced browser problems: ${problems.join(' | ')}`).toEqual([])
  } finally {
    await finance.close()
  }
})

test('an administrator cannot open سجل المراجعة, and an auditor can', async ({ browser }) => {
  // The separation §34 is built on, from the browser's side. An administrator who
  // could read the trail could read the record of their own approvals, so the tab
  // is not there for them — and this is the assertion that keeps it that way.
  const admin = await actor(browser, ADMIN_URL, 'admin-user')

  try {
    await expect(admin.page.getByTestId('nav-audit')).toHaveCount(0)
  } finally {
    await admin.close()
  }

  const auditor = await actor(browser, ADMIN_URL, 'auditor-user')

  try {
    await auditor.page.getByTestId('nav-audit').click()
    await expect(auditor.page.getByTestId('audit')).toBeVisible()

    await expect(
      auditor.page.getByTestId('audit-table').or(auditor.page.getByTestId('audit-empty')),
    ).toBeVisible({ timeout: 15_000 })

    // The button the whole design rests on. §34's claim is that the trail is
    // tamper-evident rather than merely stored, and a claim nobody can check on a
    // screen is a claim nobody believes.
    await auditor.page.getByTestId('audit-verify').click()

    const verdict = auditor.page.getByTestId('audit-verdict')
    await expect(verdict).toBeVisible({ timeout: 20_000 })
    await expect(verdict).toHaveAttribute('data-intact', 'true')

    // And the auditor's own role opens nothing else: the auctions screen says so
    // rather than failing to load, and التقارير are not theirs either (§35 keeps
    // the promise that `auditor` grants nothing outside the trail).
    await expect(auditor.page.getByTestId('nav-reports')).toHaveCount(0)

    const problems = auditor.problems.all()
    expect(problems, `سجل المراجعة produced browser problems: ${problems.join(' | ')}`).toEqual([])
  } finally {
    await auditor.close()
  }
})

test('a reader with only the reporting role is not shown the auction surface', async ({
  browser,
}) => {
  // A finance officer has no business on the approval screens, and the services
  // would refuse them anyway. The portal says so rather than showing a list that
  // fails to load — which is the difference between a read-only account and a
  // broken one.
  const finance = await actor(browser, ADMIN_URL, 'reporting-user')

  try {
    await expect(finance.page.getByTestId('nav-audit')).toHaveCount(0)

    await finance.page.getByTestId('nav-auctions').click()
    await expect(finance.page.getByText('هذا الحساب للقراءة فقط')).toBeVisible()

    expect(finance.problems.all()).toEqual([])
  } finally {
    await finance.close()
  }
})

test('المتابعة المباشرة is open to the committee and closed to finance', async ({
  browser,
}) => {
  // The live board is operational: who is bidding on what, right now. The roles
  // that run auctions get it; finance does not, because every screen they have is
  // after the fact, and the auditor does not either — that account reads the trail
  // and nothing else, by design (§34).
  //
  // What this asserts about the board itself is deliberately thin: it renders, and
  // it says one of the two true things — here are the open auctions, or there are
  // none. Asserting a price would make this test depend on an auction being live at
  // the moment it runs, which is the sort of coupling that turns a suite flaky for
  // reasons that have nothing to do with the screen.
  const committee = await actor(browser, ADMIN_URL, 'committee-user')
  const finance = await actor(browser, ADMIN_URL, 'reporting-user')

  try {
    await committee.page.getByTestId('nav-monitor').click()

    await expect(
      committee.page
        .getByTestId('monitor-card')
        .first()
        .or(committee.page.getByText('لا يوجد مزاد مفتوح الآن')),
    ).toBeVisible({ timeout: 20_000 })

    await expect(finance.page.getByTestId('nav-monitor')).toHaveCount(0)

    expect(committee.problems.all()).toEqual([])
    expect(finance.problems.all()).toEqual([])
  } finally {
    await committee.close()
    await finance.close()
  }
})
