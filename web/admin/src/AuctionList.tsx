import { useEffect, useState } from 'react'
import { api, config, sar, untilText, when, type Session } from '@eauction/shared'
import type { AuctionListItem } from './types'
import { label } from './types'
import { BidderName, useLeaders } from './winners'

interface Props {
  session: Session
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
export function AuctionList({ session, auctions, canCreate, busy, onOpen, onCreate }: Props) {
  const live = useLiveFigures()
  const leaders = useLeaders(session)
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
            <AuctionCard
              key={a.id}
              session={session}
              auction={a}
              live={live[a.id]}
              leaderId={leaders[a.id]}
              onOpen={onOpen}
            />
          ))}
        </div>
      )}
    </>
  )
}

/**
 * An open auction's moving figures — price and actual close — read from the same
 * public source the bidders' screens and the live monitor read, so staff never see
 * the planned end time while bidders see an extended one.
 */
interface LiveFigures {
  priceMinorUnits: number | null
  effectiveEndsAt: string
}

function useLiveFigures(): Record<string, LiveFigures> {
  const [rows, setRows] = useState<Record<string, LiveFigures>>({})
  useEffect(() => {
    const client = api({ baseUrl: config.queryApi, session: null })
    let stop = false
    const tick = async () => {
      try {
        const page = await client.get<{ items: Array<LiveFigures & { auctionId: string }> }>('/auctions/live')
        if (!stop) setRows(Object.fromEntries(page.items.map((r) => [r.auctionId, r])))
      } catch {
        // The card falls back to the planned figures; the next tick tries again.
      }
    }
    void tick()
    const t = window.setInterval(() => void tick(), 5000)
    return () => {
      stop = true
      window.clearInterval(t)
    }
  }, [])
  return rows
}

function AuctionCard({
  session,
  auction,
  live: figures,
  leaderId,
  onOpen,
}: {
  session: Session
  auction: AuctionListItem
  live?: LiveFigures
  leaderId?: string
  onOpen: (id: string) => void
}) {
  const l = label(auction.status)
  const live = auction.status === 'Live'
  // Staff see who it is, not the public pseudonym: the leader while it runs, the
  // awarded bidder once the committee has decided, the top bidder in between.
  const winnerId = live ? leaderId : (auction.winnerBidderId ?? leaderId)
  const winnerLabel = live
    ? 'المزايد الأعلى'
    : auction.winnerBidderId && ['Awarded', 'Settled'].includes(auction.status)
      ? 'الفائز'
      : 'الأعلى عند الإغلاق'
  const bidding = live && figures?.priceMinorUnits != null
  const onsite = auction.channel === 'Onsite'

  return (
    // The citizen's card, with the staff's additions: the status workflow, drafts,
    // the start date and the English name. The same cover, words and figures, so
    // what staff look at is what the public sees.
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
        {auction.coverImageDocumentId && (
          <img
            src={`${config.documentsApi.replace(/\/$/, '')}/documents/${auction.coverImageDocumentId}`}
            alt=""
            loading="lazy"
            onError={(e) => {
              e.currentTarget.style.display = 'none'
            }}
          />
        )}
        <span className="channel">
          {auction.plotCount} قطعة · <span className="num">{auction.totalAreaSqm}</span> م²
        </span>
        <span className="channel at-end">{onsite ? '📍 حضوري' : '🌐 إلكتروني'}</span>
        <Clock auction={auction} endsAt={figures?.effectiveEndsAt ?? auction.endsAt} />
      </div>

      <div className="body">
        <p className="title">{auction.nameAr}</p>
        <div>
          <span className={`pill ${l.tone}`}>{l.ar}</span>
        </div>

        <div>
          <div className="muted small">{bidding ? 'السعر الحالي' : 'سعر الافتتاح'}</div>
          <div className="price num">
            {sar(bidding ? figures!.priceMinorUnits : auction.openingPriceMinorUnits, 'ar')}
          </div>
        </div>

        <div className="facts">
          <div>
            <span>التأمين</span>
            <span className="num">{sar(auction.depositMinorUnits, 'ar')}</span>
          </div>
          <div>
            <span>الكراسة</span>
            <span className="num">
              {auction.bookletPriceMinorUnits === 0 ? 'مجاناً' : sar(auction.bookletPriceMinorUnits, 'ar')}
            </span>
          </div>
          <div>
            <span>{live ? 'بدأ' : 'يبدأ'}</span>
            <span>{auction.startsAt ? when(auction.startsAt) : 'لم يُجدول بعد'}</span>
          </div>
          {winnerId && auction.status !== 'Cancelled' && (
            <div className="winner-row" data-testid="card-winner">
              <span>{winnerLabel}</span>
              <BidderName session={session} id={winnerId} />
            </div>
          )}
          <div className="ltr small">{auction.nameEn}</div>
        </div>
      </div>
    </div>
  )
}

/**
 * The strip over the cover: the one time that matters for the auction's state, as
 * on the citizen's card — to the start while it is upcoming, to the close while it
 * runs. Nothing once it is over: a countdown on a cancelled or awarded auction read
 * as if it were still to happen.
 *
 * Ticks on its own rather than from a prop, because the list around it refreshes on
 * a five-second poll and a countdown that only moved with the list would stutter.
 */
function Clock({ auction, endsAt }: { auction: AuctionListItem; endsAt: string | null }) {
  const [, setNow] = useState(() => Date.now())

  const upcoming = ['Approved', 'Scheduled'].includes(auction.status)
  const live = auction.status === 'Live'
  const ticking = (upcoming && auction.startsAt) || (live && endsAt)

  useEffect(() => {
    if (!ticking) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [ticking])

  if (live && auction.channel === 'Onsite') {
    // A hall auction is closed by the hammer, not a clock (§29).
    return (
      <div className="countdown wide" aria-hidden="true">
        <div>
          <b>جارٍ في القاعة</b>
          <span>يُغلق بقرار مدير المزاد</span>
        </div>
      </div>
    )
  }

  if (!ticking) return null
  const target = upcoming ? auction.startsAt! : endsAt!
  if (new Date(target).getTime() <= Date.now()) return null

  return (
    <div className="countdown wide" aria-hidden="true">
      <div>
        <b>{untilText(target)}</b>
        <span>{upcoming ? 'حتى البدء' : 'حتى الإغلاق'}</span>
      </div>
    </div>
  )
}
