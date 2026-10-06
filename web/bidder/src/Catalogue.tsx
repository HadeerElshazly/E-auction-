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

      <div className="auction-grid">
        {auctions.map((a) => {
          const s = statusAr[a.status] ?? { ar: a.status, tone: 'done' }
          const live = a.status === 'Live'
          return (
            <div className="auction-card static" key={a.id}>
              <div className="cover">
                <span className="channel">
                  {a.plotCount} قطعة · {a.totalAreaSqm} م²
                </span>

                {/* The clock, over the cover, as the proposal has it. A live
                    auction shows what is left of it; a scheduled one shows when it
                    opens. A closed one shows nothing, because a countdown on a
                    finished auction is a number nobody can act on. */}
                {live && (
                  <div className="countdown" aria-hidden="true">
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
                  <div className="big-number num">
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
