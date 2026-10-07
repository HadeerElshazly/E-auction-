import { useEffect, useMemo, useState } from 'react'
import { CardClock, Icon, PageHead, Stats, api, config, sar, stageLabels } from '@eauction/shared'
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
 * The status tabs. The grouping itself — which lifecycle stages count as
 * "finished" — is the catalogue service's, as is the search: filtered on the server,
 * so every client gets the same list and the counts are the real ones.
 */
const filters = [
  { key: 'all', ar: 'جميع المزادات' },
  { key: 'live', ar: 'جارية الآن' },
  { key: 'upcoming', ar: 'القادمة' },
  { key: 'closed', ar: 'المنتهية' },
] as const

type FilterKey = (typeof filters)[number]['key']

interface CataloguePage {
  items: AuctionSummary[]
  counts: Record<FilterKey, number>
}

/** المزادات — the public catalogue: what is on offer, what is running, what is next. */
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

  return (
    <>
      <PageHead
        eyebrow="مزادات الأراضي"
        title="فرصتك تبدأ بقطعة أرض"
        sub="استعرض القطع المطروحة، وتابع المزادات وشارك بثقة."
        action={
          signedIn && (
            <a className="button" href="#applications">
              <Icon name="file" size={18} /> مشاركاتي
            </a>
          )
        }
      />

      {!signedIn && (
        <div className="notice info">
          يمكنك تصفّح المزادات دون تسجيل دخول. للمزايدة يلزم الدخول بنفاذ وشراء كراسة الشروط
          وسداد التأمين.
        </div>
      )}

      <Stats
        items={[
          { label: 'المزادات المطروحة', value: countFor('all'), icon: 'grid' },
          { label: 'جارية الآن', value: countFor('live'), icon: 'gavel' },
          { label: 'تفتح قريباً', value: countFor('upcoming'), icon: 'clock' },
          { label: 'منتهية', value: countFor('closed'), icon: 'check' },
        ]}
      />

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
              {f.ar} <span className="num">{countFor(f.key)}</span>
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

      {auctions.length === 0 ? (
        <div className="empty-state card">
          <h3>لا توجد مزادات معروضة حالياً</h3>
          <p className="muted">تُعرض المزادات هنا بعد اعتمادها من لجنة الترسية.</p>
        </div>
      ) : visible.length === 0 ? (
        <div className="empty-state card">
          <h3>لا توجد مزادات مطابقة</h3>
          <p className="muted">جرّب اسماً آخر أو غيّر حالة المزاد.</p>
        </div>
      ) : (
        <div className="lot-grid" data-testid="catalogue">
          {visible.map((a) => (
            <LotCard key={a.id} auction={a} onOpen={onOpen} />
          ))}
        </div>
      )}

      <div className="section-foot">عرض {visible.length} مزاد</div>
    </>
  )
}

function LotCard({ auction: a, onOpen }: { auction: AuctionSummary; onOpen: (id: string) => void }) {
  const s = statusAr[a.status] ?? { ar: a.status, tone: 'done' }
  const live = a.status === 'Live'
  const bidding = live && a.priceMinorUnits != null
  return (
    <article className="lot-card auction-card static">
      <button className="lot-visual cover" onClick={() => onOpen(a.id)} aria-label={`تفاصيل ${a.nameAr}`}>
        {a.coverImageDocumentId && (
          <img
            src={documentUrl(a.coverImageDocumentId)}
            alt=""
            loading="lazy"
            // A cover that fails to load leaves the gradient behind it, rather than
            // a broken-image icon on a citizen's screen.
            onError={(e) => {
              e.currentTarget.style.display = 'none'
            }}
          />
        )}
        <span className={`pill ${s.tone} lot-badge`}>{s.ar}</span>
        <span className="lot-channel">{a.channel === 'Onsite' ? '📍 حضوري' : '🌐 إلكتروني'}</span>
        {/* The clock, over the cover — only where there is a clock to show. A hall
            auction has none: the auctioneer brings the hammer down (§29). */}
        <CardClock status={a.status} channel={a.channel} startsAt={a.startsAt} endsAt={a.endsAt} />
      </button>

      <div className="lot-body">
        <h3>
          <button className="link" onClick={() => onOpen(a.id)}>
            {a.nameAr}
          </button>
        </h3>
        {a.nameEn && <p className="lot-subtitle ltr">{a.nameEn}</p>}
        <div className="lot-specs">
          <span>
            <Icon name="area" size={15} />
            <span className="num">{a.totalAreaSqm}</span> م²
          </span>
          <span>
            <Icon name="grid" size={15} />
            {a.plotCount} قطعة
          </span>
          <span>
            <Icon name="shield" size={15} />
            تأمين <span className="num">{sar(a.depositMinorUnits, 'ar')}</span>
          </span>
        </div>
        <div className="price-caption">{bidding ? 'أعلى مزايدة' : 'سعر الافتتاح'}</div>
        <div className="lot-price num">
          {sar(bidding ? a.priceMinorUnits! : a.openingPriceMinorUnits, 'ar')}
        </div>
        <div className="card-bottom">
          <span className="muted small">
            الكراسة {a.bookletPriceMinorUnits === 0 ? 'مجانية' : sar(a.bookletPriceMinorUnits, 'ar')}
          </span>
          <button className="small" onClick={() => onOpen(a.id)}>
            تفاصيل المزاد
          </button>
        </div>
      </div>
    </article>
  )
}
