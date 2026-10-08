import { useEffect, useMemo, useState } from 'react'
import { Icon, PageHead, Pager, Stats, api, config, riyals, sar, stageLabels, usePage, useTick, type PageOf, type Session } from '@eauction/shared'
import type { AuctionSummary } from './types'

interface Props {
  auctions: AuctionSummary[]
  session: Session | null
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

interface CataloguePage extends PageOf<AuctionSummary> {
  counts: Record<FilterKey, number>
}

/** المزادات — the public catalogue: what is on offer, what is running, what is next. */
export function Catalogue({ auctions, session, onOpen }: Props) {
  const signedIn = session !== null
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [filter, setFilter] = useState<FilterKey>('all')
  const [page, setPage] = useState<CataloguePage | null>(null)
  const paging = usePage()
  // As whoever is reading: a visitor gets what «إعدادات العرض للزوار» allows, a
  // signed-in bidder the whole public auction.
  const client = useMemo(() => api({ baseUrl: config.queryApi, session }), [session])

  // A new filter or search starts again from the first page.
  useEffect(() => paging.setPage(0), [filter, debounced])

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
    client
      .get<CataloguePage>(`/auctions?${params.toString()}&${paging.query}`)
      .then(setPage)
      .catch(() => undefined)
  }, [client, filter, debounced, auctions, paging.query])

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

      <Pager page={paging.page} total={page?.total ?? 0} noun="مزاد" onPage={paging.setPage} />
    </>
  )
}

/** HH : MM : SS to a moment, as the card counts it — hours run past 24 for a far one. */
function hms(target: string | null, now: number): string | undefined {
  // Hidden from a visitor by «إعدادات العرض للزوار»: no clock rather than a wrong one.
  if (target == null) return undefined
  let n = Math.max(0, Math.floor((new Date(target).getTime() - now) / 1000))
  const h = Math.floor(n / 3600)
  n %= 3600
  return [h, Math.floor(n / 60), n % 60].map((x) => String(x).padStart(2, '0')).join(' : ')
}

/** What the card's corner says about time, by stage. */
function timeLine(a: AuctionSummary, now: number): { caption: string; clock?: string } {
  if (a.status === 'Live')
    return a.channel === 'Onsite'
      ? { caption: 'جارٍ في القاعة — يُغلق بقرار مدير المزاد' }
      : { caption: 'ينتهي خلال', clock: hms(a.endsAt, now) }
  if (a.status === 'Scheduled' || a.status === 'Approved') return { caption: 'يفتح خلال', clock: hms(a.startsAt, now) }
  if (a.status === 'PendingAward' || a.status === 'PendingEligibilityReview') return { caption: 'بانتظار قرار اللجنة' }
  if (a.status === 'Awarded' || a.status === 'Settled') return { caption: 'ترسية معتمدة' }
  if (a.status === 'Cancelled') return { caption: 'أُلغي المزاد' }
  return { caption: 'انتهى المزاد' }
}

function LotCard({ auction: a, onOpen }: { auction: AuctionSummary; onOpen: (id: string) => void }) {
  const s = statusAr[a.status] ?? { ar: a.status, tone: 'done' }
  const live = a.status === 'Live'
  const bidding = live && a.priceMinorUnits != null
  const ticking = live || a.status === 'Scheduled' || a.status === 'Approved'
  const now = useTick(ticking)
  const t = timeLine(a, now)
  return (
    <article className="lot-card auction-card static" data-testid="lot-card">
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
        <span className="lot-number">
          {a.plotCount} قطعة · {a.channel === 'Onsite' ? 'حضوري' : 'إلكتروني'}
        </span>
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
            <Icon name="shield" size={15} />
            تأمين <span className="num">{sar(a.depositMinorUnits, 'ar')}</span>
          </span>
        </div>
        <div className="price-caption">{bidding ? 'أعلى مزايدة' : 'سعر البداية'}</div>
        {(bidding ? a.priceMinorUnits : a.openingPriceMinorUnits) == null ? (
          // Hidden from a visitor by «إعدادات العرض للزوار».
          <div className="lot-price locked-value">
            <Icon name="lock" size={16} /> يظهر بعد تسجيل الدخول
          </div>
        ) : (
          <div className="lot-price">
            <small>ر.س</small>
            <span className="num">{riyals(bidding ? a.priceMinorUnits! : a.openingPriceMinorUnits, 'ar')}</span>
          </div>
        )}
        <div className="card-bottom">
          <div className="time-text">
            {t.caption}
            {t.clock && <b className="num">{t.clock}</b>}
          </div>
          <button className="small" onClick={() => onOpen(a.id)}>
            تفاصيل المزاد
          </button>
        </div>
      </div>
    </article>
  )
}
