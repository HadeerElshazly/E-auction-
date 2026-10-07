import { useEffect, useMemo, useState } from 'react'
import { api, config, sar, stageLabels, untilText } from '@eauction/shared'
import type { AuctionSummary } from './types'

interface Props {
  auctions: AuctionSummary[]
  signedIn: boolean
  onOpen: (id: string) => void
}

/**
 * A Public document's address. Public documents open without a token or a grant,
 * so a plain URL is right here — unlike the booklet, which must be fetched with one.
 */
export function documentUrl(documentId: string): string {
  return `${config.documentsApi.replace(/\/$/, '')}/documents/${documentId}`
}

// One vocabulary for both portals: see shared/src/stages.ts.
export const statusAr = stageLabels

/**
 * The status chips. The grouping itself — which lifecycle stages count as
 * "finished" — is the catalogue service's, as is the search: filtered on the server,
 * so every client gets the same list and the counts are the real ones.
 */
const filters = [
  { key: 'all', ar: 'الكل' },
  { key: 'upcoming', ar: 'القادمة' },
  { key: 'live', ar: 'الجارية' },
  { key: 'closed', ar: 'المنتهية' },
] as const

type FilterKey = (typeof filters)[number]['key']

interface CataloguePage {
  items: AuctionSummary[]
  counts: Record<FilterKey, number>
}

export function Catalogue({ auctions, signedIn, onOpen }: Props) {
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [filter, setFilter] = useState<FilterKey>('all')
  const [page, setPage] = useState<CataloguePage | null>(null)
  const client = useMemo(() => api({ baseUrl: config.queryApi, session: null }), [])

  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(query.trim()), 300)
    return () => window.clearTimeout(t)
  }, [query])

  // Re-asked when the filter or the search changes, and whenever the app's own
  // catalogue poll brings new data, so prices and stages stay current.
  useEffect(() => {
    const params = new URLSearchParams()
    if (filter !== 'all') params.set('state', filter)
    if (debounced) params.set('q', debounced)
    const qs = params.toString()
    client
      .get<CataloguePage>(`/auctions${qs ? `?${qs}` : ''}`)
      .then(setPage)
      .catch(() => undefined)
  }, [client, filter, debounced, auctions])

  const visible = page?.items ?? []
  const countFor = (key: FilterKey) => page?.counts[key] ?? 0

  if (auctions.length === 0) {
    return (
      <div className="card">
        <h2>لا توجد مزادات معروضة حالياً</h2>
        <p className="muted small">
          تُعرض المزادات هنا بعد اعتمادها من لجنة الترسية.
        </p>
      </div>
    )
  }

  return (
    <>
      {!signedIn && (
        <div className="notice info">
          يمكنك تصفّح المزادات دون تسجيل دخول. للمزايدة يلزم الدخول بنفاذ وشراء كراسة
          الشروط وسداد التأمين.
        </div>
      )}

      <div className="catalogue-tools">
        <input
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="ابحث باسم المزاد أو المخطط…"
          aria-label="البحث في المزادات"
        />
        <div className="chips" role="tablist" aria-label="تصفية حسب الحالة">
          {filters.map((f) => (
            <button
              key={f.key}
              role="tab"
              aria-selected={filter === f.key}
              className={filter === f.key ? 'chip on' : 'chip'}
              onClick={() => setFilter(f.key)}
            >
              {f.ar} <span className="num">({countFor(f.key)})</span>
            </button>
          ))}
        </div>
      </div>

      {visible.length === 0 && (
        <div className="card">
          <p className="muted" style={{ margin: 0 }}>
            لا توجد مزادات مطابقة للبحث أو التصفية.
          </p>
        </div>
      )}

      <div className="auction-grid">
        {visible.map((a) => {
          const s = statusAr[a.status] ?? { ar: a.status, tone: 'done' }
          const live = a.status === 'Live'
          return (
            <div className="auction-card static" key={a.id}>
              <div className="cover">
                {a.coverImageDocumentId && (
                  <img
                    src={documentUrl(a.coverImageDocumentId)}
                    alt=""
                    loading="lazy"
                    // A cover that fails to load leaves the gradient behind it,
                    // rather than a broken-image icon on a citizen's screen.
                    onError={(e) => {
                      e.currentTarget.style.display = 'none'
                    }}
                  />
                )}
                <span className="channel">
                  {a.plotCount} قطعة · {a.totalAreaSqm} م²
                </span>

                {/* Where the bidding happens, on the card rather than three
                    screens in. A hall auction takes the same booklet and the same
                    deposit as an online one and then needs the bidder in the room,
                    so it is not a detail to find out after paying. */}
                <span className="channel at-end">
                  {a.channel === 'Onsite' ? '📍 حضوري' : '🌐 إلكتروني'}
                </span>

                {/* The clock, over the cover, as the proposal has it — but only
                    where there is actually a clock to show.
                    
                    A hall auction has none: the auctioneer brings the hammer down,
                    not a timer (§29), so its endsAt is already in the past while it
                    is legitimately running. Rendering the countdown regardless put
                    "انتهى" on a card that said جارٍ الآن beside it, which is not a
                    cosmetic mismatch — it tells a citizen an open auction is over. */}
                {live && new Date(a.endsAt).getTime() > Date.now() && (
                  <div className="countdown wide" aria-hidden="true">
                    <div>
                      <b>{untilText(a.endsAt)}</b>
                      <span>حتى الإغلاق</span>
                    </div>
                  </div>
                )}
              </div>

              <div className="body">
                <p className="title">{a.nameAr}</p>

                <div>
                  <span className={`pill ${s.tone}`}>{s.ar}</span>
                </div>

                <div>
                  <div className="muted small">
                    {live ? 'السعر الحالي' : 'سعر الافتتاح'}
                  </div>
                  <div className="price num">
                    {sar(
                      live
                        ? a.priceMinorUnits ?? a.openingPriceMinorUnits
                        : a.openingPriceMinorUnits,
                      'ar',
                    )}
                  </div>
                </div>

                <div className="facts">
                  <div>
                    <span>التأمين</span>
                    <span className="num">{sar(a.depositMinorUnits, 'ar')}</span>
                  </div>
                  <div>
                    <span>الكراسة</span>
                    <span className="num">{sar(a.bookletPriceMinorUnits, 'ar')}</span>
                  </div>
                </div>

                <button
                  className="primary"
                  style={{ marginTop: 'auto' }}
                  onClick={() => onOpen(a.id)}
                >
                  التفاصيل
                </button>
              </div>
            </div>
          )
        })}
      </div>
    </>
  )
}
