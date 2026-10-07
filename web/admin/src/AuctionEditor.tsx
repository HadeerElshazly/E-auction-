import { useEffect, useRef, useState } from 'react'
import { api, config, parseRiyals, riyals, sar, when, type Api, type Session } from '@eauction/shared'
import type { Auction } from './types'
import { label } from './types'
import { BidderName, useLeaders } from './winners'
import { BidHistory } from './AuditViews'

interface Props {
  auction: Auction
  client: Api
  busy: boolean
  canEdit: boolean
  canApprove: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}

/** Editing is confined to Draft and Rejected, as the domain enforces. */
const editable = new Set(['Draft', 'Rejected'])

export function AuctionEditor({
  auction,
  client,
  session,
  busy,
  canEdit,
  canApprove,
  onAct,
}: Props & { session: Session }) {
  const l = label(auction.status)
  const open = editable.has(auction.status)

  return (
    <div className="card">
      <div className="row" style={{ marginBottom: 14 }}>
        <h2 style={{ margin: 0 }}>{auction.nameAr}</h2>
        <span className={`pill ${l.tone}`}>{l.ar}</span>
        <span className="grow" />
        <code className="muted small">{auction.id}</code>
      </div>

      {auction.rejectionReason && (
        <div className="notice error">سبب الرفض: {auction.rejectionReason}</div>
      )}

      {auction.cancellationReason && (
        <div className="notice error">
          أُلغي المزاد{auction.cancelledAt && <> في {when(auction.cancelledAt)}</>} — السبب:{' '}
          {auction.cancellationReason}
        </div>
      )}

      {open && canEdit ? (
        <Details auction={auction} client={client} busy={busy} onAct={onAct} />
      ) : (
        <Summary auction={auction} session={session} />
      )}

      {auction.channel === 'Onsite' && (
        <Clerk auction={auction} client={client} busy={busy} canEdit={canEdit} onAct={onAct} />
      )}

      <Plots auction={auction} client={client} busy={busy} canEdit={open && canEdit} onAct={onAct} />

      {!['Draft', 'PendingReview', 'Rejected', 'Approved', 'Scheduled'].includes(auction.status) && (
        <details className="card bid-history-card">
          <summary>
            <h2 style={{ display: 'inline' }}>سجل المزايدات</h2>
            <span className="muted small"> — تسلسل المزايدات وقرار المعالج في كل منها (للاطلاع فقط)</span>
          </summary>
          <BidHistory session={session} auctionId={auction.id} withDecisions={false} />
        </details>
      )}

      <Documents
        auction={auction}
        client={client}
        session={session}
        busy={busy}
        canEdit={open && canEdit}
        onAct={onAct}
      />

      <Workflow
        auction={auction}
        client={client}
        busy={busy}
        canEdit={canEdit}
        canApprove={canApprove}
        onAct={onAct}
      />
    </div>
  )
}

/**
 * The auction at a glance, once it can no longer be edited: when it runs, what it
 * costs to take part, and how it is set up — in that order, because that is the
 * order the committee and the clerk ask about it in.
 */
function Summary({ auction, session }: { auction: Auction; session: Session }) {
  const figures = usePublicPrice(auction)
  const leaderId = useLeaders(session)[auction.id]
  const award = auction.currentAward ?? auction.followUpAward
  const ended = figures?.effectiveEndsAt ?? auction.endsAt
  const extension = auction.quietPeriodSeconds
    ? `${auction.quietPeriodSeconds} ثانية، حتى ${auction.maxExtensions} مرات`
    : 'بلا تمديد'

  return (
    <div className="summary">
      <h3>التوقيت</h3>
      {/* Dates without `.num`: they carry Arabic month names and هـ, and an
          isolated left-to-right run scrambles them around the digits. */}
      <div className="timeline">
        <div className="timeline-point">
          <span className="timeline-label">يبدأ</span>
          <span className="timeline-value">{when(auction.startsAt)}</span>
        </div>
        <span className="timeline-arrow" aria-hidden="true">←</span>
        <div className="timeline-point">
          <span className="timeline-label">ينتهي</span>
          <span className="timeline-value">{when(ended)}</span>
          {figures && figures.extensionsUsed > 0 && (
            <span className="timeline-note">
              مُدّد {figures.extensionsUsed} من {figures.maxExtensions}
            </span>
          )}
        </div>
        <div className="timeline-countdown">
          <span className="timeline-label">التمديد عند المزايدة المتأخرة</span>
          <span className="timeline-value">{extension}</span>
        </div>
      </div>

      <h3>الأسعار والرسوم</h3>
      <div className="stat-grid">
        {figures?.priceMinorUnits != null && (
          // From the same source the bidders' screens read, so the price here is
          // the price they see — not a figure this service keeps a copy of.
          <div className="stat highlight">
            <div className="stat-label">
              {figures.status === 'Live' ? 'السعر الحالي' : 'أعلى سعر عند الإغلاق'}
            </div>
            <div className="stat-value num">{sar(figures.priceMinorUnits, 'ar')}</div>
            {(leaderId || figures.leaderLabel) && (
              <div className="stat-sub">
                المزايد الأعلى:{' '}
                {leaderId ? <BidderName session={session} id={leaderId} /> : figures.leaderLabel}
              </div>
            )}
          </div>
        )}
        {award && (
          <div className="stat highlight" data-testid="summary-winner">
            <div className="stat-label">الفائز بالترسية</div>
            <div className="stat-value num">{sar(award.amountMinorUnits, 'ar')}</div>
            <div className="stat-sub">
              <BidderName session={session} id={award.bidderId} />
            </div>
          </div>
        )}
        <Stat label="سعر الافتتاح" value={sar(auction.openingPriceMinorUnits, 'ar')} />
        <Stat label="الحد الأدنى للزيادة" value={sar(auction.minIncrementMinorUnits, 'ar')} />
        <Stat label="التأمين" value={sar(auction.depositMinorUnits, 'ar')} />
        <Stat
          label="سعر الكراسة"
          value={auction.bookletPriceMinorUnits === 0 ? 'مجاناً' : sar(auction.bookletPriceMinorUnits, 'ar')}
        />
        <Stat label="نسبة السعي" value={`${auction.brokerageFeePercent}%`} sub="من سعر الترسية" />
      </div>
      {/* The reserve price is deliberately absent: it never leaves the processor
          (D-23), so this portal cannot show it and does not try. */}

      <h3>الإعدادات</h3>
      <div className="hero-meta" style={{ margin: 0 }}>
        <span>{auction.channel === 'Online' ? 'مزاد إلكتروني' : 'مزاد حضوري'}</span>
        <span>{auction.bidderVisibility === 'Named' ? 'أسماء المزايدين ظاهرة' : 'هوية المزايدين مخفية'}</span>
        <span>
          {auction.plotCount} قطعة · <span className="num">{auction.totalAreaSqm}</span> م²
        </span>
        {auction.phase && <span>{auction.phase}</span>}
      </div>
    </div>
  )
}

interface PublicPrice {
  status: string
  priceMinorUnits: number | null
  leaderLabel: string | null
  effectiveEndsAt: string
  extensionsUsed: number
  maxExtensions: number
}

/**
 * The bidding figures as the public catalogue holds them — the one source both
 * portals read — for an auction that has opened. Polled while it is live; read
 * once after that, when they no longer move.
 */
function usePublicPrice(auction: Auction): PublicPrice | null {
  const [figures, setFigures] = useState<PublicPrice | null>(null)
  const opened = !['Draft', 'PendingReview', 'Rejected', 'Approved', 'Scheduled', 'Cancelled']
    .includes(auction.status)
  const live = auction.status === 'Live'

  useEffect(() => {
    if (!opened) {
      setFigures(null)
      return
    }
    const client = api({ baseUrl: config.queryApi, session: null })
    let stop = false
    const read = () =>
      client
        .get<PublicPrice>(`/auctions/${auction.id}/price`)
        .then((p) => !stop && setFigures(p))
        .catch(() => undefined)
    void read()
    const t = live ? window.setInterval(() => void read(), 3000) : undefined
    return () => {
      stop = true
      if (t) window.clearInterval(t)
    }
  }, [auction.id, opened, live])

  return figures
}

function Stat({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="stat">
      <div className="stat-label">{label}</div>
      <div className="stat-value num">{value}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  )
}

/**
 * Who runs this auction from the floor (§29).
 *
 * Deliberately outside the draft-only editor: a clerk is operational rather than a
 * term of sale, so they can be put on the floor after approval — somebody falls
 * ill, a shift changes, and an approved auction cannot be re-approved to deal with
 * it. Replacing them rotates the signing key, which is why the warning is here and
 * not only in the domain.
 *
 * The identifier is a user id because nothing in this system can list the
 * municipality's staff; a directory lookup belongs here when there is one to call.
 */
function Clerk({ auction, client, busy, canEdit, onAct }: Omit<Props, 'canApprove'>) {
  const [userId, setUserId] = useState(auction.clerkUserId ?? '')

  return (
    <section>
      <h3>موظف القاعة</h3>

      {auction.clerkUserId ? (
        <p className="small">
          معيَّن: <code className="ltr">{auction.clerkUserId}</code>
        </p>
      ) : (
        <p className="muted small">لم يُعيَّن موظف قاعة بعد — لا يمكن إدخال مزايدات.</p>
      )}

      {canEdit && (
        <>
          <div className="row" style={{ alignItems: 'flex-end' }}>
            <label style={{ flex: '1 1 320px', marginBottom: 0 }}>
              <span>معرّف المستخدم (operator)</span>
              <input
                className="ltr num"
                aria-label="معرّف موظف القاعة"
                value={userId}
                onChange={(e) => setUserId(e.target.value)}
              />
            </label>
            <button
              disabled={busy || userId.trim() === ''}
              onClick={() =>
                void onAct(() =>
                  client.put(`/auctions/${auction.id}/clerk`, { clerkUserId: userId.trim() }),
                )
              }
            >
              تعيين
            </button>
            {auction.clerkUserId && (
              <button
                disabled={busy}
                onClick={() => void onAct(() => client.del(`/auctions/${auction.id}/clerk`))}
              >
                إلغاء التعيين
              </button>
            )}
          </div>
          <p className="muted small">
            تغيير الموظف يُبطل مفتاح الموظف السابق فوراً، فتتوقف شاشته عن إدخال
            المزايدات.
          </p>
        </>
      )}
    </section>
  )
}

function Details({ auction, client, busy, onAct }: Omit<Props, 'canEdit' | 'canApprove'>) {
  // Local form state, reseeded when a DIFFERENT auction is opened — keyed on the id,
  // not on the auction object.
  //
  // Depending on the object reseeds on every refresh, and the editor refreshes after
  // every action anywhere in it: adding a plot or attaching the booklet would wipe
  // the dates and prices the clerk had just typed and not yet saved. Worse, a save
  // that raced a refresh sent the reseeded defaults instead of the typed values, so
  // the auction came back with an end date before its start date.
  const [form, setForm] = useState(() => toForm(auction))
  useEffect(() => setForm(toForm(auction)), [auction.id])

  const [problems, setProblems] = useState<string[]>([])

  const amounts: Array<[keyof FormState, string]> = [
    ['opening', 'سعر الافتتاح'],
    ['increment', 'الحد الأدنى للمزايدة'],
    ['deposit', 'التأمين'],
    ['booklet', 'سعر الكراسة'],
  ]

  const save = () =>
    onAct(async () => {
      const money = Object.fromEntries(
        amounts.map(([key]) => [key, parseRiyals(String(form[key]))]),
      ) as Record<string, number | null>

      const bad = amounts.filter(([key]) => money[key] === null).map(([, text]) => text)

      // The reserve is write-only: the server never returns it, so a blank box means
      // "leave it alone" rather than "set it to zero". A value that is present but
      // unparseable is still an error.
      const reserve = form.reserve.trim() === '' ? null : parseRiyals(form.reserve)
      if (form.reserve.trim() !== '' && reserve === null) {
        bad.push('السعر الاحتياطي')
      }

      if (bad.length > 0) {
        setProblems(bad.map((t) => `${t}: مبلغ غير صحيح`))
        throw new Error('')
      }
      setProblems([])

      await client.put(`/auctions/${auction.id}`, {
        nameAr: form.nameAr,
        nameEn: form.nameEn,
        channel: form.channel,
        bidderVisibility: form.bidderVisibility,
        startsAt: new Date(form.startsAt).toISOString(),
        endsAt: new Date(form.endsAt).toISOString(),
        openingPriceMinorUnits: money.opening,
        reservePriceMinorUnits: reserve,
        minIncrementMinorUnits: money.increment,
        depositMinorUnits: money.deposit,
        bookletPriceMinorUnits: money.booklet,
        brokerageFeePercent: Number(form.brokerage),
        quietPeriodSeconds: form.extend ? Number(form.quiet) : null,
        maxExtensions: form.extend ? Number(form.maxExtensions) : 0,
        phase: form.phase || null,
      })
    })

  return (
    <>
      {problems.length > 0 && (
        <div className="notice error">
          <ul>
            {problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="grid">
        <label>
          <span>الاسم (عربي)</span>
          <input value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />
        </label>
        <label>
          <span>الاسم (إنجليزي)</span>
          <input
            className="ltr"
            value={form.nameEn}
            onChange={(e) => setForm({ ...form, nameEn: e.target.value })}
          />
        </label>
        <label>
          <span>القناة</span>
          <select
            value={form.channel}
            onChange={(e) => setForm({ ...form, channel: e.target.value })}
          >
            <option value="Online">إلكتروني</option>
            <option value="Onsite">حضوري</option>
          </select>
        </label>
        <label>
          <span>ظهور المزايدين</span>
          <select
            value={form.bidderVisibility}
            onChange={(e) => setForm({ ...form, bidderVisibility: e.target.value })}
          >
            <option value="Masked">مُخفى — مزايد #1</option>
            <option value="Named">بالاسم</option>
          </select>
          <span className="muted small">
            بالاسم يعني نشر اسم المزايد الأعلى للجميع. لا يمكن تغييره بعد الاعتماد.
          </span>
        </label>
        <label>
          <span>المرحلة</span>
          <input
            className="ltr"
            value={form.phase}
            onChange={(e) => setForm({ ...form, phase: e.target.value })}
            placeholder="phase-1"
          />
        </label>
        <label>
          <span>بداية المزاد</span>
          <input
            type="datetime-local"
            className="ltr"
            value={form.startsAt}
            onChange={(e) => setForm({ ...form, startsAt: e.target.value })}
          />
          {/* The browser's picker is Gregorian and cannot be made Hijri; the
              reading underneath is what bidders will see. */}
          {form.startsAt && <span className="muted small">{when(form.startsAt)}</span>}
        </label>
        <label>
          <span>نهاية المزاد</span>
          <input
            type="datetime-local"
            className="ltr"
            value={form.endsAt}
            onChange={(e) => setForm({ ...form, endsAt: e.target.value })}
          />
          {form.endsAt && <span className="muted small">{when(form.endsAt)}</span>}
        </label>

        {amounts.map(([key, text]) => (
          <label key={String(key)}>
            <span>
              {text} <span className="muted">(ر.س)</span>
            </span>
            <input
              className="ltr num"
              inputMode="decimal"
              value={String(form[key])}
              onChange={(e) => setForm({ ...form, [key]: e.target.value })}
            />
          </label>
        ))}

        <label>
          <span>
            السعر الاحتياطي <span className="muted">(ر.س)</span>
          </span>
          <input
            className="ltr num"
            inputMode="decimal"
            value={form.reserve}
            aria-label="السعر الاحتياطي"
            placeholder="اتركه فارغاً لعدم التغيير"
            onChange={(e) => setForm({ ...form, reserve: e.target.value })}
          />
        </label>

        <label>
          <span>نسبة السعي %</span>
          <input
            className="ltr num"
            inputMode="decimal"
            value={form.brokerage}
            onChange={(e) => setForm({ ...form, brokerage: e.target.value })}
          />
        </label>
      </div>

      <h3>التمديد عند المزايدة المتأخرة</h3>
      <label className="row" style={{ gap: 8 }}>
        <input
          type="checkbox"
          style={{ width: 'auto' }}
          checked={form.extend}
          onChange={(e) => setForm({ ...form, extend: e.target.checked })}
        />
        <span style={{ margin: 0 }}>تمديد وقت الإغلاق عند مزايدة في الدقائق الأخيرة</span>
      </label>

      {form.extend && (
        <div className="grid">
          <label>
            <span>مدة التمديد (ثانية)</span>
            <input
              className="ltr num"
              inputMode="numeric"
              value={form.quiet}
              onChange={(e) => setForm({ ...form, quiet: e.target.value })}
            />
          </label>
          <label>
            <span>أقصى عدد تمديدات</span>
            <input
              className="ltr num"
              inputMode="numeric"
              value={form.maxExtensions}
              onChange={(e) => setForm({ ...form, maxExtensions: e.target.value })}
            />
          </label>
        </div>
      )}

      <div className="row end">
        <button className="primary" disabled={busy} onClick={save}>
          حفظ البيانات
        </button>
      </div>
    </>
  )
}

interface FormState {
  nameAr: string
  nameEn: string
  channel: string
  bidderVisibility: string
  phase: string
  startsAt: string
  endsAt: string
  opening: string
  reserve: string
  increment: string
  deposit: string
  booklet: string
  brokerage: string
  extend: boolean
  quiet: string
  maxExtensions: string
}

function toForm(a: Auction): FormState {
  return {
    nameAr: a.nameAr,
    nameEn: a.nameEn,
    channel: a.channel,
    bidderVisibility: a.bidderVisibility ?? 'Masked',
    phase: a.phase ?? '',
    startsAt: forInput(a.startsAt) || forInput(new Date(Date.now() + 864e5).toISOString()),
    endsAt: forInput(a.endsAt) || forInput(new Date(Date.now() + 1728e5).toISOString()),
    opening: riyals(a.openingPriceMinorUnits || null) === '—' ? '' : riyalsPlain(a.openingPriceMinorUnits),
    // The reserve is write-only here. The server never sends it back, by design, so
    // an edit has to restate it — showing a blank box is more honest than showing a
    // zero that would overwrite the real figure.
    // Left blank deliberately: the server never returns the reserve (D-23), so
    // there is nothing to prefill, and a blank box means "unchanged" on save.
    reserve: '',
    increment: riyalsPlain(a.minIncrementMinorUnits),
    deposit: riyalsPlain(a.depositMinorUnits),
    booklet: riyalsPlain(a.bookletPriceMinorUnits),
    brokerage: String(a.brokerageFeePercent ?? 0),
    extend: a.quietPeriodSeconds !== null,
    quiet: String(a.quietPeriodSeconds ?? 120),
    maxExtensions: String(a.maxExtensions || 3),
  }
}

function riyalsPlain(minor: number): string {
  if (!minor) return ''
  return (minor / 100).toFixed(2)
}

/** datetime-local needs a local-time string with no zone and no seconds. */
function forInput(iso: string | null): string {
  if (!iso) return ''
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

function Plots({
  auction,
  client,
  busy,
  canEdit,
  onAct,
}: Omit<Props, 'canApprove'> & { canEdit: boolean }) {
  const [number, setNumber] = useState('')
  const [area, setArea] = useState('')
  const [streetWidth, setStreetWidth] = useState('')
  const [frontage, setFrontage] = useState('')
  const [lat, setLat] = useState('')
  const [lng, setLng] = useState('')

  return (
    <>
      <h3>
        قطع الأرض ({auction.plotCount}) — تُباع كوحدة واحدة
      </h3>
      <p className="muted small" style={{ marginTop: -4 }}>
        المزايدة على المزاد كاملاً، لا على قطعة بعينها.
      </p>

      {canEdit && (
        <div className="grid">
          <label>
            <span>رقم القطعة</span>
            <input className="ltr" value={number} onChange={(e) => setNumber(e.target.value)} />
          </label>
          <label>
            <span>المساحة (م²)</span>
            <input
              className="ltr num"
              inputMode="decimal"
              value={area}
              onChange={(e) => setArea(e.target.value)}
            />
          </label>
          <label>
            <span>عرض الشارع (م)</span>
            <input
              className="ltr num"
              inputMode="decimal"
              value={streetWidth}
              onChange={(e) => setStreetWidth(e.target.value)}
            />
          </label>
          <label>
            <span>الواجهة (م)</span>
            <input
              className="ltr num"
              inputMode="decimal"
              value={frontage}
              onChange={(e) => setFrontage(e.target.value)}
            />
          </label>
          <label>
            <span>خط العرض</span>
            <input className="ltr num" value={lat} onChange={(e) => setLat(e.target.value)} />
          </label>
          <label>
            <span>خط الطول</span>
            <input className="ltr num" value={lng} onChange={(e) => setLng(e.target.value)} />
          </label>
          <div style={{ alignSelf: 'end' }}>
            <button
              disabled={busy || number.trim() === '' || Number(area) <= 0}
              onClick={() =>
                onAct(async () => {
                  await client.post(`/auctions/${auction.id}/plots`, {
                    plotNumber: number.trim(),
                    areaSqm: Number(area),
                    latitude: lat || null,
                    longitude: lng || null,
                    descriptionAr: null,
                    descriptionEn: null,
                    // Left out rather than sent as 0: the domain refuses a
                    // non-positive measurement, and an empty box means "not
                    // surveyed yet", not "zero metres".
                    streetWidthMeters: streetWidth.trim() === '' ? null : Number(streetWidth),
                    frontageMeters: frontage.trim() === '' ? null : Number(frontage),
                  })
                  setNumber('')
                  setArea('')
                  setStreetWidth('')
                  setFrontage('')
                })
              }
            >
              + إضافة قطعة
            </button>
          </div>
        </div>
      )}
    </>
  )
}

/**
 * The two documents an auction is prepared with: كراسة الشروط and the cover image.
 *
 * They go to the document service first and to auction-admin second, in that
 * order, because the id auction-admin records has to be an id that resolves. The
 * other order — attach, then upload — leaves an auction pointing at a document
 * that does not exist if the upload fails, and the auction looks complete.
 */
function Documents({
  auction,
  client,
  session,
  busy,
  canEdit,
  onAct,
}: Omit<Props, 'canApprove'> & { canEdit: boolean; session: Session }) {
  const documents = api({ baseUrl: config.documentsApi, session })

  return (
    <>
      <h3>المستندات</h3>
      <div className="row">
        <Attach
          label="كراسة الشروط"
          buttonLabel="إرفاق كراسة"
          inputLabel="ملف كراسة الشروط"
          // Restricted: a bidder reads it with a grant the participant service
          // mints once they have paid for it. No role opens it.
          access="Restricted"
          accept="application/pdf"
          attached={auction.bookletDocumentId}
          documentsApi={documents}
          canEdit={canEdit}
          busy={busy}
          onAttach={(documentId) =>
            onAct(() => client.post(`/auctions/${auction.id}/booklet`, { documentId }))
          }
        />
        <Attach
          label="صورة الغلاف"
          buttonLabel="إرفاق غلاف"
          inputLabel="ملف صورة الغلاف"
          // Public: it is on the catalogue an anonymous citizen reads before
          // deciding whether to register at all.
          access="Public"
          accept="image/*"
          attached={auction.coverImageDocumentId}
          documentsApi={documents}
          canEdit={canEdit}
          busy={busy}
          onAttach={(documentId) =>
            onAct(() => client.post(`/auctions/${auction.id}/cover-image`, { documentId }))
          }
        />
      </div>

      <PublicAttachments
        auction={auction}
        client={client}
        documentsApi={documents}
        busy={busy}
        canEdit={canEdit}
        onAct={onAct}
      />
    </>
  )
}

/**
 * المستندات العامة — plans and photographs anyone may download from the catalogue.
 * Uploaded as Public, unlike the booklet, so listing them gives nothing paid away.
 */
function PublicAttachments({
  auction,
  client,
  documentsApi,
  busy,
  canEdit,
  onAct,
}: {
  auction: Auction
  client: Api
  documentsApi: Api
  busy: boolean
  canEdit: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}) {
  const input = useRef<HTMLInputElement>(null)
  const [title, setTitle] = useState('')
  const attachments = auction.attachments ?? []

  const upload = (file: File) =>
    onAct(async () => {
      const uploaded = await documentsApi.upload<{ id: string }>('/documents', file, {
        access: 'Public',
      })
      await client.post(`/auctions/${auction.id}/attachments`, {
        documentId: uploaded.id,
        titleAr: title.trim() || file.name,
      })
      setTitle('')
    })

  return (
    <>
      <h3>المستندات العامة (مخططات، صور القطع…)</h3>
      {attachments.length === 0 ? (
        <p className="muted small">لا توجد مستندات عامة.</p>
      ) : (
        <ul className="doc-list">
          {attachments.map((d) => (
            <li key={d.documentId}>
              <span>{d.titleAr}</span>
              {canEdit && (
                <button
                  className="ghost"
                  disabled={busy}
                  onClick={() =>
                    void onAct(() =>
                      client.del(`/auctions/${auction.id}/attachments/${d.documentId}`),
                    )
                  }
                >
                  حذف
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {canEdit && (
        <div className="row" style={{ marginTop: 10 }}>
          <input
            value={title}
            placeholder="عنوان المستند — مثل: المخطط المعتمد"
            aria-label="عنوان المستند العام"
            style={{ flex: '1 1 260px' }}
            onChange={(e) => setTitle(e.target.value)}
          />
          <input
            ref={input}
            type="file"
            accept="application/pdf,image/*"
            aria-label="ملف المستند العام"
            style={{ position: 'absolute', width: 1, height: 1, opacity: 0 }}
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) void upload(file)
              e.target.value = ''
            }}
          />
          <button disabled={busy} onClick={() => input.current?.click()}>
            إرفاق مستند عام
          </button>
        </div>
      )}
    </>
  )
}

interface AttachProps {
  label: string
  buttonLabel: string
  /**
   * The hidden input's own accessible name, which must differ from the button's.
   *
   * An `input[type=file]` has role `button` in the accessibility tree, so giving
   * both the same name makes `getByRole('button', { name })` ambiguous and every
   * test that presses the button fails strict mode. Found exactly that way.
   */
  inputLabel: string
  access: 'Public' | 'Private' | 'Restricted'
  accept: string
  attached: string | null | undefined
  documentsApi: Api
  canEdit: boolean
  busy: boolean
  onAttach: (documentId: string) => Promise<void>
}

/**
 * One file picker.
 *
 * A hidden input driven by a button rather than a bare `<input type="file">`,
 * because the browser's own control cannot be styled and renders its label in
 * English in the middle of an Arabic form.
 */
function Attach({
  label,
  buttonLabel,
  inputLabel,
  access,
  accept,
  attached,
  documentsApi,
  canEdit,
  busy,
  onAttach,
}: AttachProps) {
  const input = useRef<HTMLInputElement>(null)
  const [name, setName] = useState<string | null>(null)

  const choose = async (file: File) => {
    setName(file.name)
    const uploaded = await documentsApi.upload<{ id: string }>('/documents', file, { access })
    await onAttach(uploaded.id)
  }

  return (
    <>
      <span className="small">
        {label}: {attached ? '✓' : '—'}
        {name !== null && attached === null && <span className="muted"> ({name})</span>}
      </span>
      {canEdit && (
        <>
          <input
            ref={input}
            type="file"
            accept={accept}
            aria-label={inputLabel}
            // Hidden rather than display:none, so the input is still focusable and
            // Playwright can set files on it.
            style={{ position: 'absolute', width: 1, height: 1, opacity: 0 }}
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) void choose(file)
            }}
          />
          <button disabled={busy} onClick={() => input.current?.click()}>
            {buttonLabel}
          </button>
        </>
      )}
    </>
  )
}

function Workflow({ auction, client, busy, canEdit, canApprove, onAct }: Props) {
  const [problems, setProblems] = useState<string[] | null>(null)
  const [rejectReason, setRejectReason] = useState('')
  const [cancelReason, setCancelReason] = useState('')
  const open = editable.has(auction.status)

  const check = () =>
    onAct(async () => {
      const result = await client.get<{ problems: string[] }>(
        `/auctions/${auction.id}/validation`,
      )
      setProblems(result.problems)
    })

  return (
    <>
      {/* Nothing to act on — an awarded or live auction — means no section. */}
      {(open && canEdit) || auction.status === 'PendingReview' ? <h3>سير العمل</h3> : null}

      {problems !== null && open && (
        <div className={`notice ${problems.length === 0 ? 'ok' : 'error'}`}>
          {problems.length === 0 ? (
            'البيانات مكتملة — يمكن إرسال المزاد للاعتماد.'
          ) : (
            <>
              يلزم استكمال ما يلي:
              <ul>
                {problems.map((p) => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}

      <div className="row">
        {/* Only while it can still be edited. The check is "is this ready to be
            submitted", and on an approved or finished auction its rules — a start
            in the future — fail by definition and read as a fault that is not one. */}
        {canEdit && open && (
          <button disabled={busy} onClick={check}>
            فحص البيانات
          </button>
        )}

        {canEdit && editable.has(auction.status) && (
          <button
            className="primary"
            disabled={busy}
            onClick={() => onAct(() => client.post(`/auctions/${auction.id}/submit`))}
          >
            إرسال للاعتماد
          </button>
        )}

        {auction.status === 'PendingReview' &&
          (canApprove ? (
            <>
              <button
                className="primary"
                disabled={busy}
                onClick={() => onAct(() => client.post(`/auctions/${auction.id}/approve`))}
              >
                اعتماد المزاد
              </button>
              <input
                placeholder="سبب الرفض"
                style={{ width: 220 }}
                value={rejectReason}
                onChange={(e) => setRejectReason(e.target.value)}
              />
              <button
                className="danger"
                disabled={busy || rejectReason.trim() === ''}
                onClick={() =>
                  onAct(() =>
                    client.post(`/auctions/${auction.id}/reject`, {
                      reason: rejectReason.trim(),
                    }),
                  )
                }
              >
                رفض
              </button>
            </>
          ) : (
            <span className="muted small">
              بانتظار لجنة الترسية — لا يعتمد مُعدّ المزاد مزاده بنفسه.
            </span>
          ))}
      </div>

      {/* «توثيق الإلغاء المصرح به»: withdrawing an approved auction before it
          opens, with the reason on record and shown to its bidders. */}
      {canEdit && (auction.status === 'Approved' || auction.status === 'Scheduled') && (
        <>
          <h3>إلغاء المزاد</h3>
          <p className="muted small" style={{ marginTop: -4 }}>
            يُتاح قبل بدء المزاد فقط. يُبلَّغ المشتركون، ويُرد التأمين المدفوع أو يُحرَّر الضمان.
          </p>
          <div className="row">
            <input
              placeholder="سبب الإلغاء (يظهر للمشتركين)"
              aria-label="سبب الإلغاء"
              style={{ flex: '1 1 280px' }}
              value={cancelReason}
              onChange={(e) => setCancelReason(e.target.value)}
            />
            <button
              className="danger"
              disabled={busy || cancelReason.trim() === ''}
              onClick={() => {
                if (!window.confirm('إلغاء المزاد نهائي ولا يمكن التراجع عنه. متابعة؟')) return
                void onAct(() =>
                  client.post(`/auctions/${auction.id}/cancel`, { reason: cancelReason.trim() }),
                )
              }}
            >
              إلغاء المزاد
            </button>
          </div>
        </>
      )}
    </>
  )
}
