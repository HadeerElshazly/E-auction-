import { useState } from 'react'
import { parseRiyals } from '@eauction/shared'
import { Modal } from './PlotsPanel'

/** What «إضافة مزاد» sends: the draft, its terms and its plot, saved together. */
export interface NewAuctionBody {
  nameAr: string
  nameEn: string
  minIncrementMinorUnits: number
  terms: Record<string, unknown>
  plot: Record<string, unknown>
}

const localDateTime = (t: number) => {
  const d = new Date(t)
  return new Date(t - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}

/**
 * «إضافة مزاد» — the prototype's short form: one auction is one plot, so the plot's
 * number and area go in with the price, the deposit, the step and the schedule, and
 * the draft is saved. Everything else — the reserve, the booklet and its price, the
 * plot's location and use, the photos — is completed on the auction's page, where
 * the draft opens next.
 */
export function NewAuction({
  busy,
  onClose,
  onCreate,
}: {
  busy: boolean
  onClose: () => void
  onCreate: (body: NewAuctionBody) => void
}) {
  const [number, setNumber] = useState('')
  const [area, setArea] = useState('')
  const [opening, setOpening] = useState('')
  const [deposit, setDeposit] = useState('')
  const [increment, setIncrement] = useState('')
  const [hours, setHours] = useState('24')
  const [start, setStart] = useState(() => localDateTime(Date.now() + 3600_000))

  const money = { opening: parseRiyals(opening), deposit: parseRiyals(deposit), increment: parseRiyals(increment) }
  const startsAt = new Date(start).getTime()
  const problem =
    number.trim() === '' || !(Number(area) > 0)
      ? 'أدخل رقم القطعة ومساحتها.'
      : !money.opening || money.opening <= 0
        ? 'سعر البداية مطلوب.'
        : !money.deposit || money.deposit <= 0
          ? 'مبلغ التأمين مطلوب.'
          : !money.increment || money.increment <= 0
            ? 'زيادة المزايدة مطلوبة.'
            : !(Number(hours) > 0)
              ? 'مدة المزاد بالساعات مطلوبة.'
              : !Number.isFinite(startsAt) || startsAt <= Date.now()
                ? 'اختر وقت بداية في المستقبل.'
                : null

  const submit = () => {
    const plotNumber = number.trim()
    const nameAr = `قطعة ${plotNumber}`
    const nameEn = `Plot ${plotNumber}`
    onCreate({
      nameAr,
      nameEn,
      minIncrementMinorUnits: money.increment!,
      terms: {
        nameAr,
        nameEn,
        channel: 'Online',
        bidderVisibility: 'Masked',
        startsAt: new Date(startsAt).toISOString(),
        endsAt: new Date(startsAt + Number(hours) * 3600_000).toISOString(),
        openingPriceMinorUnits: money.opening,
        // Set on the auction's page, before it goes for approval.
        reservePriceMinorUnits: null,
        minIncrementMinorUnits: money.increment,
        depositMinorUnits: money.deposit,
        brokerageFeePercent: 2.5,
        bookletPriceMinorUnits: 0,
        quietPeriodSeconds: null,
        maxExtensions: 0,
        phase: null,
      },
      plot: {
        plotNumber,
        areaSqm: Number(area),
        latitude: null,
        longitude: null,
        descriptionAr: null,
        descriptionEn: null,
        streetWidthMeters: null,
        frontageMeters: null,
        landUse: 'Residential',
        facing: null,
      },
    })
  }

  const money2 = (label: string, value: string, set: (v: string) => void) => (
    <label>
      <span>{label}</span>
      <input className="ltr num" inputMode="decimal" value={value} onChange={(e) => set(e.target.value)} aria-label={label} />
    </label>
  )

  return (
    <Modal title="إضافة مزاد جديد" onClose={onClose}>
      <p className="muted small" style={{ margin: '0 0 16px' }}>يُحفظ المزاد كمسودة قبل نشره.</p>
      <div className="form-row-2">
        <label>
          <span>رقم القطعة</span>
          <input className="ltr" value={number} onChange={(e) => setNumber(e.target.value)} aria-label="رقم القطعة" />
        </label>
        <label>
          <span>المساحة بالمتر المربع</span>
          <input className="ltr num" inputMode="decimal" value={area} onChange={(e) => setArea(e.target.value)} aria-label="المساحة بالمتر المربع" />
        </label>
        {money2('سعر البداية', opening, setOpening)}
        {money2('التأمين', deposit, setDeposit)}
        {money2('زيادة المزايدة', increment, setIncrement)}
        <label>
          <span>مدة المزاد بالساعات</span>
          <input className="ltr num" inputMode="numeric" value={hours} onChange={(e) => setHours(e.target.value)} aria-label="مدة المزاد بالساعات" />
        </label>
      </div>
      <label style={{ display: 'block', marginTop: 12 }}>
        <span>بداية المزاد</span>
        <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} aria-label="بداية المزاد" style={{ width: '100%' }} />
      </label>

      <div className="notice info small" style={{ marginTop: 16 }}>
        بعد الحفظ تُكمَل بقية البيانات في صفحة المزاد: السعر الاحتياطي، كراسة الشروط، موقع القطعة واستخدامها، والصور.
      </div>
      {problem && <p className="muted small">{problem}</p>}

      <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16, gap: 8 }}>
        <button onClick={onClose}>إلغاء</button>
        <button className="primary" disabled={busy || problem !== null} onClick={submit}>
          حفظ المسودة
        </button>
      </div>
    </Modal>
  )
}
