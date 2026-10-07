import { useEffect, useState } from 'react'
import { untilText } from './money'

/** Re-renders every second while <paramref name="active"/>, so a countdown moves. */
export function useTick(active = true): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active) return
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [active])
  return now
}

const upcomingStages = ['Approved', 'Scheduled']

/**
 * The strip over a card's cover: to the start while the auction is upcoming, to the
 * close while it runs. One component for both portals, so a citizen's card and the
 * clerk's card count the same thing — the catalogue had only the closing clock and
 * the administrators' list had both, and the two cards disagreed for every
 * upcoming auction.
 *
 * A hall auction has no closing clock: the auctioneer brings the hammer down, not a
 * timer (§29), so it says so instead of counting to an end time that does not bind.
 */
export function CardClock({
  status,
  channel,
  startsAt,
  endsAt,
}: {
  status: string
  channel: string
  startsAt: string | null
  endsAt: string | null
}) {
  const upcoming = upcomingStages.includes(status)
  const live = status === 'Live'
  const onsite = channel === 'Onsite'
  const target = upcoming ? startsAt : live && !onsite ? endsAt : null
  const now = useTick(!!target)

  if (live && onsite) {
    return (
      <div className="countdown wide" aria-hidden="true">
        <div>
          <b>جارٍ في القاعة</b>
          <span>يُغلق بقرار مدير المزاد</span>
        </div>
      </div>
    )
  }
  if (!target || new Date(target).getTime() <= now) return null

  return (
    <div className="countdown wide" aria-hidden="true">
      <div>
        <b>{untilText(target, new Date(now))}</b>
        <span>{upcoming ? 'حتى البدء' : 'حتى الإغلاق'}</span>
      </div>
    </div>
  )
}

/**
 * The large countdown on an auction's own page: days, hours, minutes, seconds, each
 * in its own box, ticking — to the start before it opens, to the close while it runs.
 */
export function CountdownPanel({ target, label }: { target: string; label: string }) {
  const now = useTick(true)
  const ms = Math.max(0, new Date(target).getTime() - now)
  const total = Math.floor(ms / 1000)
  const parts: Array<[number, string]> = [
    [Math.floor(total / 86400), 'يوم'],
    [Math.floor((total % 86400) / 3600), 'ساعة'],
    [Math.floor((total % 3600) / 60), 'دقيقة'],
    [total % 60, 'ثانية'],
  ]
  return (
    <div className="countdown-panel" role="timer" aria-label={`${label}: ${untilText(target, new Date(now))}`}>
      <div className="countdown-panel-label">{label}</div>
      <div className="countdown-panel-units">
        {parts.map(([value, unit]) => (
          <div key={unit} className="countdown-panel-unit">
            <b className="num">{String(value).padStart(2, '0')}</b>
            <span>{unit}</span>
          </div>
        ))}
      </div>
    </div>
  )
}
