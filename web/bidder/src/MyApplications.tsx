import { useEffect, useMemo, useState } from 'react'
import { ApiError, api, config, when, type Session } from '@eauction/shared'
import type { AuctionSummary, Subscription } from './types'
import { statusAr } from './Catalogue'

interface Props {
  session: Session
  /** The catalogue already loaded, for the auctions' names and states. */
  auctions: AuctionSummary[]
  onOpen: (auctionId: string) => void
  onBack: () => void
}

const eligibilityAr: Record<Subscription['eligibility'], { ar: string; tone: string }> = {
  Incomplete: { ar: 'قيد الاستكمال', tone: 'done' },
  UnderReview: { ar: 'قيد المراجعة', tone: 'wait' },
  Accepted: { ar: 'مقبول', tone: 'live' },
  Rejected: { ar: 'مرفوض', tone: 'bad' },
}

const depositAr: Partial<Record<Subscription['depositSettlement'], string>> = {
  Held: 'محتجز حتى انتهاء المزاد',
  ToRefund: 'قيد الرد',
  ToRelease: 'قيد تحرير الضمان',
  ToForfeit: 'مصادَر',
  AppliedToPurchase: 'احتُسب من ثمن الترسية',
  Closed: 'تمت التسوية',
}

/**
 * طلباتي — «صفحة شخصية لمتابعة الحالة» (الخاصية 09). Every auction this bidder applied
 * to in one place: where the application stands, why if it was refused, and what
 * happened to the deposit — without opening each auction to find out.
 */
export function MyApplications({ session, auctions, onOpen, onBack }: Props) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const [items, setItems] = useState<Subscription[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    participant
      .get<{ items: Subscription[] }>(`/bidders/${session.subject}/subscriptions`)
      .then((r) => setItems(r.items))
      .catch((e) =>
        setError(e instanceof ApiError ? `تعذّر تحميل طلباتك (${e.status}).` : String(e)),
      )
  }, [participant, session.subject])

  const byId = useMemo(() => new Map(auctions.map((a) => [a.id, a])), [auctions])

  return (
    <>
      <button className="back-link" onClick={onBack}>
        → كل المزادات
      </button>

      <div className="card">
        <div className="section-head">
          <h2>طلباتي</h2>
          {items && <span className="pill teal plain">{items.length}</span>}
        </div>
        <p className="lede">المزادات التي اشتركت فيها، وحالة أهليتك والتأمين في كلٍّ منها.</p>

        {error && <div className="notice error">{error}</div>}
        {items === null && !error && <p className="muted">…</p>}
        {items?.length === 0 && (
          <p className="muted">لم تشترك في أي مزاد بعد. اختر مزاداً من القائمة للبدء.</p>
        )}

        {items && items.length > 0 && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>المزاد</th>
                  <th>حالة المزاد</th>
                  <th>الأهلية</th>
                  <th>التأمين</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {items.map((s) => {
                  const a = byId.get(s.auctionId)
                  const st = a ? (statusAr[a.status] ?? { ar: a.status, tone: 'done' }) : null
                  const e = eligibilityAr[s.eligibility] ?? eligibilityAr.Incomplete
                  return (
                    <tr key={s.id}>
                      <td>
                        <div className="strong">{a?.nameAr ?? 'مزاد غير معروض'}</div>
                        {a && <div className="muted small">يبدأ {when(a.startsAt)}</div>}
                      </td>
                      <td>{st && <span className={`pill ${st.tone}`}>{st.ar}</span>}</td>
                      <td>
                        <span className={`pill ${e.tone}`}>{e.ar}</span>
                        {s.eligibilityReason && (
                          <div className="small" style={{ color: 'var(--danger)', marginTop: 4 }}>
                            {s.eligibilityReason}
                          </div>
                        )}
                      </td>
                      <td className="small">
                        {depositAr[s.depositSettlement] ?? <span className="muted">—</span>}
                      </td>
                      <td>
                        {a && (
                          <button className="ghost" onClick={() => onOpen(s.auctionId)}>
                            فتح المزاد
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
