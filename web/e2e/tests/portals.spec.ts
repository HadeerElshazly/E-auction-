import { expect, test } from '@playwright/test'
import {
  ADMIN_URL,
  attachDocument,
  actor,
  openAuction,
  openAuctionInPlace,
  qualify,
  BIDDER_URL,
  arabicRiyals,
  completeStepUp,
  watchPage,
  localInput,
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
      // Plot number, street width and frontage — the three a bidder values the
      // land on. The last plot leaves the two measurements empty, which is the
      // case that must not reach the domain as a zero.
      const plots: Array<[string, string, string, string]> = [
        ['1005', '812.5', '20', '25.5'],
        ['1006', '940', '25', '30'],
        ['1007', '756.25', '', ''],
      ]
      for (const [index, [number, area, streetWidth, frontage]] of plots.entries()) {
        await page.getByLabel('رقم القطعة').fill(number)
        await page.getByLabel('المساحة (م²)').fill(area)
        await page.getByLabel('عرض الشارع (م)').fill(streetWidth)
        await page.getByLabel('الواجهة (م)').fill(frontage)
        const add = page.getByRole('button', { name: '+ إضافة قطعة' })
        await expect(add).toBeEnabled()
        await add.click()
        await expect(
          page.getByRole('heading', { level: 3, name: new RegExp(`قطع الأرض \\(${index + 1}\\)`) }),
        ).toBeVisible()
      }

      // Real files, through the document service, and the ids it hands back. The
      // Arabic filename is the case that matters: S3 user metadata is ASCII-only,
      // so this is what turns a booklet's name into question marks.
      await attachDocument(
        page,
        'ملف كراسة الشروط',
        'كراسة الشروط.pdf',
        '%PDF-1.7\n% كراسة الشروط\n%%EOF\n',
        'application/pdf',
      )
      await expect(page.getByText('كراسة الشروط: ✓')).toBeVisible({ timeout: 30_000 })

      await attachDocument(page, 'ملف صورة الغلاف', 'cover.svg', '<svg/>', 'image/svg+xml')
      await expect(page.getByText('صورة الغلاف: ✓')).toBeVisible({ timeout: 30_000 })

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
      await expect(page.getByText(/معتمد|قادم/).first()).toBeVisible()
    })

    await test.step('a citizen sees the catalogue without signing in', async () => {
      // A land auction is published before anyone registers; that is how someone
      // decides whether to buy the booklet at all.
      await anon.goto(BIDDER_URL)
      await expect(anon.getByRole('button', { name: 'الدخول بنفاذ' }).first()).toBeVisible()

      const card = anon.locator('.auction-card', { hasText: nameAr })
      await expect(card).toBeVisible({ timeout: 90_000 })
      await expect(card).toContainText('3 قطعة')
      await card.getByRole('button', { name: 'تفاصيل المزاد' }).click()

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

      // Bidding happens on its own screen, entered from the auction page.
      await page.getByRole('button', { name: 'شاشة المزايدة' }).click()

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
      await page.getByRole('button', { name: 'شاشة المزايدة' }).click()

      const bid = page.getByRole('button', { name: 'إرسال المزايدة' })
      await expect(bid).toBeVisible({ timeout: 120_000 })

      // Below the minimum: refused by the form, without troubling the server.
      await page.getByLabel('مبلغ المزايدة').fill('900000.00')
      await expect(bid).toBeDisabled()
      await expect(page.getByText(/يجب أن يزيد المبلغ على السعر الحالي/)).toBeVisible()

      // At the reserve, which is what makes the auction awardable.
      await page.getByLabel('مبلغ المزايدة').fill('1200000.00')
      await expect(bid).toBeEnabled()
      await bid.click()

      await expect(page.getByText('أنت الأعلى حالياً')).toBeVisible({ timeout: 60_000 })
    })

    await test.step('the bidder can take a certificate for the bid', async () => {
      const page = sara.page

      await page.getByRole('button', { name: 'شهادة' }).first().click()

      const certificate = page.locator('.certificate')
      await expect(certificate.getByRole('heading', { name: 'شهادة مزايدة' })).toBeVisible({
        timeout: 30_000,
      })

      // Every figure on it is read back out of the log by the service, so this is
      // also the assertion that the read-by-offset path works against a real broker.
      await expect(certificate.getByText(/EA-[0-9A-F]{8}-\d+/)).toBeVisible()
      // Sara's own bid was the opening price; khalid's 1,200,000 is not hers.
      await expect(certificate.getByText(arabicRiyals(100_000_000))).toBeVisible()
      await expect(certificate.getByText('إلكتروني')).toBeVisible()

      // The print stylesheet, which nothing else would catch. It hides the rest of
      // the app by visibility rather than display — `display: none` on an ancestor
      // would take the certificate out of the layout with it and print a blank
      // page, and that mistake is invisible on screen.
      await page.emulateMedia({ media: 'print' })
      await expect(certificate.getByRole('heading', { name: 'شهادة مزايدة' })).toBeVisible()
      await expect(page.getByRole('button', { name: 'إرسال المزايدة' })).toBeHidden()
      await page.emulateMedia({ media: 'screen' })

      await page.getByRole('button', { name: 'إغلاق' }).click()
      await expect(certificate).toHaveCount(0)
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

    await test.step('the bell carries the notices her tab would have missed', async () => {
      // The other half of being outbid. The row above is pushed to a bidder who is
      // watching; this is the one who closed the tab, and it is the whole reason
      // the notification service exists.
      const page = sara.page

      // The bell in the top bar opens الإشعارات, a page of its own.
      const bell = page.getByRole('link', { name: /الإشعارات/ }).first()
      await expect(bell).toBeVisible()

      // Every assertion below names this auction.
      //
      // An inbox belongs to a bidder, not to an auction, so it holds notices for
      // every auction they are registered for — including the hall one from the
      // other spec and anything left by an earlier run. A bare
      // getByText('بدأ المزاد') matched four notices and failed strict mode, which
      // is the test being wrong about its subject rather than the product.
      const notice = (title: string) =>
        page.locator('.notice-item').filter({ hasText: title }).filter({ hasText: nameAr })

      // The page polls every twenty seconds, so the notice may not be there the
      // instant the bid lands. Waited for by reloading the page rather than by one
      // long expect on a list that may not have asked again yet.
      await bell.click()
      await expect(async () => {
        await page.reload()
        await expect(notice('تمت المزايدة عليك')).toBeVisible({ timeout: 3_000 })
      }).toPass({ timeout: 90_000 })

      // Registered and qualified earlier in this walk-through, so both are here too.
      await expect(notice('مؤهّل للمزايدة')).toBeVisible()
      await expect(notice('بدأ المزاد')).toBeVisible()

      // D-22 holds in the inbox as well: she is told she is behind, not who is
      // ahead, and nothing here names the other bidder.
      const panel = await page.locator('main.page').innerText()
      expect(panel, "the leading bidder's name leaked into the inbox").not.toContain('خالد')

      // Reading them clears the unread count they were contributing to.
      await page.getByRole('button', { name: 'تعليم الكل كمقروء' }).click()
      await expect(page.getByRole('link', { name: 'الإشعارات', exact: true }).first()).toBeVisible()
      await expect(page.locator('.notice-item.unread')).toHaveCount(0)
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
      // The auction page is tabbed; the result is under «النتيجة والترسية».
      await page.getByTestId('auction-tab-award').click()

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

