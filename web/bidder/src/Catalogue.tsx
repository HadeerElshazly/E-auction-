import { useMemo, useState } from 'react'
import { config, sar, untilText } from '@eauction/shared'
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

export const statusAr: Record<string, { ar: string; tone: string }> = {
  Scheduled: { ar: 'قادم', tone: 'wait' },
  Live: { ar: 'جارٍ الآن', tone: 'live' },
  Closed: { ar: 'أُغلق', tone: 'done' },
  PendingAward: { ar: 'بانتظار الترسية', tone: 'done' },
  Unsold: { ar: 'لم يُبع', tone: 'bad' },
  Cancelled: { ar: 'أُلغي', tone: 'bad' },
}

/** The status filter, grouped the way a citizen thinks of them rather than by lifecycle. */
const filters = [
  { key: 'all', ar: 'الكل', match: () => true },
  { key: 'upcoming', ar: 'القادمة', match: (s: string) => s === 'Scheduled' },
  { key: 'live', ar: 'الجارية', match: (s: string) => s === 'Live' },
  {
    key: 'closed',
    ar: 'المنتهية',
    match: (s: string) =>
      s === 'Closed' || s === 'PendingAward' || s === 'Unsold' || s === 'Cancelled',
  },
] as const

type FilterKey = (typeof filters)[number]['key']

/** Arabic search that ignores the hamza and taa-marbuta spellings people type either way. */
function normalise(text: string): string {
  return text
    .toLowerCase()
    .replace(/[أإآ]/g, 'ا')
    .replace(/ة/g, 'ه')
    .replace(/ى/g, 'ي')
    .replace(/[ً-ْ]/g, '')
    .trim()
}

export function Catalogue({ auctions, signedIn, onOpen }: Props) {
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<FilterKey>('all')

  const visible = useMemo(() => {
    const q = normalise(query)
    const match = filters.find((f) => f.key === filter)?.match ?? (() => true)
    return auctions.filter(
      (a) =>
        match(a.status) &&
        (q === '' || normalise(`${a.nameAr} ${a.nameEn}`).includes(q)),
    )
  }, [auctions, query, filter])

  const countFor = (key: FilterKey) =>
    auctions.filter((a) => (filters.find((f) => f.key === key)?.match ?? (() => true))(a.status))
      .length

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
