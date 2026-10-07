import { useEffect, useMemo, useState } from 'react'
import { PageHead, api, config, sar, untilText, when, type Session } from '@eauction/shared'
import type { AuctionDetail } from './types'
import { useLivePrice } from './useLivePrice'
import { BidBox } from './BidBox'

interface Props {
  auction: AuctionDetail
  session: Session
  onBack: () => void
  onRefresh: () => void
}

/**
 * شاشة المزايدة — the bidder's own screen while an auction runs.
 *
 * Separate from the auction page because the two are read differently. The page is
 * for deciding whether to take part: plots, documents, fees. This is for the minutes
 * when the price moves, and holds only what a bidder looks at then — the price,
 * whether they lead, the clock — with the raise one tap away and nothing to scroll
 * past to reach it.
 */
export function BiddingRoom({ auction, session, onBack, onRefresh }: Props) {
  const { price, verdicts, transport } = useLivePrice(auction.id, session)
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])

  // A ticking clock, so the countdown moves between price updates.
  const [, setNow] = useState(0)
  useEffect(() => {
    const t = window.setInterval(() => setNow((n) => n + 1), 1000)
    return () => window.clearInterval(t)
  }, [])

  const status = price?.status ?? auction.status
  const live = status === 'Live'
  const scheduled = status === 'Scheduled'
  const endsAt = price?.effectiveEndsAt ?? auction.effectiveEndsAt ?? auction.endsAt
  const leftMs = new Date(endsAt).getTime() - Date.now()
  const closing = live && leftMs > 0 && leftMs <= 120_000
  const bidding = price?.priceMinorUnits != null

  return (
    <div className="room">
      <PageHead
        eyebrow="شاشة المزايدة"
        title={auction.nameAr}
        sub={
          live && transport === 'stream'
            ? 'مباشر — يُحدَّث السعر فور تغيّره.'
            : live && transport === 'polling'
              ? 'تحديث دوري — تعذّر البث المباشر، يُحدَّث السعر كل ثانيتين.'
              : undefined
        }
        action={<button onClick={onBack}>تفاصيل المزاد</button>}
      />

      <div className={`room-board${closing ? ' closing' : ''}`}>
        <div className="room-price">
          <span className="room-label">{bidding ? 'السعر الحالي' : 'سعر الافتتاح'}</span>
          <span className="room-figure num">
            {sar(bidding ? price!.priceMinorUnits : auction.openingPriceMinorUnits, 'ar')}
          </span>
          {/* Where this bidder stands, in one line. Rendered as sent for the
              leader's label (D-22): a pseudonym on a masked auction. */}
          {price?.leaderIsYou ? (
            <span className="room-standing lead">أنت الأعلى حالياً</span>
          ) : price?.leaderLabel ? (
            <span className="room-standing behind">المزايد الأعلى: {price.leaderLabel}</span>
          ) : (
            <span className="room-standing">لا مزايدات بعد</span>
          )}
        </div>

        <div className="room-clock">
          <span className="room-label">
            {scheduled ? 'يبدأ بعد' : live ? 'يُغلق بعد' : 'الحالة'}
          </span>
          <span className={`room-figure${live ? ' num' : ''}`}>
            {scheduled
              ? untilText(auction.startsAt)
              : live
                ? untilText(endsAt)
                : 'أُغلق المزاد'}
          </span>
          <span className="room-sub">
            {scheduled
              ? when(auction.startsAt)
              : live && price && price.extensionsUsed > 0
                ? `مُدّد ${price.extensionsUsed} من ${price.maxExtensions}`
                : live && auction.quietPeriodSeconds
                  ? 'المزايدة في الدقيقة الأخيرة تمدّد الإغلاق'
                  : ''}
          </span>
        </div>
      </div>

      <BidBox
        auction={auction}
        session={session}
        price={price}
        verdicts={verdicts}
        participant={participant}
        onBid={onRefresh}
      />
    </div>
  )
}
