import { useEffect, useMemo, useState } from 'react'
import { CardClock, Icon, PageHead, Pager, Stats, api, config, sar, usePage, when, type Session } from '@eauction/shared'
import type { AuctionListItem } from './types'
import { amendmentLabel, isDraft, label } from './types'
import { BidderName, useLeaders } from './winners'
import { NewAuction, type NewAuctionBody } from './NewAuction'

interface Props {
  session: Session
  auctions: AuctionListItem[]
  canCreate: boolean
  busy: boolean
  onOpen: (id: string) => void
  /** The bid step in halalas, when given up front; else set later in the details. */
  onCreate: (body: NewAuctionBody) => void
  /** «حذف المسودة» from the row, for a draft the administrator will not keep. */
  onDelete: (id: string) => Promise<void>
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

export function AuctionList({ session, auctions, canCreate, busy, onOpen, onCreate, onDelete }: Props) {
  const live = useLiveFigures()
  const leaders = useLeaders(session)
  const [creating, setCreating] = useState(false)

  // Filtered and searched on the server, like the catalogue: every client gets the
  // same list and the chips' counts are real. Re-asked whenever the app reloads its
  // own list, so an approval or a new draft shows up here at once.
  const client = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [filter, setFilter] = useState<FilterKey>('all')
  const [page, setPage] = useState<{ items: AuctionListItem[]; total: number; counts: Record<FilterKey, number> } | null>(null)
  const paging = usePage()
  useEffect(() => paging.setPage(0), [filter, debounced])

  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(query.trim()), 300)
    return () => window.clearTimeout(t)
  }, [query])

  useEffect(() => {
    const params = new URLSearchParams()
    if (filter !== 'all') params.set('state', filter)
    if (debounced) params.set('q', debounced)
    client
      .get<{ items: AuctionListItem[]; total: number; counts: Record<FilterKey, number> }>(
        `/auctions?${params}&${paging.query}`,
      )
      .then(setPage)
      .catch(() => undefined)
  }, [client, filter, debounced, auctions, paging.query])

  const visible = page?.items ?? auctions

  // A table by default, as the operators' screen; the cards, with their covers, on request.
  const [layout, setLayout] = useState<'table' | 'cards'>(() => {
    try {
      return localStorage.getItem('admin.list') === 'cards' ? 'cards' : 'table'
    } catch {
      return 'table'
    }
  })
  const chooseLayout = (l: 'table' | 'cards') => {
    setLayout(l)
    try {
      localStorage.setItem('admin.list', l)
    } catch {
      // Not remembered; still switches.
    }
  }

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
        <NewAuction
          busy={busy}
          onClose={() => setCreating(false)}
          onCreate={(body) => {
            onCreate(body)
            setCreating(false)
          }}
        />
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
          <div className="tabs" role="group" aria-label="طريقة العرض">
            <button className={`tab${layout === 'table' ? ' active' : ''}`} onClick={() => chooseLayout('table')}>
              <Icon name="list" size={16} /> جدول
            </button>
            <button className={`tab${layout === 'cards' ? ' active' : ''}`} onClick={() => chooseLayout('cards')}>
              <Icon name="grid" size={16} /> بطاقات
            </button>
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
        layout === 'table' ? (
          <AuctionTable
            session={session}
            auctions={visible}
            live={live}
            leaders={leaders}
            busy={busy}
            canDelete={canCreate}
            onOpen={onOpen}
            onDelete={onDelete}
          />
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
        )
      )}
      <Pager page={paging.page} total={page?.total ?? 0} noun="مزاد" onPage={paging.setPage} />
    </>
  )
}

/**
 * An open auction's moving figures — price and actual close — read from the same
 * public source the bidders' screens and the live monitor read, so staff never see
 * the planned end time while bidders see an extended one.
 */
/**
 * إدارة المزادات as a table — the prototype's operators' view: one row per auction,
 * the figure that matters for its stage, and the way into it.
 */
function AuctionTable({
  session,
  auctions,
  live,
  leaders,
  busy,
  canDelete,
  onOpen,
  onDelete,
}: {
  session: Session
  auctions: AuctionListItem[]
  live: Record<string, LiveFigures>
  leaders: Record<string, string>
  busy: boolean
  canDelete: boolean
  onOpen: (id: string) => void
  onDelete: (id: string) => Promise<void>
}) {
  return (
    <div className="card table-card">
      <div className="table-scroll">
        <table data-testid="auction-table">
          <thead>
            <tr>
              <th>رقم</th>
              <th>المزاد</th>
              <th>القطع والمساحة</th>
              <th>أعلى عرض</th>
              <th>الحالة</th>
              <th>البدء</th>
              <th>إدارة</th>
            </tr>
          </thead>
          <tbody>
            {auctions.map((a) => {
              const l = label(a.status)
              const amending = amendmentLabel(a.amendment)
              const figures = live[a.id]
              const bidding = a.status === 'Live' && figures?.priceMinorUnits != null
              const winnerId = a.status === 'Live' ? leaders[a.id] : a.winnerBidderId ?? leaders[a.id]
              return (
                <tr
                  key={a.id}
                  data-testid="auction-card"
                  className="row-link"
                  onClick={(e) => {
                    // The whole row opens the auction; its own buttons and links do their own thing.
                    if (!(e.target as HTMLElement).closest('button, a')) onOpen(a.id)
                  }}
                >
                  <td className="num">{a.number}</td>
                  <td>
                    <button className="link strong" onClick={() => onOpen(a.id)}>
                      {a.nameAr}
                    </button>
                  </td>
                  <td>
                    {a.channel === 'Onsite' ? 'حضوري' : 'إلكتروني'}
                    <small><span className="num">{a.totalAreaSqm}</span> م²</small>
                  </td>
                  <td>
                    <span className="num strong">
                      {sar(bidding ? figures!.priceMinorUnits : a.openingPriceMinorUnits, 'ar')}
                    </span>
                    <small>
                      {bidding ? 'السعر الحالي' : 'سعر البداية'}
                      {winnerId && a.status !== 'Cancelled' && (
                        <>
                          {' · '}
                          <BidderName session={session} id={winnerId} />
                        </>
                      )}
                    </small>
                  </td>
                  <td>
                    <span className={`pill ${l.tone}`}>{l.ar}</span>
                    {amending && <small><span className="pill wait small">{amending}</span></small>}
                  </td>
                  <td className="small">{a.startsAt ? when(a.startsAt) : <span className="muted">لم يُجدول</span>}</td>
                  <td>
                    <div className="actions">
                      <button className="small" onClick={() => onOpen(a.id)}>
                        {['Draft', 'Rejected'].includes(a.status) ? 'تعديل' : 'فتح'}
                      </button>
                      {a.status === 'Live' && (
                        <a className="button small" href="#monitor">
                          المتابعة
                        </a>
                      )}
                      {canDelete && isDraft(a) && (
                        <button
                          className="danger small"
                          disabled={busy}
                          onClick={() => {
                            if (!window.confirm(`حذف المسودة «${a.nameAr}» نهائياً؟ لا يمكن التراجع.`)) return
                            void onDelete(a.id)
                          }}
                        >
                          حذف
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

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
          <span className="num">{auction.totalAreaSqm}</span> م²
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
        <div className="row" style={{ gap: 6 }}>
          <span className={`pill ${l.tone}`}>{l.ar}</span>
          <span className="muted small num">#{auction.number}</span>
        </div>

        <div>
          <div className="muted small">{bidding ? 'السعر الحالي' : 'سعر البداية'}</div>
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
        </div>
      </div>
    </div>
  )
}

