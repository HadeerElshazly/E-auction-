import { useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { FACINGS, Icon, LAND_USES, PlotMap, type Api } from '@eauction/shared'
import type { Auction } from './types'

type Plot = Auction['plots'][number]

const located = (p: { latitude: string | null; longitude: string | null }) =>
  !!p.latitude && !!p.longitude && !isNaN(Number(p.latitude)) && !isNaN(Number(p.longitude))

/** On the page itself, so the top bar and the side box cannot sit over it. */
export function Modal({
  title,
  sub,
  onClose,
  wide,
  children,
}: {
  title: string
  sub?: string
  onClose: () => void
  wide?: boolean
  children: ReactNode
}) {
  return createPortal(
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={title}>
      <div className={`modal${wide ? ' wide' : ''}`}>
        <div className="modal-head">
          <button className="icon-btn" aria-label="إغلاق النافذة" onClick={onClose}>
            ✕
          </button>
          <div className="grow" style={{ textAlign: 'start' }}>
            <h2>{title}</h2>
            {sub && <p>{sub}</p>}
          </div>
        </div>
        {children}
      </div>
    </div>,
    document.body,
  )
}

/** The plot as typed into a form. */
export interface PlotDraft {
  number: string
  area: string
  use: string
  street: string
  frontage: string
  facing: string
  lat: string
  lng: string
  description: string
}

export const emptyPlot: PlotDraft = {
  number: '',
  area: '',
  use: 'Residential',
  street: '',
  frontage: '',
  facing: '',
  lat: '',
  lng: '',
  description: '',
}

const draftOf = (p: Plot): PlotDraft => ({
  number: p.plotNumber,
  area: String(p.areaSqm),
  use: p.landUse ?? 'Residential',
  street: p.streetWidthMeters == null ? '' : String(p.streetWidthMeters),
  frontage: p.frontageMeters == null ? '' : String(p.frontageMeters),
  facing: p.facing ?? '',
  lat: p.latitude ?? '',
  lng: p.longitude ?? '',
  description: p.descriptionAr ?? '',
})

/** An empty measurement is "not surveyed yet"; anything typed must be a positive number. */
const measured = (v: string) => v.trim() === '' || Number(v) > 0

export const plotReady = (d: PlotDraft) =>
  d.number.trim() !== '' && Number(d.area) > 0 && measured(d.street) && measured(d.frontage)

/** The request body auction-admin takes for a plot. */
export const plotBody = (d: PlotDraft) => ({
  plotNumber: d.number.trim(),
  areaSqm: Number(d.area),
  latitude: d.lat || null,
  longitude: d.lng || null,
  descriptionAr: d.description.trim() || null,
  descriptionEn: null,
  // Left out rather than sent as 0: the domain refuses a non-positive measurement,
  // and an empty box means "not surveyed yet".
  streetWidthMeters: d.street.trim() === '' ? null : Number(d.street),
  frontageMeters: d.frontage.trim() === '' ? null : Number(d.frontage),
  landUse: d.use,
  facing: d.facing || null,
})

/** The plot's fields and its place on the map — used by «إضافة مزاد» and «تعديل القطعة». */
export function PlotFields({ value, onChange }: { value: PlotDraft; onChange: (d: PlotDraft) => void }) {
  const set = (patch: Partial<PlotDraft>) => onChange({ ...value, ...patch })
  const picked = located({ latitude: value.lat, longitude: value.lng })
    ? { lat: Number(value.lat), lng: Number(value.lng) }
    : null
  return (
    <>
      <div className="form-row-3">
        <label>
          <span>رقم القطعة</span>
          <input className="ltr" value={value.number} onChange={(e) => set({ number: e.target.value })} />
        </label>
        <label>
          <span>المساحة (م²)</span>
          <input className="ltr num" inputMode="decimal" value={value.area} onChange={(e) => set({ area: e.target.value })} />
        </label>
        <label>
          <span>الاستخدام</span>
          <select value={value.use} onChange={(e) => set({ use: e.target.value })} aria-label="استخدام القطعة">
            {LAND_USES.map(([key, ar]) => (
              <option key={key} value={key}>
                {ar}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>عرض الشارع (م)</span>
          <input className="ltr num" inputMode="decimal" value={value.street} onChange={(e) => set({ street: e.target.value })} />
        </label>
        <label>
          <span>الواجهة</span>
          <select value={value.facing} onChange={(e) => set({ facing: e.target.value })} aria-label="واجهة القطعة">
            <option value="">غير محدد</option>
            {FACINGS.map(([key, ar]) => (
              <option key={key} value={key}>
                {ar}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>طول الواجهة (م)</span>
          <input className="ltr num" inputMode="decimal" value={value.frontage} onChange={(e) => set({ frontage: e.target.value })} />
          {!measured(value.frontage) && <small style={{ color: 'var(--danger)' }}>أدخل رقماً بالمتر.</small>}
        </label>
      </div>
      <label style={{ display: 'block', marginTop: 12 }}>
        <span>الوصف</span>
        <textarea
          rows={2}
          value={value.description}
          onChange={(e) => set({ description: e.target.value })}
          style={{ width: '100%' }}
        />
      </label>
      <p className="muted small" style={{ margin: '12px 0 6px' }}>
        الموقع: انقر على الخريطة لتحديده، أو أدخل الإحداثيات.
      </p>
      <PlotMap points={[]} picked={picked} height={240} onPick={(p) => set({ lat: String(p.lat), lng: String(p.lng) })} />
      <div className="form-row-3" style={{ marginTop: 10 }}>
        <label>
          <span>خط العرض</span>
          <input className="ltr num" value={value.lat} onChange={(e) => set({ lat: e.target.value })} />
        </label>
        <label>
          <span>خط الطول</span>
          <input className="ltr num" value={value.lng} onChange={(e) => set({ lng: e.target.value })} />
        </label>
      </div>
    </>
  )
}

/**
 * The plot's buttons beside «تفاصيل الأرض». The page shows the land's basic figures
 * once; «تفاصيل» opens everything recorded about the plot — description,
 * coordinates, the map — and, while the auction is a draft, «تعديل» changes it (or
 * «إضافة القطعة» adds it, if a draft has none yet).
 */
export function PlotsPanel({
  auction,
  client,
  busy,
  canEdit,
  onAct,
}: {
  auction: Auction
  client: Api
  busy: boolean
  canEdit: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}) {
  const plot = auction.plots[0]
  const [shown, setShown] = useState(false)
  const [editing, setEditing] = useState(false)

  return (
    <div className="row-actions">
      {plot && (
        <button className="small" onClick={() => setShown(true)} data-testid="plot-details">
          عرض الموقع
        </button>
      )}
      {canEdit && (
        <button className={plot ? 'ghost small' : 'primary small'} disabled={busy} onClick={() => setEditing(true)}>
          {plot ? 'تعديل' : <><Icon name="plus" size={16} /> إضافة القطعة</>}
        </button>
      )}

      {shown && plot && <PlotDetails plot={plot} onClose={() => setShown(false)} />}
      {editing && (
        <EditPlot
          initial={plot ? draftOf(plot) : emptyPlot}
          title={plot ? 'تعديل القطعة' : 'إضافة القطعة'}
          sub={auction.nameAr}
          busy={busy}
          onClose={() => setEditing(false)}
          onSave={(d) =>
            onAct(async () => {
              if (plot) await client.put(`/auctions/${auction.id}/plot`, plotBody(d))
              else await client.post(`/auctions/${auction.id}/plots`, plotBody(d))
              setEditing(false)
            })
          }
        />
      )}
    </div>
  )
}

/** «عرض الموقع» — where the plot is: the map and its coordinates. Its figures are on the page. */
function PlotDetails({ plot, onClose }: { plot: Plot; onClose: () => void }) {
  return (
    <Modal title="موقع القطعة" sub={`قطعة رقم ${plot.plotNumber}`} onClose={onClose}>
        {located(plot) ? (
          <>
            <PlotMap points={[{ lat: Number(plot.latitude), lng: Number(plot.longitude), label: `قطعة ${plot.plotNumber}` }]} height={300} />
            <div className="spec-grid" style={{ marginTop: 14 }}>
              <div><small>خط العرض</small><b className="num ltr">{plot.latitude}</b></div>
              <div><small>خط الطول</small><b className="num ltr">{plot.longitude}</b></div>
            </div>
            <p className="small" style={{ margin: 0 }}>
              <a href={`https://www.google.com/maps?q=${plot.latitude},${plot.longitude}`} target="_blank" rel="noreferrer noopener">
                فتح الموقع في خرائط Google ↗
              </a>
            </p>
          </>
        ) : (
          <p className="muted">لم يُحدَّد موقع القطعة على الخريطة بعد.</p>
        )}
      <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16 }}>
        <button onClick={onClose}>إغلاق</button>
      </div>
    </Modal>
  )
}

function EditPlot({
  initial,
  title,
  sub,
  busy,
  onClose,
  onSave,
}: {
  initial: PlotDraft
  title: string
  sub: string
  busy: boolean
  onClose: () => void
  onSave: (d: PlotDraft) => Promise<void>
}) {
  const [draft, setDraft] = useState(initial)
  return (
    <Modal title={title} sub={sub} onClose={onClose}>
      <PlotFields value={draft} onChange={setDraft} />
      <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16, gap: 8 }}>
        <button onClick={onClose}>تراجع</button>
        <button className="primary" disabled={busy || !plotReady(draft)} onClick={() => void onSave(draft)}>
          حفظ القطعة
        </button>
      </div>
    </Modal>
  )
}
