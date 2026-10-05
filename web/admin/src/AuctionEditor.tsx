import { useEffect, useState } from 'react'
import { parseRiyals, riyals, sar, type Api } from '@eauction/shared'
import type { Auction } from './types'
import { label } from './types'

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

export function AuctionEditor({ auction, client, busy, canEdit, canApprove, onAct }: Props) {
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

      {open && canEdit ? (
        <Details auction={auction} client={client} busy={busy} onAct={onAct} />
      ) : (
        <Summary auction={auction} />
      )}

      <Plots auction={auction} client={client} busy={busy} canEdit={open && canEdit} onAct={onAct} />

      <Documents auction={auction} client={client} busy={busy} canEdit={open && canEdit} onAct={onAct} />

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

function Summary({ auction }: { auction: Auction }) {
  return (
    <div className="grid">
      <Fact k="القناة" v={auction.channel === 'Online' ? 'إلكتروني' : 'حضوري'} />
      <Fact k="سعر الافتتاح" v={sar(auction.openingPriceMinorUnits, 'ar')} />
      <Fact k="الحد الأدنى للمزايدة" v={sar(auction.minIncrementMinorUnits, 'ar')} />
      <Fact k="التأمين" v={sar(auction.depositMinorUnits, 'ar')} />
      <Fact k="سعر الكراسة" v={sar(auction.bookletPriceMinorUnits, 'ar')} />
      <Fact k="نسبة السعي" v={`${auction.brokerageFeePercent}%`} />
      <Fact
        k="البداية"
        v={auction.startsAt ? new Date(auction.startsAt).toLocaleString('ar-SA') : '—'}
      />
      <Fact
        k="النهاية"
        v={auction.endsAt ? new Date(auction.endsAt).toLocaleString('ar-SA') : '—'}
      />
      <Fact
        k="فترة التمديد"
        v={
          auction.quietPeriodSeconds
            ? `${auction.quietPeriodSeconds} ث × ${auction.maxExtensions}`
            : 'معطّلة'
        }
      />
      <Fact k="المساحة الإجمالية" v={`${auction.totalAreaSqm} م²`} />
      {/* The reserve price is deliberately absent: it never leaves the processor
          (D-23), so this portal cannot show it and does not try. */}
    </div>
  )
}

function Fact({ k, v }: { k: string; v: string }) {
  return (
    <div>
      <div className="muted small">{k}</div>
      <div className="num">{v}</div>
    </div>
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
        </label>
        <label>
          <span>نهاية المزاد</span>
          <input
            type="datetime-local"
            className="ltr"
            value={form.endsAt}
            onChange={(e) => setForm({ ...form, endsAt: e.target.value })}
          />
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
  const [deed, setDeed] = useState('')
  const [area, setArea] = useState('')
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
            <span>رقم الصك</span>
            <input className="ltr" value={deed} onChange={(e) => setDeed(e.target.value)} />
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
            <span>خط العرض</span>
            <input className="ltr num" value={lat} onChange={(e) => setLat(e.target.value)} />
          </label>
          <label>
            <span>خط الطول</span>
            <input className="ltr num" value={lng} onChange={(e) => setLng(e.target.value)} />
          </label>
          <div style={{ alignSelf: 'end' }}>
            <button
              disabled={busy || deed.trim() === '' || Number(area) <= 0}
              onClick={() =>
                onAct(async () => {
                  await client.post(`/auctions/${auction.id}/plots`, {
                    deedNumber: deed.trim(),
                    areaSqm: Number(area),
                    latitude: lat || null,
                    longitude: lng || null,
                    descriptionAr: null,
                    descriptionEn: null,
                  })
                  setDeed('')
                  setArea('')
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

function Documents({
  auction,
  client,
  busy,
  canEdit,
  onAct,
}: Omit<Props, 'canApprove'> & { canEdit: boolean }) {
  return (
    <>
      <h3>المستندات</h3>
      <p className="muted small" style={{ marginTop: -4 }}>
        خدمة المستندات لم تُبنَ بعد، فتُسجَّل هنا كمعرّفات فقط.
      </p>
      <div className="row">
        <span className="small">
          كراسة الشروط: {auction.bookletDocumentId ? '✓' : '—'}
        </span>
        {canEdit && (
          <button
            disabled={busy}
            onClick={() =>
              onAct(() =>
                client.post(`/auctions/${auction.id}/booklet`, {
                  documentId: crypto.randomUUID(),
                }),
              )
            }
          >
            إرفاق كراسة
          </button>
        )}
        <span className="small">صورة الغلاف: {auction.coverImageDocumentId ? '✓' : '—'}</span>
        {canEdit && (
          <button
            disabled={busy}
            onClick={() =>
              onAct(() =>
                client.post(`/auctions/${auction.id}/cover-image`, {
                  documentId: crypto.randomUUID(),
                }),
              )
            }
          >
            إرفاق غلاف
          </button>
        )}
      </div>
    </>
  )
}

function Workflow({ auction, client, busy, canEdit, canApprove, onAct }: Props) {
  const [problems, setProblems] = useState<string[] | null>(null)
  const [rejectReason, setRejectReason] = useState('')

  const check = () =>
    onAct(async () => {
      const result = await client.get<{ problems: string[] }>(
        `/auctions/${auction.id}/validation`,
      )
      setProblems(result.problems)
    })

  return (
    <>
      <h3>سير العمل</h3>

      {problems !== null && (
        <div className={`notice ${problems.length === 0 ? 'ok' : 'error'}`}>
          {problems.length === 0 ? (
            'البيانات مكتملة — يمكن إرسال المزاد للاعتماد.'
          ) : (
            <>
              يلزم استكمال ما يلي:
              <ul>
                {problems.map((p) => (
                  <li key={p} className="ltr">
                    {p}
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}

      <div className="row">
        {canEdit && (
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
    </>
  )
}
