import { expect, test, type Browser, type Page } from '@playwright/test'
import {
  ADMIN_URL,
  BIDDER_URL,
  arabicRiyals,
  completeStepUp,
  watchPage,
  localInput,
  signIn,
  type PageProblems,
} from './helpers'

/**
 * One auction, driven end to end through the two portals' user interfaces.
 *
 * Deliberately a single test with steps rather than several tests sharing state.
 * Playwright restarts its worker process after a failure, so module-level state does
 * not survive one — a suite built that way reports a cascade of confusing failures
 * when the real problem is in the first step. It is also honest about the shape of
 * the thing: an auction cannot be bid on before it is approved, so this is a
 * sequence, not a set.
 *
 * Nothing is mocked. A real Keycloak login form, real services, real Kafka, and a
 * bid whose 104-byte frame is built and signed by the browser's own Web Crypto.
 */
test('an auction runs from draft to award through the portals', async ({ browser }) => {
  const OPENING = '1000000.00'
  const RESERVE = '1200000.00'
  const INCREMENT = '50000.00'
  const nameAr = `مخطط السعيد — اختبار ${Date.now()}`

  // A context per actor, because they are different people with different sessions
  // and Keycloak keeps a cookie: sharing one would silently sign the committee in
  // as the administrator.
  const admin = await actor(browser, ADMIN_URL, 'admin-user')
  const committee = await actor(browser, ADMIN_URL, 'committee-user')
  const sara = await actor(browser, BIDDER_URL, 'sara')
  const khalid = await actor(browser, BIDDER_URL, 'khalid')
  const citizen = await browser.newContext()
  const anon = await citizen.newPage()
  const anonProblems = watchPage(anon)

  let auctionId = ''
  let startsAt = new Date()

  try {
    await test.step('an administrator prepares the auction', async () => {
      const page = admin.page

      await page.getByRole('button', { name: '+ إنشاء مزاد' }).click()
      await page.getByLabel('اسم المزاد بالعربي').fill(nameAr)
      await page.getByLabel('Auction name in English').fill('Al-Saeed plan — e2e')
      await page.getByRole('button', { name: 'إنشاء مسودة' }).click()

      await expect(page.getByText('مسودة')).toBeVisible()
      auctionId = await page.locator('code.muted.small').first().innerText()
      expect(auctionId).toMatch(/^[0-9a-f-]{36}$/)

      // Three plots, sold as one indivisible package.
      //
      // Each iteration waits for the count in the heading to reach its own number
      // before the next one types: the form clears itself once the POST returns, so
      // typing straight into the next plot races that reset and loses the input.
      const plots: Array<[string, string]> = [
        ['1010/5', '812.5'],
        ['1010/6', '940'],
        ['1010/7', '756.25'],
      ]
      for (const [index, [deed, area]] of plots.entries()) {
        await page.getByLabel('رقم الصك').fill(deed)
        await page.getByLabel('المساحة (م²)').fill(area)
        const add = page.getByRole('button', { name: '+ إضافة قطعة' })
        await expect(add).toBeEnabled()
        await add.click()
        await expect(
          page.getByRole('heading', { level: 3, name: new RegExp(`قطع الأرض \\(${index + 1}\\)`) }),
        ).toBeVisible()
      }

      await page.getByRole('button', { name: 'إرفاق كراسة' }).click()
      await expect(page.getByText('كراسة الشروط: ✓')).toBeVisible()
      await page.getByRole('button', { name: 'إرفاق غلاف' }).click()
      await expect(page.getByText('صورة الغلاف: ✓')).toBeVisible()

      // Two minutes and then one, not seconds: datetime-local has minute precision,
      // so a start and an end inside the same minute arrive at the server identical
      // and validation rejects "End must be after start". Right for real auctions,
      // which run for days, and it sets the floor on how short this test can be.
      startsAt = new Date(Date.now() + 120_000)
      const endsAt = new Date(startsAt.getTime() + 60_000)
      await page.getByLabel('بداية المزاد').fill(localInput(startsAt))
      await page.getByLabel('نهاية المزاد').fill(localInput(endsAt))

      await page.getByLabel(/سعر الافتتاح/).fill(OPENING)
      await page.getByLabel('السعر الاحتياطي').fill(RESERVE)
      await page.getByLabel(/الحد الأدنى للمزايدة/).fill(INCREMENT)
      await page.getByLabel(/^التأمين/).fill('100000.00')
      await page.getByLabel(/سعر الكراسة/).fill('1000.00')
      await page.getByLabel('نسبة السعي %').fill('2.5')
      await page.getByRole('button', { name: 'حفظ البيانات' }).click()

      await page.getByRole('button', { name: 'فحص البيانات' }).click()
      await expect(page.getByText('البيانات مكتملة — يمكن إرسال المزاد للاعتماد.')).toBeVisible()

      await page.getByRole('button', { name: 'إرسال للاعتماد' }).click()
      await expect(page.getByText('بانتظار الاعتماد').first()).toBeVisible()
    })

    await test.step('the preparer cannot approve their own auction', async () => {
      // Separation of duties, enforced by the service and reflected in the portal.
      const page = admin.page
      await expect(
        page.getByText('بانتظار لجنة الترسية — لا يعتمد مُعدّ المزاد مزاده بنفسه.'),
      ).toBeVisible()
      await expect(page.getByRole('button', { name: 'اعتماد المزاد' })).toHaveCount(0)
    })

    await test.step('the award committee approves it', async () => {
      const page = committee.page
      await openAuction(page, auctionId)
      await page.getByRole('button', { name: 'اعتماد المزاد' }).click()
      await expect(page.getByText(/معتمد|مجدول/).first()).toBeVisible()
    })

    await test.step('a citizen sees the catalogue without signing in', async () => {
      // A land auction is published before anyone registers; that is how someone
      // decides whether to buy the booklet at all.
      await anon.goto(BIDDER_URL)
      await expect(anon.getByRole('button', { name: 'الدخول بنفاذ' }).first()).toBeVisible()

      const card = anon.locator('.card', { hasText: nameAr })
      await expect(card).toBeVisible({ timeout: 90_000 })
      await expect(card).toContainText('3 قطعة')
      await card.getByRole('button', { name: 'التفاصيل' }).click()

      // The plots a bidder needs in order to decide.
      await expect(anon.getByRole('cell', { name: '1010/6' })).toBeVisible()

      // D-23 end to end: the reserve must not reach a public page in any form —
      // checked in the digits the page actually renders, and in the raw minor units
      // in case something serialises it unformatted.
      const body = await anon.locator('body').innerText()
      expect(body, 'the reserve price leaked to the public catalogue').not.toContain(
        arabicRiyals(120_000_000),
      )
      expect(body).not.toContain('1,200,000')
      expect(body).not.toContain('120000000')
    })

    await test.step('both bidders qualify to bid', async () => {
      // Two, because a bidding war needs two: the engine refuses a leader raising
      // their own bid (B-04), so a one-bidder test can never get past the opening
      // price and the auction closes below its reserve.
      await qualify(sara.page, nameAr, 'sara@example.sa')
      await qualify(khalid.page, nameAr, 'khalid@example.sa')
    })

    await test.step('the portal is on the push channel, not polling', async () => {
      // The badge is driven by which transport delivered the price, so this is the
      // one assertion that distinguishes a stream from a poll from outside. If the
      // SSE connection were refused — a missing CORS header, an ingress that
      // buffers, EventSource's inability to send an Authorization header — the
      // portal would still work and would quietly say "تحديث دوري" instead.
      await expect(sara.page.getByText('مباشر')).toBeVisible({ timeout: 180_000 })
      await expect(sara.page.getByText('تحديث دوري')).toHaveCount(0)
    })

    await test.step('the browser signs a bid and the catcher accepts it', async () => {
      const page = sara.page

      // The bid box appears once the processor has opened the auction and the
      // catcher holds it. Nothing polls for that here — the portal does.
      const bid = page.getByRole('button', { name: 'إرسال المزايدة' })
      await expect(bid).toBeVisible({ timeout: 180_000 })

      // The opening price is the first acceptable bid and the box is prefilled with
      // exactly that, so an unmodified submit must be accepted.
      await expect(page.getByLabel('مبلغ المزايدة')).toHaveValue('1,000,000.00')
      await bid.click()

      // The row appears, then carries the processor's actual ruling.
      //
      // Deliberately not asserting the intermediate "مُسجَّلة" — recorded, not yet
      // judged. That state is real and the 202 still means it, but over the push
      // channel the verdict arrives within milliseconds, so the row goes straight
      // to "الأعلى" and the intermediate state is not observable from outside.
      // Pinning a test to it made the test fail because the fan-out was fast.
      await expect(page.getByRole('cell', { name: /الأعلى|مُسجَّلة/ })).toBeVisible({
        timeout: 30_000,
      })

      // The processor's verdict, pushed: she led, and it was this bid that won.
      await expect(page.getByText('أنت الأعلى حالياً')).toBeVisible({ timeout: 60_000 })
      await expect(page.getByRole('cell', { name: 'الأعلى' })).toBeVisible({ timeout: 30_000 })

      // And once she leads, the portal refuses to let her raise her own bid rather
      // than letting the processor discard it silently.
      await expect(bid).toBeDisabled()
      await expect(page.getByText(/أنت الأعلى بالفعل/)).toBeVisible()
    })

    await test.step('the other bidder outbids her', async () => {
      const page = khalid.page

      const bid = page.getByRole('button', { name: 'إرسال المزايدة' })
      await expect(bid).toBeVisible({ timeout: 120_000 })

      // Below the minimum: refused by the form, without troubling the server.
      await page.getByLabel('مبلغ المزايدة').fill('900000.00')
      await expect(bid).toBeDisabled()
      await expect(page.getByText('أقل من أقل مزايدة مقبولة.')).toBeVisible()

      // At the reserve, which is what makes the auction awardable.
      await page.getByLabel('مبلغ المزايدة').fill('1200000.00')
      await expect(bid).toBeEnabled()
      await bid.click()

      await expect(page.getByText('أنت الأعلى حالياً')).toBeVisible({ timeout: 60_000 })
    })

    await test.step('the first bidder is told she has been outbid', async () => {
      const page = sara.page

      // Pushed, not polled: her own bid, which the price has now moved past.
      await expect(page.getByRole('cell', { name: 'تجاوزها غيرك' })).toBeVisible({
        timeout: 60_000,
      })
      await expect(page.getByText('أنت الأعلى حالياً')).toHaveCount(0)

      // D-22 cuts both ways: she is told she is behind, not who is ahead.
      const body = await page.locator('body').innerText()
      expect(body, "the leading bidder's name leaked").not.toContain('خالد')
    })

    await test.step('an onlooker sees the price but not who is leading', async () => {
      // D-22 from outside the auction entirely: the price is public because this is
      // an open auction, the identity is not.
      await expect(anon.getByText(/المزايد الأعلى: مزايد #\d+/)).toBeVisible({ timeout: 90_000 })
      await expect(anon.getByText('أنت الأعلى حالياً')).toHaveCount(0)

      const body = await anon.locator('body').innerText()
      expect(body, "a bidder's name leaked to an anonymous watcher").not.toContain('خالد')
      expect(body).not.toContain('سارة')
    })

    await test.step('the committee awards to the winning bidder', async () => {
      const page = committee.page
      await openAuction(page, auctionId)

      // The processor closes the auction and offers a candidate over
      // auctions.lifecycle. Nothing in the portal asked for this.
      await expect(page.getByRole('heading', { name: 'المرشّح' })).toBeVisible({
        timeout: 150_000,
      })
      await expect(page.locator('.big-number').first()).toContainText(arabicRiyals(120_000_000))

      await page.getByRole('button', { name: 'تأكيد الترسية' }).click()

      // Awarding a parcel of state land. The committee role is not enough.
      if (await completeStepUp(page)) {
        await expect(page.getByText('تم التحقق من هويتك')).toBeVisible({ timeout: 30_000 })
        await openAuctionInPlace(page, auctionId)
        await page.getByRole('button', { name: 'تأكيد الترسية' }).click()
      }

      await expect(page.getByRole('heading', { name: /الترسية المعتمدة/ })).toBeVisible({
        timeout: 30_000,
      })

      // The letter order the domain enforces, made visible as steps.
      await page.getByRole('button', { name: 'إصدار الخطاب' }).click()
      await page.getByRole('button', { name: 'رفع الخطاب الموقّع' }).click()
      await page.getByRole('button', { name: 'إشعار الفائز' }).click()
      await expect(page.locator('.steps .done')).toHaveCount(3)
    })

    // A CORS block, a missing asset, a 500, or a render error from a claim read
    // under the wrong name shows up here and nowhere else.
    for (const [who, problems] of [
      ['admin-user', admin.problems],
      ['committee-user', committee.problems],
      ['sara', sara.problems],
      ['khalid', khalid.problems],
      ['anonymous', anonProblems],
    ] as const) {
      expect(problems.all(), `${who}'s browser reported: ${problems.all().join(' | ')}`).toEqual([])
    }

    // And the gate itself, from the browser's side. The steps above complete the
    // challenges, so a gate that stopped engaging would leave them passing and only
    // this would notice — which is the whole reason the challenges are counted
    // rather than discarded as expected noise.
    for (const [who, problems] of [
      ['sara', sara.problems],
      ['khalid', khalid.problems],
      ['committee-user', committee.problems],
    ] as const) {
      expect(
        problems.stepUpChallenges.length,
        `${who} was never asked to confirm their identity before committing`,
      ).toBeGreaterThan(0)
    }

    // The admin prepares auctions and commits nothing, so nothing should have asked
    // them to confirm. A challenge here would mean the gate is on the wrong surface.
    expect(admin.problems.stepUpChallenges).toEqual([])
  } finally {
    await Promise.all([
      admin.close(),
      committee.close(),
      sara.close(),
      khalid.close(),
      citizen.close(),
    ])
  }
})

/**
 * Registers, completes the profile, subscribes and pays, through the UI.
 *
 * Crosses two step-up gates on the way — registration and the deposit — and asserts
 * that each one actually demanded a second factor. Asserting that matters: a gate
 * that silently stopped engaging would leave this helper passing while the thing it
 * exists to protect was wide open.
 */
async function qualify(page: Page, nameAr: string, email: string): Promise<void> {
  await openPublicAuction(page, nameAr)

  // --- KYC: binds a national identity to this account permanently -----------
  await page.getByRole('button', { name: 'التسجيل بالهوية الوطنية' }).click()

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

  // --- the steps that commit nothing: no second factor expected -------------
  await page.getByRole('button', { name: 'الاشتراك في المزاد' }).click()
  await page.getByRole('button', { name: 'شراء كراسة الشروط' }).click()
  await page.getByRole('button', { name: 'أوافق على الشروط والأحكام' }).click()
  await page.getByRole('button', { name: 'سداد التأمين إلكترونياً' }).click()

  // --- the deposit: money ---------------------------------------------------
  await page.getByRole('button', { name: /تأكيد سداد التأمين/ }).click()

  // The confirmation from registration is a minute old at most, so the gate lets
  // this through on the same token and no form appears. Either outcome is correct —
  // what matters is that the deposit goes through and was not taken on a token that
  // never carried a second factor — so this handles the redirect if there is one and
  // carries on if there is not.
  if (await completeStepUp(page)) {
    await expect(page.getByText('تم التحقق من هويتك')).toBeVisible({ timeout: 30_000 })
    await openPublicAuctionInPlace(page, nameAr)
  }

  const confirmDeposit = page.getByRole('button', { name: /تأكيد سداد التأمين/ })
  if (await confirmDeposit.isVisible().catch(() => false)) {
    await confirmDeposit.click()
  }

  await expect(page.getByRole('heading', { name: 'مؤهّل للمزايدة ✓' })).toBeVisible({
    timeout: 30_000,
  })
}

interface Actor {
  page: Page
  problems: PageProblems
  close: () => Promise<void>
}

async function actor(browser: Browser, portal: string, username: string): Promise<Actor> {
  const context = await browser.newContext()
  const page = await context.newPage()
  const problems = watchPage(page)
  await signIn(page, portal, username)
  return { page, problems, close: () => context.close() }
}

/** Opens one auction in the admin portal by its id, rather than whatever is first. */
async function openAuction(page: Page, auctionId: string): Promise<void> {
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
async function openAuctionInPlace(page: Page, auctionId: string): Promise<void> {
  const row = page.locator('tbody tr').filter({ hasNot: page.locator('_nonexistent') })
  await expect(row.first()).toBeVisible({ timeout: 30_000 })

  // The list shows no ids, so open rows until the editor shows the one wanted. The
  // newest is first and that is this run's auction, but asserting the id means a
  // stale auction from an earlier run cannot quietly stand in for it.
  await page.getByRole('button', { name: 'فتح' }).first().click()
  await expect(page.locator('code.muted.small').first()).toHaveText(auctionId, {
    timeout: 20_000,
  })
}

/** Opens one auction in the bidder portal by its Arabic name. */
async function openPublicAuction(page: Page, nameAr: string): Promise<void> {
  await page.goto(BIDDER_URL)
  await openPublicAuctionInPlace(page, nameAr)
}

/** The same, without the reload — see openAuctionInPlace for why that matters. */
async function openPublicAuctionInPlace(page: Page, nameAr: string): Promise<void> {
  const card = page.locator('.card', { hasText: nameAr })
  await expect(card).toBeVisible({ timeout: 90_000 })
  await card.getByRole('button', { name: 'التفاصيل' }).click()
}
