import { sar, untilText } from '@eauction/shared'
import type { AuctionSummary } from './types'

interface Props {
  auctions: AuctionSummary[]
  signedIn: boolean
  onOpen: (id: string) => void
}

const statusAr: Record<string, { ar: string; tone: string }> = {
  Scheduled: { ar: 'قادم', tone: 'wait' },
  Live: { ar: 'جارٍ الآن', tone: 'live' },
  Closed: { ar: 'أُغلق', tone: 'done' },
  PendingAward: { ar: 'بانتظار الترسية', tone: 'done' },
  Unsold: { ar: 'لم يُبع', tone: 'bad' },
}

export function Catalogue({ auctions, signedIn, onOpen }: Props) {
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

      <div className="grid">
        {auctions.map((a) => {
          const s = statusAr[a.status] ?? { ar: a.status, tone: 'done' }
          const live = a.status === 'Live'
          return (
            <div className="card" key={a.id} style={{ margin: 0 }}>
              <div className="row" style={{ marginBottom: 10 }}>
                <span className={`pill ${s.tone}`}>{s.ar}</span>
                <span className="grow" />
                {live && (
                  <span className="muted small num" title="الوقت المتبقي">
                    {untilText(a.endsAt)}
                  </span>
                )}
              </div>

              <h2 style={{ marginBottom: 6 }}>{a.nameAr}</h2>
              <div className="muted small" style={{ marginBottom: 12 }}>
                {a.plotCount} قطعة · {a.totalAreaSqm} م²
              </div>

              <div className="muted small">{live ? 'السعر الحالي' : 'سعر الافتتاح'}</div>
              <div className="big-number num" style={{ marginBottom: 12 }}>
                {sar(live ? a.priceMinorUnits ?? a.openingPriceMinorUnits : a.openingPriceMinorUnits, 'ar')}
              </div>

              <div className="muted small" style={{ marginBottom: 12 }}>
                التأمين <span className="num">{sar(a.depositMinorUnits, 'ar')}</span>
                {' · '}
                الكراسة <span className="num">{sar(a.bookletPriceMinorUnits, 'ar')}</span>
              </div>

              <button className="primary" onClick={() => onOpen(a.id)}>
                التفاصيل
              </button>
            </div>
          )
        })}
      </div>
    </>
  )
}
