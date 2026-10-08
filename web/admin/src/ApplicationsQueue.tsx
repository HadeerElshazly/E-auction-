import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, Icon, PageHead, Stats, api, config, when, type Session } from '@eauction/shared'

interface Row {
  application: {
    bidderId: string
    auctionId: string
    nameAr: string
    eligibility: 'Incomplete' | 'UnderReview' | 'Accepted' | 'Rejected'
    eligibilityReason: string | null
    depositMethod: string | null
    depositPaidAt: string | null
    guaranteeDocumentId: string | null
    guaranteeExpiresAt: string | null
    eligibleAt: string | null
  }
  auctionNameAr: string | null
}

type Filter = 'UnderReview' | 'Accepted' | 'Rejected' | 'Incomplete' | 'all'

const eligibilityAr: Record<Row['application']['eligibility'], { ar: string; tone: string }> = {
  Incomplete: { ar: 'قيد الاستكمال', tone: 'done' },
  UnderReview: { ar: 'قيد المراجعة', tone: 'wait' },
  Accepted: { ar: 'معتمدة', tone: 'live' },
  Rejected: { ar: 'مرفوضة', tone: 'bad' },
}

/**
 * طلبات المشاركة — every application across the auctions, those waiting on a
 * decision first. A bank guarantee is reviewed here: opened, then accepted or
 * refused with its reason, without going auction by auction.
 */
export function ApplicationsQueue({
  session,
  onOpenAuction,
}: {
  session: Session
  onOpenAuction: (id: string) => void
}) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const documents = useMemo(() => api({ baseUrl: config.documentsApi, session }), [session])
  const [filter, setFilter] = useState<Filter>('UnderReview')
  const [page, setPage] = useState<{ items: Row[]; counts: Record<string, number> } | null>(null)
  const [refusing, setRefusing] = useState<string | null>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    participant
      .get<{ items: Row[]; counts: Record<string, number> }>(
        `/applications${filter === 'all' ? '' : `?eligibility=${filter}`}`,
      )
      .then(setPage)
      .catch((e) => setError(e instanceof ApiError ? `تعذّر التحميل (${e.status}).` : String(e)))
  }, [participant, filter])
  useEffect(() => load(), [load])

  const run = async (work: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await work()
      setRefusing(null)
      setReason('')
      load()
    } catch (e) {
      setError(e instanceof ApiError && e.problems.length > 0 ? e.problems.join(' ') : String(e))
    } finally {
      setBusy(false)
    }
  }

  const tabs: Array<{ key: Filter; ar: string }> = [
    { key: 'UnderReview', ar: 'قيد المراجعة' },
    { key: 'Accepted', ar: 'معتمدة' },
    { key: 'Rejected', ar: 'مرفوضة' },
    { key: 'Incomplete', ar: 'قيد الاستكمال' },
    { key: 'all', ar: 'الكل' },
  ]

  return (
    <>
      <PageHead
        eyebrow="مساحة الإدارة"
        title="طلبات المشاركة"
        sub="مراجعة أهلية المشاركين والضمانات البنكية في كل المزادات."
      />

      <Stats
        items={[
          { label: 'كل الطلبات', value: page?.counts.all ?? 0, icon: 'file' },
          { label: 'قيد المراجعة', value: page?.counts.UnderReview ?? 0, icon: 'clock' },
          { label: 'معتمدة', value: page?.counts.Accepted ?? 0, icon: 'shield' },
          { label: 'مرفوضة', value: page?.counts.Rejected ?? 0, icon: 'close' },
        ]}
      />

      <div className="toolbar">
        <div className="tabs" role="tablist" aria-label="حالة الطلب">
          {tabs.map((t) => (
            <button
              key={t.key}
              role="tab"
              aria-selected={filter === t.key}
              className={`tab${filter === t.key ? ' active' : ''}`}
              onClick={() => setFilter(t.key)}
            >
              {t.ar} <span className="num">{page?.counts[t.key] ?? 0}</span>
            </button>
          ))}
        </div>
      </div>

      {error && <div className="notice error">{error}</div>}

      <div className="card table-card">
        {page && page.items.length === 0 ? (
          <div className="empty-state">
            <h3>لا توجد طلبات هنا</h3>
            <p className="muted">تظهر هنا طلبات المشاركة حين يبدأ المزايدون التسجيل.</p>
          </div>
        ) : (
          <div className="table-scroll">
            <table data-testid="applications-queue">
              <thead>
                <tr>
                  <th>المشارك</th>
                  <th>المزاد</th>
                  <th>التأمين</th>
                  <th>الحالة</th>
                  <th>الإجراء</th>
                </tr>
              </thead>
              <tbody>
                {page?.items.map(({ application: a, auctionNameAr }) => {
                  const key = `${a.auctionId}:${a.bidderId}`
                  const e = eligibilityAr[a.eligibility]
                  const base = `/auctions/${a.auctionId}/subscriptions/${a.bidderId}`
                  const guarantee = a.depositMethod === 'BankGuarantee'
                  return (
                    <tr key={key}>
                      <td>
                        <b>{a.nameAr}</b>
                        <small>فرد · هوية موثّقة عبر نفاذ</small>
                      </td>
                      <td>
                        <button className="link" onClick={() => onOpenAuction(a.auctionId)}>
                          {auctionNameAr ?? 'مزاد'}
                        </button>
                      </td>
                      <td>
                        {guarantee ? 'ضمان بنكي' : a.depositMethod ? 'دفع إلكتروني' : '—'}
                        <small>
                          {guarantee && a.guaranteeExpiresAt && <>ينتهي {when(a.guaranteeExpiresAt)} · </>}
                          {a.depositPaidAt ? 'مؤكَّد' : guarantee ? 'بانتظار المراجعة' : 'لم يُسدَّد'}
                        </small>
                      </td>
                      <td>
                        <span className={`pill ${e.tone}`}>{e.ar}</span>
                        {a.eligibilityReason && <small>{a.eligibilityReason}</small>}
                      </td>
                      <td>
                        {a.eligibility === 'UnderReview' && guarantee ? (
                          refusing === key ? (
                            <div className="decision-cell">
                              <input
                                placeholder="سبب الرفض"
                                aria-label={`سبب رفض طلب ${a.nameAr}`}
                                value={reason}
                                onChange={(ev) => setReason(ev.target.value)}
                              />
                              <button
                                className="danger small"
                                disabled={busy || !reason.trim()}
                                onClick={() => void run(() => participant.post(`${base}/guarantee/reject`, { reason: reason.trim() }))}
                              >
                                تأكيد الرفض
                              </button>
                              <button className="ghost small" onClick={() => setRefusing(null)}>
                                رجوع
                              </button>
                            </div>
                          ) : (
                            <div className="actions">
                              {a.guaranteeDocumentId && (
                                <button
                                  className="ghost small"
                                  disabled={busy}
                                  onClick={() =>
                                    void run(() =>
                                      documents.download(`/documents/${a.guaranteeDocumentId}`, `ضمان-${a.nameAr}.pdf`),
                                    )
                                  }
                                >
                                  <Icon name="file" size={15} /> الضمان
                                </button>
                              )}
                              <button
                                className="primary small"
                                disabled={busy}
                                onClick={() =>
                                  void run(() =>
                                    participant.post(`${base}/guarantee/verify`, { verifiedByUserId: session.subject }),
                                  )
                                }
                              >
                                اعتماد
                              </button>
                              <button className="danger small" disabled={busy} onClick={() => setRefusing(key)}>
                                رفض
                              </button>
                            </div>
                          )
                        ) : (
                          <span className="muted small">
                            {a.eligibility === 'Accepted' && a.eligibleAt ? `اعتُمد ${when(a.eligibleAt)}` : 'لا إجراء مطلوب'}
                          </span>
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
