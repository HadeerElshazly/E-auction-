import { useMemo, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Icon, LAND_USES, PlotMap, landUseAr, type Api } from '@eauction/shared'
import type { Auction } from './types'

type Plot = Auction['plots'][number]

const located = (p: Plot) =>
  !!p.latitude && !!p.longitude && !isNaN(Number(p.latitude)) && !isNaN(Number(p.longitude))

/** On the page itself, so the top bar and the side box cannot sit over it. */
function Modal({ title, sub, onClose, children }: { title: string; sub?: string; onClose: () => void; children: ReactNode }) {
  return createPortal(
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={title}>
      <div className="modal">
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

/**
 * قطع الأرض on the auction page: one row per plot with «تفاصيل», the map of all of
 * them above, and — while the auction is being prepared — «إضافة قطعة» in a dialog
 * and «إزالة» on each row.
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
  const [adding, setAdding] = useState(false)
  const [shown, setShown] = useState<Plot | null>(null)
  const points = useMemo(
    () =>
      auction.plots
        .filter(located)
        .map((p) => ({ lat: Number(p.latitude), lng: Number(p.longitude), label: `قطعة ${p.plotNumber}` })),
    [auction.plots],
  )

  return (
    <>
      <div className="panel-title">
        <h3 style={{ margin: 0 }}>قطع الأرض ({auction.plots.length})</h3>
        {canEdit && (
          <button className="primary small" disabled={busy} onClick={() => setAdding(true)}>
            <Icon name="plus" size={16} /> إضافة قطعة
          </button>
        )}
      </div>
      <p className="muted small" style={{ marginTop: 4 }}>تُباع القطع كوحدة واحدة — المزايدة على المزاد كاملاً.</p>

      {points.length > 0 && <PlotMap points={points} height={240} />}

      {auction.plots.length > 0 ? (
        <div className="table-scroll" style={{ marginTop: 12 }}>
          <table data-testid="plots-table">
            <thead>
              <tr>
                <th>رقم القطعة</th>
                <th>المساحة</th>
                <th>الاستخدام</th>
                <th>الموقع</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {auction.plots.map((p) => (
                <tr key={p.id}>
                  <td><span className="num strong">{p.plotNumber}</span></td>
                  <td><span className="num">{p.areaSqm}</span> م²</td>
                  <td>{landUseAr(p.landUse)}</td>
                  <td>{located(p) ? 'محدد على الخريطة' : <span className="muted">غير محدد</span>}</td>
                  <td className="row-actions">
                    <button className="small" onClick={() => setShown(p)}>
                      تفاصيل
                    </button>
                    {canEdit && (
                      <button
                        className="ghost small"
                        disabled={busy}
                        onClick={() => {
                          if (!window.confirm(`إزالة القطعة ${p.plotNumber} من المزاد؟`)) return
                          void onAct(() => client.del(`/auctions/${auction.id}/plots/${p.id}`))
                        }}
                      >
                        إزالة
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="muted">
          لم تُضف قطع بعد.{canEdit && ' أضف قطعة واحدة على الأقل قبل رفع المزاد للاعتماد.'}
        </p>
      )}

      {shown && <PlotDetails plot={shown} onClose={() => setShown(null)} />}
      {adding && (
        <AddPlot
          auction={auction}
          others={points}
          busy={busy}
          onClose={() => setAdding(false)}
          onAdd={(body) =>
            onAct(async () => {
              await client.post(`/auctions/${auction.id}/plots`, body)
              setAdding(false)
            })
          }
        />
      )}
    </>
  )
}

/** «تفاصيل» — everything recorded about one plot, with its place on the map. */
function PlotDetails({ plot, onClose }: { plot: Plot; onClose: () => void }) {
  const metres = (v: number | null) => (v != null ? <><span className="num">{v}</span> متر</> : 'غير محدد')
  return (
    <Modal title={`قطعة رقم ${plot.plotNumber}`} sub="تفاصيل القطعة" onClose={onClose}>
      <div className="spec-grid" style={{ marginTop: 0 }}>
        <div><small>رقم القطعة</small><b className="num">{plot.plotNumber}</b></div>
        <div><small>المساحة</small><b><span className="num">{plot.areaSqm}</span> م²</b></div>
        <div><small>الاستخدام</small><b>{landUseAr(plot.landUse)}</b></div>
        <div><small>عرض الشارع</small><b>{metres(plot.streetWidthMeters)}</b></div>
        <div><small>الواجهة</small><b>{metres(plot.frontageMeters)}</b></div>
        <div>
          <small>الإحداثيات</small>
          <b>{located(plot) ? <span className="ltr num">{plot.latitude}, {plot.longitude}</span> : 'غير محدد'}</b>
        </div>
      </div>
      {plot.descriptionAr && (
        <>
          <h3>الوصف</h3>
          <p style={{ marginTop: 0 }}>{plot.descriptionAr}</p>
        </>
      )}
      {located(plot) && (
        <div style={{ marginTop: 14 }}>
          <PlotMap
            points={[{ lat: Number(plot.latitude), lng: Number(plot.longitude), label: `قطعة ${plot.plotNumber}` }]}
            height={260}
          />
        </div>
      )}
      <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16 }}>
        <button onClick={onClose}>إغلاق</button>
      </div>
    </Modal>
  )
}

/** «إضافة قطعة» — the form, and the map to place the plot by clicking it. */
function AddPlot({
  auction,
  others,
  busy,
  onClose,
  onAdd,
}: {
  auction: Auction
  others: Array<{ lat: number; lng: number; label?: string }>
  busy: boolean
  onClose: () => void
  onAdd: (body: unknown) => Promise<void>
}) {
  const [number, setNumber] = useState('')
  const [area, setArea] = useState('')
  const [streetWidth, setStreetWidth] = useState('')
  const [frontage, setFrontage] = useState('')
  const [use, setUse] = useState<string>('Residential')
  const [lat, setLat] = useState('')
  const [lng, setLng] = useState('')
  const [description, setDescription] = useState('')
  const picked =
    lat !== '' && lng !== '' && !isNaN(Number(lat)) && !isNaN(Number(lng)) ? { lat: Number(lat), lng: Number(lng) } : null
  const ready = number.trim() !== '' && Number(area) > 0

  return (
    <Modal title="إضافة قطعة" sub={auction.nameAr} onClose={onClose}>
      <div className="grid">
        <label>
          <span>رقم القطعة</span>
          <input className="ltr" value={number} onChange={(e) => setNumber(e.target.value)} autoFocus />
        </label>
        <label>
          <span>المساحة (م²)</span>
          <input className="ltr num" inputMode="decimal" value={area} onChange={(e) => setArea(e.target.value)} />
        </label>
        <label>
          <span>الاستخدام</span>
          <select value={use} onChange={(e) => setUse(e.target.value)} aria-label="استخدام القطعة">
            {LAND_USES.map(([key, ar]) => (
              <option key={key} value={key}>
                {ar}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>عرض الشارع (م)</span>
          <input className="ltr num" inputMode="decimal" value={streetWidth} onChange={(e) => setStreetWidth(e.target.value)} />
        </label>
        <label>
          <span>الواجهة (م)</span>
          <input className="ltr num" inputMode="decimal" value={frontage} onChange={(e) => setFrontage(e.target.value)} />
        </label>
      </div>
      <label style={{ display: 'block', marginTop: 12 }}>
        <span>الوصف</span>
        <textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)} style={{ width: '100%' }} />
      </label>

      <h3>الموقع</h3>
      <p className="muted small" style={{ marginTop: -4 }}>انقر على الخريطة لتحديد موقع القطعة، أو أدخل الإحداثيات.</p>
      <PlotMap
        points={others}
        picked={picked}
        height={280}
        onPick={(p) => {
          setLat(String(p.lat))
          setLng(String(p.lng))
        }}
      />
      <div className="grid" style={{ marginTop: 10 }}>
        <label>
          <span>خط العرض</span>
          <input className="ltr num" value={lat} onChange={(e) => setLat(e.target.value)} />
        </label>
        <label>
          <span>خط الطول</span>
          <input className="ltr num" value={lng} onChange={(e) => setLng(e.target.value)} />
        </label>
      </div>

      <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16, gap: 8 }}>
        <button onClick={onClose}>تراجع</button>
        <button
          className="primary"
          disabled={busy || !ready}
          onClick={() =>
            void onAdd({
              plotNumber: number.trim(),
              areaSqm: Number(area),
              latitude: lat || null,
              longitude: lng || null,
              descriptionAr: description.trim() || null,
              descriptionEn: null,
              // Left out rather than sent as 0: the domain refuses a non-positive
              // measurement, and an empty box means "not surveyed yet".
              streetWidthMeters: streetWidth.trim() === '' ? null : Number(streetWidth),
              frontageMeters: frontage.trim() === '' ? null : Number(frontage),
              landUse: use,
            })
          }
        >
          إضافة القطعة
        </button>
      </div>
    </Modal>
  )
}
