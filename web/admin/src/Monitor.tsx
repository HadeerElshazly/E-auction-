import { useCallback, useEffect, useState } from 'react'
import { Pager, api, config, sar, usePage, type Session } from '@eauction/shared'
import { BidderName, useLeaders } from './winners'

interface Row {
  auctionId: string
  nameAr: string
  channel: string
  status: string
  priceMinorUnits: number | null
  minimumNextBidMinorUnits: number
  openingPriceMinorUnits: number
  leaderLabel: string | null
  startsAt: string
  effectiveEndsAt: string
  extensionsUsed: number
  maxExtensions: number
}

/**
 * المتابعة المباشرة — every open auction at once.
 *
 * The screen a municipality running six auctions on a Tuesday morning did not have.
 * Until now the only live view in the platform was a bidder's own auction page: the
 * committee could see that an auction had opened and that one had closed, and
 * nothing in between — no price, no leader, no sense of whether a lot was moving.
 *
 * Polled rather than streamed, and on one request rather than one per auction. The
 * push channel (§7.2) is built for the opposite shape — one auction fanned out to
 * thousands of bidders — and six of those streams from a single page is where a
 * browser's per-origin connection limit begins refusing, which would show up as the
 * seventh auction silently never updating. A two-second poll of one endpoint is a
 * fraction of the traffic and has no such cliff.
 */
export function Monitor({ session, onOpen }: { session: Session; onOpen: (id: string) => void }) {
  const leaders = useLeaders(session)
  const [rows, setRows] = useState<Row[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Ticks every second so the countdowns move between polls, which is what makes
  // the screen feel live rather than like a page that refreshes.
  const [, setNow] = useState(() => Date.now())

  const paging = usePage()
  const [total, setTotal] = useState(0)
  const load = useCallback(async () => {
    const client = api({ baseUrl: config.queryApi, session })
    try {
      const page = await client.get<{ items: Row[]; total: number }>(`/auctions/live?${paging.query}`)
      setRows(page.items)
      setTotal(page.total)
      setError(null)
    } catch {
      // Said in Arabic, and the last good rows kept on screen: a watcher in the
      // middle of an auction is better served by a slightly stale board than by an
      // empty one with an English HTTP message on it.
      setError('تعذّر الاتصال بخدمة المزادات — تُعرض آخر قراءة، وتُعاد المحاولة تلقائياً.')
    }
  }, [session, paging.query])

  useEffect(() => {
    let cancelled = false

    const tick = async () => {
      if (cancelled) return
      if (!document.hidden) await load()
    }

    void tick()
    const poll = window.setInterval(() => void tick(), 2000)
    const clock = window.setInterval(() => setNow(Date.now()), 1000)

    return () => {
      cancelled = true
      window.clearInterval(poll)
      window.clearInterval(clock)
    }
  }, [load])

  return (
    <>
      <div className="page-head">
        <div className="grow">
          <div className="eyebrow">مساحة الإدارة</div>
          <h1>المتابعة المباشرة</h1>
          <p>
            {rows === null
              ? 'جارٍ القراءة…'
              : rows.length === 0
                ? 'لا يوجد مزاد مفتوح الآن.'
                : `${rows.length} مزاد مفتوح — تُحدَّث كل ثانيتين.`}
          </p>
        </div>
      </div>

      {error && <div className="notice error">{error}</div>}

      {rows !== null && rows.length === 0 && (
        <div className="card">
          <p className="muted small" style={{ margin: 0 }}>
            تظهر المزادات هنا لحظة افتتاحها، وتختفي عند إغلاقها.
          </p>
        </div>
      )}

      <div className="monitor-grid" data-testid="monitor">
        {(rows ?? []).map((r) => (
          <AuctionTile key={r.auctionId} row={r} session={session} leaderId={leaders[r.auctionId]} onOpen={() => onOpen(r.auctionId)} />
        ))}
      </div>
      <Pager page={paging.page} total={total} noun="مزاد جارٍ" onPage={paging.setPage} />
    </>
  )
}

function AuctionTile({ row, session, leaderId, onOpen }: { row: Row; session: Session; leaderId?: string; onOpen: () => void }) {
  const left = Math.max(0, new Date(row.effectiveEndsAt).getTime() - Date.now())
  const seconds = Math.floor(left / 1000)

  // A hall auction has no clock: the auctioneer brings the hammer down (§29), so
  // its end time is already behind while it is legitimately running. Counted down,
  // the tile said انتهى in red over an auction still taking bids in the room — the
  // same mistake the catalogue card and the auction page were corrected for.
  const onsite = row.channel === 'Onsite'

  // Under two minutes is when an auction is actually decided, and when somebody
  // watching needs to be looking at this one rather than the other five.
  const closing = !onsite && seconds <= 120

  // No bid yet is not a price of zero: the auction is open at its opening price and
  // nobody has moved. Saying "—" would hide the number a watcher wants.
  const bidding = row.priceMinorUnits !== null

  return (
    <div
      className={`monitor-card clickable${closing ? ' closing' : ''}`}
      data-testid="monitor-card"
      role="link"
      tabIndex={0}
      aria-label={`فتح ${row.nameAr}`}
      onClick={onOpen}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onOpen()
        }
      }}
    >
      <div className="row" style={{ marginBottom: 10 }}>
        <span className="pill teal plain">
          {row.channel === 'Onsite' ? '📍 في الموقع' : '🌐 عبر الإنترنت'}
        </span>
        <span className="grow" />
        {onsite ? (
          <span className="clock word">جارٍ في القاعة</span>
        ) : (
          <Countdown seconds={seconds} urgent={closing} />
        )}
      </div>

      <p className="name">{row.nameAr}</p>

      <div className="price num">
        {sar(row.priceMinorUnits ?? row.openingPriceMinorUnits, 'ar')}
      </div>
      <div className="muted small" style={{ marginBottom: 12 }}>
        {bidding ? 'السعر الحالي' : 'سعر البداية — لا مزايدات بعد'}
      </div>

      <div className="facts">
        <div>
          <span className="muted">المزايد الأعلى</span>
          {/* The real bidder for staff; the public pseudonym only until it resolves. */}
          <span>{leaderId ? <BidderName session={session} id={leaderId} /> : (row.leaderLabel ?? '—')}</span>
        </div>
        {row.extensionsUsed > 0 && (
          <div>
            <span className="muted">التمديد</span>
            <span className="num">
              {row.extensionsUsed} / {row.maxExtensions}
            </span>
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * The clock, with the days kept out of it.
 *
 * Two elements rather than one string, because the clock has to be isolated
 * left-to-right — digits in an Arabic page otherwise reorder their groups — and an
 * Arabic word inside that isolated run gets dragged along with it. "1 يوم" plus
 * "23:33:30" rendered as a single isolated string came out on screen as
 * "123:33:30", which reads as a hundred and twenty-three hours.
 */
function Countdown({ seconds, urgent }: { seconds: number; urgent: boolean }) {
  // `word`, not the monospaced clock: a monospace face has no Arabic shaping, so
  // انتهى came out with its letters disconnected — "ا نتهى".
  if (seconds <= 0) return <span className="clock word">انتهى</span>

  const days = Math.floor(seconds / 86_400)
  const h = Math.floor(seconds / 3_600) % 24
  const m = Math.floor(seconds / 60) % 60
  const s = seconds % 60
  const pad = (n: number) => String(n).padStart(2, '0')

  const clock = h > 0 || days > 0
    ? `${pad(h)}:${pad(m)}:${pad(s)}`
    : `${pad(m)}:${pad(s)}`

  return (
    <>
      {days > 0 && <span className="days muted small">{days} يوم</span>}
      <span className={`clock num${urgent ? ' urgent' : ''}`}>{clock}</span>
    </>
  )
}
