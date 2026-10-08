import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, Pager, api, config, usePage, when, type Session } from '@eauction/shared'
import type { Auction } from './types'

/** One bidder's application, from the participant service's review list. */
interface Application {
  bidderId: string
  nameAr: string
  status: string
  eligibility: 'Incomplete' | 'UnderReview' | 'Accepted' | 'Rejected'
  eligibilityReason: string | null
  bookletPurchasedAt: string | null
  bookletFree: boolean
  termsAcceptedAt: string | null
  acceptedBookletDocumentId: string | null
  depositMethod: string | null
  depositPaidAt: string | null
  guaranteeDocumentId: string | null
  guaranteeExpiresAt: string | null
  eligibleAt: string | null
}

const eligibilityAr: Record<Application['eligibility'], { ar: string; tone: string }> = {
  Incomplete: { ar: 'قيد الاستكمال', tone: 'done' },
  UnderReview: { ar: 'قيد المراجعة', tone: 'wait' },
  Accepted: { ar: 'مقبول', tone: 'live' },
  Rejected: { ar: 'مرفوض', tone: 'bad' },
}

/** Before approval no bidder can apply, so there is nothing to review. */
const reviewable = new Set(['Draft', 'PendingReview', 'Rejected'])

/**
 * While bidding is still to come or under way — the only time eligibility can be
 * granted or withdrawn. After the close the list is a record; a non-compliant
 * winner is disqualified on the award (سحب الفوز), not here.
 */
const decidable = new Set(['Approved', 'Scheduled', 'Live'])

interface Props {
  auction: Auction
  session: Session
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}

/**
 * المتقدّمون — every bidder who applied to this auction, and the one decision staff
 * make about them: accept or reject a bank guarantee, or withdraw an eligibility
 * already granted. Each refusal needs a reason, because the bidder is shown it.
 *
 * A paid deposit needs no one's approval — the gateway's settlement is the
 * decision — so those rows carry no accept button.
 */
export function Applicants({ auction, session, busy, onAct }: Props) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const documents = useMemo(() => api({ baseUrl: config.documentsApi, session }), [session])

  const [items, setItems] = useState<Application[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)

  /** The row being refused, and what the administrator has typed as the reason. */
  const [refusing, setRefusing] = useState<{ bidderId: string; kind: 'guarantee' | 'revoke' } | null>(null)
  const [reason, setReason] = useState('')

  const paging = usePage()
  const [total, setTotal] = useState(0)
  const [counts, setCounts] = useState<Record<string, number>>({})
  const load = useCallback(async () => {
    try {
      const r = await participant.get<{ items: Application[]; total: number; counts: Record<string, number> }>(
        `/auctions/${auction.id}/applications?${paging.query}`,
      )
      setItems(r.items)
      setTotal(r.total)
      setCounts(r.counts)
      setLoadError(null)
    } catch (e) {
      setLoadError(e instanceof ApiError ? `تعذّر تحميل المتقدّمين (${e.status}).` : String(e))
    }
  }, [participant, auction.id, paging.query])

  useEffect(() => {
    if (!reviewable.has(auction.status)) void load()
  }, [load, auction.status])

  if (reviewable.has(auction.status)) return null
  const canDecide = decidable.has(auction.status)

  const base = (bidderId: string) => `/auctions/${auction.id}/subscriptions/${bidderId}`

  const run = (work: () => Promise<unknown>) =>
    onAct(async () => {
      await work()
      setRefusing(null)
      setReason('')
      await load()
    })


  return (
    <div className="card">
      <div className="section-head">
        <h2>المتقدّمون</h2>
        {items && (
          <span className="pill teal plain">
            {total} متقدّم
            {counts.UnderReview ? ` · ${counts.UnderReview} قيد المراجعة` : ''}
          </span>
        )}
        <span className="grow" />
        <button className="ghost" disabled={busy} onClick={() => void load()}>
          تحديث
        </button>
      </div>
      <p className="lede">
        {canDecide
          ? 'راجع الضمانات البنكية المرفوعة واقبلها أو ارفضها مع ذكر السبب. التأمين المدفوع إلكترونياً يُقبل تلقائياً عند تأكيد بوابة الدفع.'
          : 'انتهت المزايدة، فالقائمة للاطلاع فقط. إن لم يستوفِ الفائز الشروط فسحب الفوز من صلاحية لجنة الترسية في قسم «الترسية».'}
      </p>

      {loadError && <div className="notice error">{loadError}</div>}

      {items && items.length === 0 && (
        <p className="muted small">لم يتقدّم أحد لهذا المزاد بعد.</p>
      )}

      {items && items.length > 0 && (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>المتقدّم</th>
                <th>كراسة الشروط</th>
                <th>الموافقة على الشروط</th>
                <th>التأمين</th>
                <th>الأهلية</th>
                <th>الإجراء</th>
              </tr>
            </thead>
            <tbody>
              {items.map((a) => {
                const e = eligibilityAr[a.eligibility] ?? eligibilityAr.Incomplete
                const open = refusing?.bidderId === a.bidderId
                return (
                  <tr key={a.bidderId}>
                    <td className="strong">{a.nameAr}</td>
                    <td className="small">
                      {a.bookletPurchasedAt
                        ? `${a.bookletFree ? 'مجانية' : 'مدفوعة'} — ${when(a.bookletPurchasedAt)}`
                        : <span className="muted">لم تُستلم</span>}
                    </td>
                    <td className="small">
                      {a.termsAcceptedAt ? (
                        <>
                          {when(a.termsAcceptedAt)}
                          {a.acceptedBookletDocumentId && (
                            <div className="muted">
                              النسخة <span className="mono">{a.acceptedBookletDocumentId.slice(0, 8)}</span>
                            </div>
                          )}
                        </>
                      ) : (
                        <span className="muted">لم تتم</span>
                      )}
                    </td>
                    <td className="small">
                      {a.depositMethod === 'BankGuarantee' ? (
                        a.guaranteeDocumentId ? (
                          <>
                            ضمان بنكي
                            <div className="muted">
                              ينتهي {when(a.guaranteeExpiresAt)} ·{' '}
                              <a
                                href="#"
                                onClick={(ev) => {
                                  ev.preventDefault()
                                  void onAct(() =>
                                    documents.download(
                                      `/documents/${a.guaranteeDocumentId}`,
                                      `ضمان-${a.nameAr}.pdf`,
                                    ),
                                  )
                                }}
                              >
                                عرض
                              </a>
                            </div>
                          </>
                        ) : (
                          <span className="muted">ضمان بنكي — لم يُرفع</span>
                        )
                      ) : a.depositPaidAt ? (
                        `مدفوع — ${when(a.depositPaidAt)}`
                      ) : a.depositMethod === 'Payment' ? (
                        <span className="muted">دفع إلكتروني — لم يُسدَّد</span>
                      ) : (
                        <span className="muted">—</span>
                      )}
                    </td>
                    <td>
                      <span className={`pill ${e.tone}`}>{e.ar}</span>
                      {a.eligibilityReason && (
                        <div className="small" style={{ color: 'var(--danger)', marginTop: 4 }}>
                          {a.eligibilityReason}
                        </div>
                      )}
                    </td>
                    <td>
                      {!canDecide ? (
                        <span className="muted small">—</span>
                      ) : open ? (
                        <div className="refuse">
                          <input
                            autoFocus
                            value={reason}
                            placeholder="سبب الرفض (يظهر للمتقدّم)"
                            aria-label="سبب الرفض"
                            onChange={(ev) => setReason(ev.target.value)}
                          />
                          <div className="row">
                            <button
                              className="danger"
                              disabled={busy || reason.trim() === ''}
                              onClick={() =>
                                void run(() =>
                                  participant.post(
                                    `${base(a.bidderId)}/${refusing!.kind === 'guarantee' ? 'guarantee/reject' : 'revoke'}`,
                                    { reason: reason.trim() },
                                  ),
                                )
                              }
                            >
                              تأكيد الرفض
                            </button>
                            <button
                              className="ghost"
                              disabled={busy}
                              onClick={() => {
                                setRefusing(null)
                                setReason('')
                              }}
                            >
                              إلغاء
                            </button>
                          </div>
                        </div>
                      ) : a.eligibility === 'UnderReview' && a.guaranteeDocumentId ? (
                        <div className="row">
                          <button
                            className="primary"
                            disabled={busy}
                            onClick={() =>
                              void run(() =>
                                participant.post(`${base(a.bidderId)}/guarantee/verify`, {
                                  verifiedByUserId: session.subject,
                                }),
                              )
                            }
                          >
                            قبول
                          </button>
                          <button
                            className="danger"
                            disabled={busy}
                            onClick={() => setRefusing({ bidderId: a.bidderId, kind: 'guarantee' })}
                          >
                            رفض
                          </button>
                        </div>
                      ) : a.eligibility === 'Accepted' ? (
                        <button
                          disabled={busy}
                          onClick={() => setRefusing({ bidderId: a.bidderId, kind: 'revoke' })}
                        >
                          إلغاء التأهيل
                        </button>
                      ) : (
                        <span className="muted small">—</span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          <Pager page={paging.page} total={total} noun="متقدّم" onPage={paging.setPage} />
        </div>
      )}
    </div>
  )
}
