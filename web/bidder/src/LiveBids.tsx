import { useEffect, useMemo, useState } from 'react'
import { PageHead, api, config, riyals, sar, when, type Api, type Session } from '@eauction/shared'
import type { AuctionDetail, AuctionSummary, LivePrice, Subscription } from './types'
import { useBidSender } from './useBidSender'
import { recordSubmitted } from './BidBox'

interface Props {
  session: Session
  /** The catalogue already loaded, for which auctions are running. */
  auctions: AuctionSummary[]
  onOpenRoom: (auctionId: string) => void
  onBack: () => void
}

/**
 * مزاداتي الجارية — every running auction this bidder qualified for, side by side,
 * each with its price, clock, who leads, and the raise on the card itself.
 *
 * For the bidder in more than one auction at once: switching between auction pages
 * mid-war costs the seconds the war is decided in. Only what matters while it runs
 * is on a card — no opening price, no fees — the full screen is a tap away.
 */
export function LiveBids({ session, auctions, onOpenRoom, onBack }: Props) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  // One request, unpaged: a bidder qualifies for a handful of auctions, and this is
  // the screen bids are made from — nothing extra between them and the price.
  const [eligible, setEligible] = useState<Set<string> | null>(null)

  useEffect(() => {
    participant
      .get<{ items: Array<{ subscription: Subscription }> }>(
        `/bidders/${session.subject}/subscriptions?eligibility=Accepted`,
      )
      .then((r) => setEligible(new Set(r.items.map((x) => x.subscription.auctionId))))
      .catch(() => setEligible(new Set()))
  }, [participant, session.subject])

  // Online only: a hall auction is bid in the room, not from here.
  const mine = auctions.filter((a) => eligible?.has(a.id) && a.channel !== 'Onsite')
  const running = mine.filter((a) => a.status === 'Live')
  const upcoming = mine
    .filter((a) => a.status === 'Scheduled')
    .sort((x, y) => (x.startsAt ?? '').localeCompare(y.startsAt ?? ''))

  return (
    <>
      <PageHead
        eyebrow="مساحة المزايد"
        title="مزاداتي الجارية"
        sub={
          eligible === null
            ? '…'
            : running.length === 0
              ? 'لا يوجد مزاد جارٍ الآن من المزادات التي تأهّلت لها.'
              : `${running.length} مزاد جارٍ — زايد من البطاقة مباشرة.`
        }
        action={<button onClick={onBack}>جميع المزادات</button>}
      />

      {running.length > 0 && (
        <div className="live-grid">
          {running.map((a) => (
            <LiveCard
              key={a.id}
              summary={a}
              session={session}
              participant={participant}
              onOpenRoom={() => onOpenRoom(a.id)}
            />
          ))}
        </div>
      )}

      {eligible !== null && running.length === 0 && upcoming.length > 0 && (
        <div className="card">
          <h2>القادمة من مزاداتك</h2>
          <ul className="doc-list">
            {upcoming.map((a) => (
              <li key={a.id}>
                <span>{a.nameAr}</span>
                <span className="muted small">يبدأ {when(a.startsAt)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </>
  )
}

/** The price, as this bidder sees it: their own "you lead" included. */
function usePrice(auctionId: string, session: Session): LivePrice | null {
  const [price, setPrice] = useState<LivePrice | null>(null)
  useEffect(() => {
    const client = api({ baseUrl: config.queryApi, session })
    let stop = false
    const read = () =>
      client
        .get<LivePrice>(`/auctions/${auctionId}/price`)
        .then((p) => !stop && setPrice(p))
        .catch(() => undefined)
    void read()
    // A poll, not a stream per card: six streams from one page is where a browser's
    // per-origin connection limit starts refusing, and the seventh card would freeze.
    const t = window.setInterval(() => void read(), 2000)
    return () => {
      stop = true
      window.clearInterval(t)
    }
  }, [auctionId, session])
  return price
}

function LiveCard({
  summary,
  session,
  participant,
  onOpenRoom,
}: {
  summary: AuctionSummary
  session: Session
  participant: Api
  onOpenRoom: () => void
}) {
  const price = usePrice(summary.id, session)
  const sender = useBidSender(summary.id, session, participant)
  const [increment, setIncrement] = useState<number | null>(null)
  const [, setTick] = useState(0)

  useEffect(() => {
    api({ baseUrl: config.queryApi, session: null })
      .get<AuctionDetail>(`/auctions/${summary.id}`)
      .then((d) => setIncrement(d.minIncrementMinorUnits))
      .catch(() => undefined)
  }, [summary.id])

  useEffect(() => {
    const t = window.setInterval(() => setTick((n) => n + 1), 1000)
    return () => window.clearInterval(t)
  }, [])

  const live = (price?.status ?? summary.status) === 'Live'
  const current = price?.priceMinorUnits ?? null
  // Signed in, so «إعدادات العرض للزوار» hid nothing: the figures are always sent.
  const minimum = (price?.minimumNextBidMinorUnits ?? summary.minimumNextBidMinorUnits)!
  const endsAt = (price?.effectiveEndsAt ?? summary.endsAt)!
  const seconds = Math.max(0, Math.floor((new Date(endsAt).getTime() - Date.now()) / 1000))
  const closing = live && seconds <= 120
  const leading = price?.leaderIsYou === true

  // One button: the price plus «زيادة المزايدة», or the opening price for the first bid.
  const bid = async (amount: number) => {
    const sent = await sender.send(amount)
    if (sent) recordSubmitted(summary.id, session.subject, sent)
  }

  const canBid = live && !leading && !sender.busy

  return (
    <div
      className={`live-card${leading ? ' lead' : price?.leaderLabel ? ' behind' : ''}${closing ? ' closing' : ''}`}
      data-testid="live-card"
    >
      <div className="live-card-head">
        <p className="live-card-name">{summary.nameAr}</p>
        <span className={`live-card-clock num${closing ? ' urgent' : ''}`}>
          {live ? clockText(seconds) : 'أُغلق'}
        </span>
      </div>

      <div className="live-card-price">
        {current === null ? (
          <span className="muted">لا مزايدات بعد</span>
        ) : (
          <>
            <span className="room-label">السعر الحالي</span>
            <span className="live-card-figure num">{sar(current, 'ar')}</span>
          </>
        )}
      </div>

      <div className={`live-card-standing${leading ? ' lead' : ''}`}>
        {leading
          ? 'أنت الأعلى حالياً'
          : price?.leaderLabel
            ? `الأعلى: ${price.leaderLabel}`
            : 'كن أول مزايد'}
        {price && price.extensionsUsed > 0 && (
          <span className="muted small"> · مُدّد {price.extensionsUsed} من {price.maxExtensions}</span>
        )}
      </div>

      {sender.problem && <div className="notice error small">{sender.problem}</div>}

      <div className="next-bid compact">
        <small>مزايدتك التالية</small>
        <b className="num">{sar(minimum, 'ar')}</b>
        {increment !== null && current !== null && (
          <small className="muted">+ زيادة المزايدة {sar(increment, 'ar')}</small>
        )}
      </div>
      <button
        className="primary wide"
        disabled={!canBid}
        aria-label={`مزايدة بـ ${riyals(minimum)} على ${summary.nameAr}`}
        onClick={() => void bid(minimum)}
      >
        {sender.busy ? 'جارٍ الإرسال…' : 'مزايدة'}
      </button>

      <button className="ghost live-card-more" onClick={onOpenRoom}>
        شاشة المزايدة الكاملة ←
      </button>
    </div>
  )
}

/** 52:49, or 1:05:09 past the hour, or «2 يوم» further out. */
function clockText(seconds: number): string {
  if (seconds >= 86_400) return `${Math.floor(seconds / 86_400)} يوم`
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  const pad = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`
}
