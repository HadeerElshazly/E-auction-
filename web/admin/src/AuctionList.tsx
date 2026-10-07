import { useEffect, useMemo, useState } from 'react'
import { CardClock, Icon, PageHead, Stats, api, config, sar, when, type Session } from '@eauction/shared'
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
/**
 * The bidder catalogue's chips, in the same order and with the same grouping
 * (EAuction.Core.StageGroups, applied by the server), plus «قيد الإعداد» — drafts
 * and auctions under review, which the public never sees.
 */
const filters = [
  { key: 'all', ar: 'الكل' },
  { key: 'preparing', ar: 'قيد الإعداد' },
  { key: 'upcoming', ar: 'القادمة' },
  { key: 'live', ar: 'الجارية' },
  { key: 'closed', ar: 'المنتهية' },
] as const

type FilterKey = (typeof filters)[number]['key']

export function AuctionList({ session, auctions, canCreate, busy, onOpen, onCreate }: Props) {
  const live = useLiveFigures()
  const leaders = useLeaders(session)
  const [nameAr, setNameAr] = useState('')
  const [nameEn, setNameEn] = useState('')
  const [creating, setCreating] = useState(false)

  // Filtered and searched on the server, like the catalogue: every client gets the
  // same list and the chips' counts are real. Re-asked whenever the app reloads its
  // own list, so an approval or a new draft shows up here at once.
  const client = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [filter, setFilter] = useState<FilterKey>('all')
  const [page, setPage] = useState<{ items: AuctionListItem[]; counts: Record<FilterKey, number> } | null>(null)

  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(query.trim()), 300)
    return () => window.clearTimeout(t)
  }, [query])

  useEffect(() => {
    const params = new URLSearchParams({ take: '200' })
    if (filter !== 'all') params.set('state', filter)
    if (debounced) params.set('q', debounced)
    client
      .get<{ items: AuctionListItem[]; counts: Record<FilterKey, number> }>(`/auctions?${params}`)
      .then(setPage)
      .catch(() => undefined)
  }, [client, filter, debounced, auctions])

  const visible = page?.items ?? auctions

  return (
    <>
      <PageHead
        eyebrow="مساحة الإدارة"
        title="إدارة المزادات"
        sub="إعداد المزادات ومتابعتها ونتائجها."
        action={
          canCreate &&
          !creating && (
            <button className="primary" onClick={() => setCreating(true)}>
              <Icon name="plus" size={18} /> إضافة مزاد
            </button>
          )
        }
      />

      <Stats
        items={[
          { label: 'جميع المزادات', value: page?.counts.all ?? auctions.length, icon: 'grid' },
          { label: 'قيد الإعداد', value: page?.counts.preparing ?? 0, icon: 'file' },
          { label: 'جارية الآن', value: page?.counts.live ?? 0, icon: 'gavel' },
          { label: 'القادمة', value: page?.counts.upcoming ?? 0, icon: 'clock' },
          { label: 'المنتهية', value: page?.counts.closed ?? 0, icon: 'check' },
        ]}
      />

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

      {auctions.length > 0 && (
        <div className="toolbar">
          <div className="tabs" role="tablist" aria-label="تصفية حسب الحالة">
            {filters.map((f) => (
              <button
                key={f.key}
                role="tab"
                aria-selected={filter === f.key}
                className={`tab${filter === f.key ? ' active' : ''}`}
                onClick={() => setFilter(f.key)}
              >
                {f.ar} <span className="num">{page?.counts[f.key] ?? 0}</span>
              </button>
            ))}
          </div>
          <label className="search">
            <Icon name="search" size={18} />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="ابحث باسم المزاد أو المخطط"
              aria-label="البحث في المزادات"
            />
          </label>
        </div>
      )}

      {auctions.length === 0 ? (
        <div className="card">
          <p className="muted small" style={{ margin: 0 }}>لا توجد مزادات بعد.</p>
        </div>
      ) : visible.length === 0 ? (
        <div className="card">
          <p className="muted" style={{ margin: 0 }}>لا توجد مزادات مطابقة للبحث أو التصفية.</p>
        </div>
      ) : (
        <div className="lot-grid">
          {visible.map((a) => (
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
        <CardClock
          status={auction.status}
          channel={auction.channel}
          startsAt={auction.startsAt}
          endsAt={figures?.effectiveEndsAt ?? auction.endsAt}
        />
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

