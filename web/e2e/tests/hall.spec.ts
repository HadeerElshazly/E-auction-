import { expect, test } from '@playwright/test'
import {
  ADMIN_URL,
  attachDocument,
  BIDDER_URL,
  actor,
  arabicRiyals,
  localInput,
  openAuction,
  qualify,
} from './helpers'

/**
 * قاعة المزاد — the hall, driven through the clerk's own screen (§29).
 *
 * The API smoke test already proves the pipeline underneath this against real
 * Kafka. What it cannot prove is that a clerk could actually run an auction from
 * the portal: that the terminal only appears for the person on the floor, that it
 * signs with the clerk's key rather than the bidder's, that a paddle number selects
 * the right person, and that the two buttons which end an auction are where the
 * clerk can reach them.
 *
 * Deliberately a separate file from the online walk-through. They share the long
 * parts — preparing an auction, qualifying a bidder — through helpers, and keeping
 * them apart means a failure in one says which channel broke.
 */
test('a clerk runs an auction from the floor', async ({ browser }) => {
  test.setTimeout(8 * 60_000)

  const nameAr = `مخطط السعيد — قاعة ${Date.now()}`

  const admin = await actor(browser, ADMIN_URL, 'admin-user')
  const committee = await actor(browser, ADMIN_URL, 'committee-user')
  const clerk = await actor(browser, ADMIN_URL, 'clerk-user')
  const sara = await actor(browser, BIDDER_URL, 'sara')

  let auctionId = ''
  let clerkUserId = ''
  let startsAt = new Date()
  let warmUpRefusals = 0

  try {
    await test.step('the clerk has nothing to run yet', async () => {
      // And is told the id an administrator needs to put them on the floor, which
      // is the only way to discover it: nothing here can list municipal staff.
      const page = clerk.page
      await expect(page.getByRole('heading', { name: 'لجنة الترسية' })).toHaveCount(0)
      await expect(page.getByText('قاعة المزاد')).toBeVisible({ timeout: 30_000 })
    })

    await test.step('an administrator prepares a hall auction', async () => {
      const page = admin.page

      // «إضافة مزاد»: one auction is one plot, entered with the auction's terms in
      // a single form and saved as a draft.
      await page.getByRole('button', { name: 'إضافة مزاد' }).click()
      const form = page.getByRole('dialog', { name: 'إضافة مزاد جديد' })
      await form.getByLabel('رقم القطعة').fill('2001')
      await form.getByLabel('المساحة بالمتر المربع').fill('900')
      await form.getByLabel('سعر البداية').fill('2000000')
      await form.getByLabel('زيادة المزايدة').fill('5000')
      await form.getByLabel('التأمين').fill('100000')
      await form.getByRole('button', { name: 'حفظ المسودة' }).click()

      await expect(page.getByText('مسودة')).toBeVisible()
      auctionId = await page.locator('code.muted.small').first().innerText()
      expect(auctionId).toMatch(/^[0-9a-f-]{36}$/)


      // Real files, through the document service, and the ids it hands back. The
      // Arabic filename is the case that matters: S3 user metadata is ASCII-only,
      // so this is what turns a booklet's name into question marks.
      await attachDocument(
        page,
        'ملف كراسة الشروط',
        'كراسة القاعة.pdf',
        '%PDF-1.7\n% كراسة الشروط\n%%EOF\n',
        'application/pdf',
      )
      await expect(page.getByText('كراسة الشروط: ✓')).toBeVisible({ timeout: 30_000 })

      await attachDocument(page, 'ملف صورة الغلاف', 'cover.svg', '<svg/>', 'image/svg+xml')
      await expect(page.getByText('صورة الغلاف: ✓')).toBeVisible({ timeout: 30_000 })

      // Onsite is the whole point: it is what makes the clerk's screen appear and
      // what stops sara bidding from her own portal.
      await page.getByLabel('القناة').selectOption('Onsite')

      // Two minutes and then one, for the same datetime-local minute-precision
      // reason as the online walk-through.
      startsAt = new Date(Date.now() + 120_000)
      await page.getByLabel('بداية المزاد').fill(localInput(startsAt))
      await page.getByLabel('نهاية المزاد').fill(localInput(new Date(startsAt.getTime() + 60_000)))

      // Opening and reserve are the same figure, so the clerk's single bid both
      // opens the auction and clears the reserve — otherwise the correct outcome
      // is an exhausted ladder and the committee is offered nobody.
      await page.getByLabel(/سعر الافتتاح/).fill('1000000.00')
      await page.getByLabel('السعر الاحتياطي').fill('1000000.00')
      await page.getByLabel(/الحد الأدنى للمزايدة/).fill('50000.00')
      await page.getByLabel(/^التأمين/).fill('100000.00')
      await page.getByLabel(/سعر الكراسة/).fill('1000.00')
      await page.getByLabel('نسبة السعي %').fill('2.5')
      await page.getByRole('button', { name: 'حفظ البيانات' }).click()

      await page.getByRole('button', { name: 'فحص البيانات' }).click()
      await expect(page.getByText('البيانات مكتملة — يمكن إرسال المزاد للاعتماد.')).toBeVisible()
    })

    await test.step('the clerk is put on the floor', async () => {
      clerkUserId = await clerk.page.getByTestId('clerk-user-id').innerText()
      expect(clerkUserId).toMatch(/^[0-9a-f-]{36}$/)

      const page = admin.page
      await page.getByLabel('معرّف موظف القاعة').fill(clerkUserId)
      await page.getByRole('button', { name: 'تعيين' }).click()

      await expect(page.getByText('معيَّن:')).toBeVisible({ timeout: 20_000 })
    })

    await test.step('the committee approves it', async () => {
      await admin.page.getByRole('button', { name: 'إرسال للاعتماد' }).click()
      await expect(admin.page.getByText('بانتظار الاعتماد').first()).toBeVisible()

      const page = committee.page
      await openAuction(page, auctionId)
      await page.getByRole('button', { name: 'اعتماد المزاد' }).click()
      await expect(page.getByText(/معتمد|قادم/).first()).toBeVisible({ timeout: 20_000 })
    })

    await test.step('a bidder qualifies exactly as they would online', async () => {
      await qualify(sara.page, nameAr, 'sara@example.sa')
    })

    await test.step('the bidder has no bid box in a hall auction', async () => {
      // She is in the room with a paddle. A bid box on her phone would let her bid
      // past an auctioneer who is calling a different price, and the catcher would
      // refuse it with NotTheClerk — so the portal must not offer it at all.
      //
      // The notice is asserted first, and it is the half that does the work. The
      // count on its own was satisfied by the bid box collapsing to "أُغلق المزاد"
      // while the auction is still Scheduled, which made this step a test of
      // whether qualification had finished within the two minutes before the open
      // rather than of the channel. It went red the first time it did not. The
      // notice renders on the channel and not on the clock, so the step now holds
      // whenever it runs.
      await expect(sara.page.getByText('المزايدة تجري في القاعة')).toBeVisible({
        timeout: 20_000,
      })
      await expect(sara.page.getByRole('button', { name: 'إرسال المزايدة' })).toHaveCount(0)
    })

    await test.step('the clerk enters a bid for a bidder in the room', async () => {
      const page = clerk.page
      await openAuction(page, auctionId)

      const amount = page.getByLabel('مبلغ المزايدة')
      await expect(amount).toBeVisible({ timeout: 60_000 })

      // A paddle number, because that is what the room holds up.
      await page.getByLabel('رقم المجداف').fill('1')
      await expect(page.getByText(/^المزايد: /)).toBeVisible()

      const send = page.getByRole('button', { name: 'تسجيل المزايدة' })
      await amount.fill('1000000.00')
      await expect(send).toBeEnabled()

      // Waits for the catcher to have replayed both the auction and the clerk
      // assignment, which arrive on compacted topics on their own schedule. Each
      // early attempt comes back 409 "المزاد غير معروف لخدمة المزايدة بعد" — the
      // product's own answer, and what a real clerk would see if they started
      // typing the moment the auction opened.
      await expect(async () => {
        await send.click()
        await expect(page.getByRole('cell', { name: arabicRiyals(100_000_000) })).toBeVisible({
          timeout: 5_000,
        })
      }).toPass({ timeout: 120_000 })

      // Those refusals are counted rather than ignored: from here on the clerk's
      // browser must report nothing new, so a 409 that is a real refusal still
      // fails the walk-through.
      warmUpRefusals = clerk.problems.httpFailures.length
    })

    await test.step('an unknown paddle number selects nobody', async () => {
      const page = clerk.page
      await page.getByLabel('رقم المجداف').fill('99')

      await expect(page.getByText('لا يوجد مزايد مؤهَّل بهذا الرقم.')).toBeVisible()
      await expect(page.getByRole('button', { name: 'تسجيل المزايدة' })).toBeDisabled()
    })

    await test.step('the clerk extends and then brings the hammer down', async () => {
      const page = clerk.page

      await page.getByRole('button', { name: 'تمديد دقيقتين' }).click()
      await page.getByRole('button', { name: 'إغلاق المزاد' }).click()

      // The close travels to the processor over auctions.lifecycle and comes back
      // as a candidate for the committee — the same الترسية workflow as online.
      //
      // Re-opened rather than waited on: the admin portal does not poll, so a page
      // left on the auction would sit there showing the state it loaded with
      // however long the processor took.
      await expect(async () => {
        await openAuction(committee.page, auctionId)
        await expect(committee.page.getByRole('heading', { name: 'المرشّح' })).toBeVisible({
          timeout: 10_000,
        })
      }).toPass({ timeout: 180_000 })
    })

    await test.step('the committee is offered the hall winner', async () => {
      // The amount the clerk typed, having travelled through the catcher, the bid
      // log and the processor's ladder to the committee's screen.
      await expect(committee.page.locator('.big-number').first()).toContainText(
        arabicRiyals(100_000_000),
      )
    })

    for (const [who, problems] of [
      ['admin-user', admin.problems],
      ['committee-user', committee.problems],
      ['sara', sara.problems],
    ] as const) {
      expect(problems.all(), `${who}'s browser reported: ${problems.all().join(' | ')}`).toEqual([])
    }

    // The clerk's own count, net of the warm-up refusals above.
    expect(clerk.problems.exceptions).toEqual([])
    expect(clerk.problems.consoleErrors).toEqual([])
    expect(
      clerk.problems.httpFailures.slice(warmUpRefusals),
      `the clerk's browser reported: ${clerk.problems.httpFailures.slice(warmUpRefusals).join(' | ')}`,
    ).toEqual([])

    // The clerk commits nothing of their own: they type for other people, and the
    // money was committed when the bidder paid their deposit.
    expect(clerk.problems.stepUpChallenges).toEqual([])
  } finally {
    await Promise.all([admin.close(), committee.close(), clerk.close(), sara.close()])
  }
})
