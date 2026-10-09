import { useEffect, useRef, useState } from 'react'
import { api, config, parseRiyals, readEventStream, riyals, sar, when, type Api, type Session } from '@eauction/shared'
import type { Auction } from './types'
import { amendmentLabel, canEditNow, isUpcoming, label } from './types'
import { BidderName, useLeaders } from './winners'

interface Props {
  auction: Auction
  client: Api
  busy: boolean
  canEdit: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}

/**
 * «تفاصيل المزاد» — the terms, the files and the booklet, edited while the auction
 * is a draft and, as an amendment the committee approves again, while it is
 * upcoming (§6.5). The name and the phase are on «تفاصيل القطعة», with the land.
 */
export function AuctionEditor({
  auction,
  client,
  session,
  busy,
  canEdit,
  onAct,
}: Props & { session: Session }) {
  const l = label(auction.status)
  // Editing is confined to a draft and to an upcoming auction without a pending
  // amendment, as the domain enforces.
  const open = canEditNow(auction)
  const amending = amendmentLabel(auction.amendment)

  return (
    <div className="card">
      <div className="row" style={{ marginBottom: 14 }}>
        <h2 style={{ margin: 0 }}>{auction.nameAr}</h2>
        <span className={`pill ${l.tone}`}>{l.ar}</span>
        {amending && <span className="pill wait">{amending}</span>}
        <span className="grow" />
        <span className="muted small">
          رقم المزاد <b className="num">{auction.number}</b>
        </span>
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

      {/* The read-only summary lives on «تفاصيل القطعة»; here only what can be
          changed, and the approval workflow. */}
      {open && canEdit && isUpcoming(auction) && (
        <div className="notice info small">
          تعديل مزاد منشور: ما يُحفظ هنا لا يظهر للمزايدين حتى تعتمده لجنة الترسية من جديد، ويُبلَّغ المتقدمون بالتعديل
          عند اعتماده. لا يتغيّر مبلغ التأمين ولا سعر الكراسة.
        </div>
      )}
      {open && canEdit && <Details auction={auction} client={client} busy={busy} onAct={onAct} />}
      {!(open && canEdit) && (
        <p className="muted small">
          {auction.status === 'PendingReview'
            ? 'بيانات المزاد معروضة في «تفاصيل القطعة». لا تُعدَّل بعد رفعه للاعتماد؛ الإجراءات المتاحة أدناه.'
            : auction.amendment === 'PendingReview'
              ? 'التعديل بانتظار اعتماد لجنة الترسية؛ لا يُعدَّل المزاد حتى تبتّ فيه.'
              : 'بيانات المزاد معروضة في «تفاصيل القطعة». لا تُعدَّل بعد بدء المزاد؛ الإجراءات المتاحة أدناه.'}
        </p>
      )}

      {auction.channel === 'Onsite' && (
        <Clerk auction={auction} client={client} busy={busy} canEdit={canEdit} onAct={onAct} />
      )}

      {/* قطع الأرض are added on «تفاصيل القطعة», each a row with its details. */}


      <Documents
        auction={auction}
        client={client}
        session={session}
        busy={busy}
        canEdit={open && canEdit}
        onAct={onAct}
      />

      <Workflow auction={auction} />
    </div>
  )
}

/**
 * The auction at a glance, once it can no longer be edited: when it runs, what it
 * costs to take part, and how it is set up — in that order, because that is the
 * order the committee and the clerk ask about it in.
 */
export function Summary({ auction, session }: { auction: Auction; session: Session }) {
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
        <Stat label="سعر البداية" value={sar(auction.openingPriceMinorUnits, 'ar')} />
        <Stat label="زيادة المزايدة" value={sar(auction.minIncrementMinorUnits, 'ar')} sub="ما تضيفه كل ضغطة زيادة" />
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
          <span className="num">{auction.totalAreaSqm}</span> م²
        </span>
        {auction.phase && <span>{auction.phase}</span>}
      </div>
    </div>
  )
}

export interface PublicPrice {
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
export function usePublicPrice(auction: Auction, session?: Session | null): PublicPrice | null {
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

  // While live, also follow the pushed stream: a bid shows the moment the service
  // has it, not at the next poll. The poll above stays as the fallback.
  useEffect(() => {
    if (!live || !session) return
    const controller = new AbortController()
    void readEventStream({
      url: `${config.queryApi}/auctions/${auction.id}/stream`,
      token: session.accessToken,
      signal: controller.signal,
      onEvent: (event, data) => {
        if (event !== 'snapshot' && event !== 'price') return
        try {
          setFigures(JSON.parse(data) as PublicPrice)
        } catch {
          // A frame this page cannot read; the poll still brings the figures.
        }
      },
    })
    return () => controller.abort()
  }, [auction.id, live, session])

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
function Clerk({ auction, client, busy, canEdit, onAct }: Props) {
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

function Details({ auction, client, busy, onAct }: Omit<Props, 'canEdit'>) {
  // Published: the figures bidders paid on cannot move (the server refuses them too).
  const locked = isUpcoming(auction)
  // Local form state, reseeded when a DIFFERENT auction is opened — keyed on the id,
  // not on the auction object.
  //
  // Depending on the object reseeds on every refresh, and the editor refreshes after
  // every action anywhere in it: adding a plot or attaching the booklet would wipe
  // the dates and prices the clerk had just typed and not yet saved. Worse, a save
  // that raced a refresh sent the reseeded defaults instead of the typed values, so
  // the auction came back with an end date before its start date.
  const [form, setForm] = useState(() => toForm(auction))
  // What was last saved, to tell whether there is anything to save. Not the auction
  // object: the reserve is write-only and never comes back, so a typed reserve would
  // look unsaved for ever.
  const [baseline, setBaseline] = useState(() => toForm(auction))
  useEffect(() => {
    setForm(toForm(auction))
    setBaseline(toForm(auction))
  }, [auction.id])
  const dirty = JSON.stringify(form) !== JSON.stringify(baseline)

  const [problems, setProblems] = useState<string[]>([])
  // «فحص البيانات»: what is still missing before the auction can go for approval.
  const [readiness, setReadiness] = useState<string[] | null>(null)

  const amounts: Array<[keyof FormState, string]> = [
    ['opening', 'سعر البداية'],
    ['increment', 'زيادة المزايدة'],
    ['deposit', 'التأمين'],
    ['booklet', 'سعر الكراسة'],
  ]

  const persist = async () => {
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
        // The name and the phase are edited on «تفاصيل القطعة»; the terms endpoint
        // still takes them, so the current ones go back unchanged.
        nameAr: auction.nameAr,
        nameEn: auction.nameEn,
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
        phase: auction.phase,
      })
      setBaseline(form)
  }

  const save = () => onAct(persist)

  // Saves what was typed, then asks the service what is still missing — the check is
  // of the saved auction, so it must not run on a stale copy.
  const check = () =>
    onAct(async () => {
      await persist()
      setReadiness((await client.get<{ problems: string[] }>(`/auctions/${auction.id}/validation`)).problems)
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
            disabled={locked}
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

        {amounts.map(([key, text]) => {
          // What bidders paid on: the deposit and the booklet fee stay as published.
          const frozen = locked && (key === 'deposit' || key === 'booklet')
          return (
            <label key={String(key)}>
              <span>
                {text} <span className="muted">(ر.س)</span>
              </span>
              <input
                className="ltr num"
                inputMode="decimal"
                value={String(form[key])}
                disabled={frozen}
                onChange={(e) => setForm({ ...form, [key]: e.target.value })}
              />
              {frozen && <small className="muted">لا يتغيّر بعد نشر المزاد.</small>}
            </label>
          )
        })}

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
          <small className="muted">
            أدنى سعر يجوز خفض سعر البداية إليه عند إعادة الطرح — لا يزيد على سعر البداية.
          </small>
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

      {readiness !== null && (
        <div className={`notice ${readiness.length === 0 ? 'ok' : 'error'}`}>
          {readiness.length === 0 ? (
            'البيانات مكتملة — يمكن إرسال المزاد للاعتماد من المربع الجانبي.'
          ) : (
            <>
              يلزم استكمال ما يلي:
              <ul>
                {readiness.map((p) => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}

      {/* Save and check together, held at the bottom of the screen while the form
          scrolls, and live only when something was changed. */}
      <div className="form-actions-bar">
        <button className="primary" disabled={busy || !dirty} onClick={save}>
          حفظ البيانات
        </button>
        <button disabled={busy || !dirty} onClick={check}>
          فحص البيانات
        </button>
        <span className="muted small">{dirty ? 'تعديلات غير محفوظة' : 'لا تعديلات غير محفوظة'}</span>
      </div>
    </>
  )
}

interface FormState {
  channel: string
  bidderVisibility: string
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
    channel: a.channel,
    bidderVisibility: a.bidderVisibility ?? 'Masked',
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

/**
 * The booklet an auction is prepared with, and its public attachments. The cover
 * image is set on the photo at the top of the auction page (CoverPhoto).
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
}: Props & { session: Session }) {
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
        {/* صورة الغلاف is set on the photo at the top of the auction page. */}
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
  // The documents; the photos are on «معرض الصور».
  const attachments = (auction.attachments ?? []).filter((d) => d.kind !== 'Photo')

  const upload = (file: File) =>
    onAct(async () => {
      const uploaded = await documentsApi.upload<{ id: string }>('/documents', file, {
        access: 'Public',
      })
      await client.post(`/auctions/${auction.id}/attachments`, {
        documentId: uploaded.id,
        titleAr: title.trim() || file.name,
        // A document: «المستندات المرفقة» in the visitor settings decides who sees it.
        kind: 'Document',
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

/**
 * Where the auction stands with the committee, when it is with them. Withdrawing an
 * upcoming auction is «حذف المزاد» in the side box, with the other decisions.
 */
function Workflow({ auction }: Pick<Props, 'auction'>) {
  const pendingNew = auction.status === 'PendingReview'
  const pendingAmendment = isUpcoming(auction) && auction.amendment === 'PendingReview'
  if (!pendingNew && !pendingAmendment) return null

  return (
    <>
      <h3>سير العمل</h3>
      <div className="row">
        <span className="muted small">
          {pendingNew
            ? 'بانتظار لجنة الترسية — يُعتمد المزاد أو يُرفض من صفحته، ولا يعتمد مُعدّ المزاد مزاده بنفسه.'
            : 'التعديل بانتظار لجنة الترسية — يُعتمد فيُنشر للمزايدين ويُبلَّغون به، أو يُرفض بالسبب فيعود للتعديل. يبقى ما نُشر كما هو حتى ذلك.'}
        </span>
      </div>
    </>
  )
}
