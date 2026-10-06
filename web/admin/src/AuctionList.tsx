import { useEffect, useState } from 'react'
import { sar, when } from '@eauction/shared'
import type { AuctionListItem } from './types'
import { label } from './types'

interface Props {
  auctions: AuctionListItem[]
  canCreate: boolean
  busy: boolean
  onOpen: (id: string) => void
  onCreate: (nameAr: string, nameEn: string) => void
}

/**
 * المزادات, as a grid of cards.
 *
 * The shape comes from the proposal deck: a cover, the channel it runs on, a
 * countdown straddling the bottom of the image, then the handful of facts a clerk
 * scans. A table was the honest first version and it reads like a database export
 * — an auction is a thing with a place and a clock, and the card is what makes the
 * clock the most prominent thing on it.
 */
export function AuctionList({ auctions, canCreate, busy, onOpen, onCreate }: Props) {
  const [nameAr, setNameAr] = useState('')
  const [nameEn, setNameEn] = useState('')
  const [creating, setCreating] = useState(false)

  return (
    <>
      <div className="page-head">
        <div className="grow">
          <h2>المزادات</h2>
          <p>
            {auctions.length === 0
              ? 'لا توجد مزادات بعد.'
              : `يمكنك إدارة المزادات أو إنشاء مزادات جديدة — ${auctions.length} مزاد.`}
          </p>
        </div>
        {canCreate && !creating && (
          <button className="primary" onClick={() => setCreating(true)}>
            + إنشاء مزاد
          </button>
        )}
      </div>

      {canCreate && creating && (
        <div className="modal-backdrop" role="dialog" aria-modal="true">
          <div className="modal">
            <div className="modal-head">
              <button
                className="icon-btn"
                aria-label="إغلاق النافذة"
                onClick={() => setCreating(false)}
              >
                ✕
              </button>
              <div className="grow" style={{ textAlign: 'start' }}>
                <h2>إنشاء مزاد جديد</h2>
                <p>قم بإدخال بيانات المزاد</p>
              </div>
            </div>

            {/* No step bar, though the proposal's create dialog has one. Theirs
                collects the whole auction in four steps; this one names it and
                hands over to the editor, because an auction cannot be priced or
                scheduled before it has a plot in it. A progress bar that never
                reached step two would be decoration claiming to be a flow. */}
            <p className="muted small" style={{ margin: '0 0 20px' }}>
              يكفي الاسم الآن. القطع والأسعار والجدولة في شاشة المزاد بعد الحفظ.
            </p>

            <div className="grid">
              <label>
                <span>اسم المزاد (عربي)</span>
                <input
                  value={nameAr}
                  onChange={(e) => setNameAr(e.target.value)}
                  placeholder="مخطط السعيد — المرحلة الأولى"
                  aria-label="اسم المزاد بالعربي"
                />
              </label>
              <label>
                <span>اسم المزاد (إنجليزي)</span>
                <input
                  className="ltr"
                  value={nameEn}
                  onChange={(e) => setNameEn(e.target.value)}
                  placeholder="Al-Saeed plan — phase one"
                  aria-label="Auction name in English"
                />
              </label>
            </div>

            <div className="row end" style={{ marginTop: 8 }}>
              <button onClick={() => setCreating(false)}>إلغاء</button>
              <button
                className="primary"
                disabled={busy || nameAr.trim() === '' || nameEn.trim() === ''}
                onClick={() => {
                  onCreate(nameAr.trim(), nameEn.trim())
                  setNameAr('')
                  setNameEn('')
                  setCreating(false)
                }}
              >
                إنشاء مسودة
              </button>
            </div>
          </div>
        </div>
      )}

      {auctions.length === 0 ? (
        <div className="card">
          <p className="muted small" style={{ margin: 0 }}>لا توجد مزادات بعد.</p>
        </div>
      ) : (
        <div className="auction-grid">
          {auctions.map((a) => (
            <AuctionCard key={a.id} auction={a} onOpen={onOpen} />
          ))}
        </div>
      )}
    </>
  )
}

function AuctionCard({
  auction,
  onOpen,
}: {
  auction: AuctionListItem
  onOpen: (id: string) => void
}) {
  const l = label(auction.status)

  return (
    <div
      className="auction-card"
      role="button"
      tabIndex={0}
      data-testid="auction-card"
      aria-label={`فتح ${auction.nameAr}`}
      onClick={() => onOpen(auction.id)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onOpen(auction.id)
        }
      }}
    >
      <div className="cover">
        <span className="channel">
          {auction.channel === 'Onsite' ? '📍 في الموقع' : '🌐 عبر الإنترنت'}
        </span>
        <Countdown startsAt={auction.startsAt} />
      </div>

      <div className="body">
        <p className="title">{auction.nameAr}</p>

        <div className="facts">
          <div>
            <span className={`pill ${l.tone}`}>{l.ar}</span>
          </div>
          <div>
            <span>القطع</span>
            <span className="num">{auction.plotCount}</span>
            <span>·</span>
            <span>سعر الافتتاح</span>
            <span className="num">{sar(auction.openingPriceMinorUnits, 'ar')}</span>
          </div>
          <div>
            <span>تاريخ بدء المزاد</span>
            <span className="num">
              {auction.startsAt
                ? when(auction.startsAt)
                : 'لم يُجدول بعد'}
            </span>
          </div>
          <div className="ltr small">{auction.nameEn}</div>
        </div>
      </div>
    </div>
  )
}

/**
 * The strip over the cover image: days, hours, minutes, seconds to the start.
 *
 * Ticks on its own rather than from a prop, because the list around it refreshes
 * on a five-second poll and a countdown that only moved when the list did would
 * visibly stutter. Stops at zero instead of counting up — once an auction is open
 * the number that matters is on its own screen, not here.
 */
function Countdown({ startsAt }: { startsAt: string | null }) {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (!startsAt) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [startsAt])

  if (!startsAt) return null

  const left = Math.max(0, new Date(startsAt).getTime() - now)
  const seconds = Math.floor(left / 1000)

  const cells: Array<[number, string]> = [
    [Math.floor(seconds / 86_400), 'يوم'],
    [Math.floor(seconds / 3_600) % 24, 'ساعة'],
    [Math.floor(seconds / 60) % 60, 'دقيقة'],
    [seconds % 60, 'ثانية'],
  ]

  return (
    <div className="countdown" aria-hidden="true">
      {cells.map(([value, unit]) => (
        <div key={unit}>
          <b>{value}</b>
          <span>{unit}</span>
        </div>
      ))}
    </div>
  )
}
