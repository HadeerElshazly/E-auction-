import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, Icon, PageHead, Pager, api, config, riyals, usePage, type Session } from '@eauction/shared'
import { useBidders } from './winners'
import { BidHistory } from './AuditViews'

interface Pending {
  id: string
  nameAr: string
  nameEn: string
  candidateId: string | null
  amountMinorUnits: number | null
}

interface Awarded {
  auctionId: string
  nameAr: string
  status: string
  award: {
    bidderId: string
    amountMinorUnits: number
    letterDocumentId: string | null
    signedLetterDocumentId: string | null
    winnerNotifiedAt: string | null
    remainingMinorUnits: number
    settledAt: string | null
  }
}

/**
 * لجنة الترسية — the committee's board, one card per auction: the preliminary
 * winner and the offer, and the decision that is the committee's to make. Nothing
 * here passes an auction to the next bidder by itself; a refusal says why.
 */
export function Committee({
  session,
  committeeUserId,
  runDecision,
  onOpenAuction,
  onOpenBids,
}: {
  session: Session
  committeeUserId: string
  /** The portal's step-up runner: confirming an award asks for a fresh code. */
  runDecision: (work: () => Promise<unknown>) => Promise<unknown>
  onOpenAuction: (id: string) => void
  /** The whole bid log of an auction, on its own screen. */
  onOpenBids: (id: string) => void
}) {
  const admin = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])
  const [pending, setPending] = useState<Pending[] | null>(null)
  const [awarded, setAwarded] = useState<Awarded[]>([])
  const [refusing, setRefusing] = useState<string | null>(null)
  const [reason, setReason] = useState('')
  const [bidsFor, setBidsFor] = useState<{ id: string; nameAr: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // Both lists paged by the auction service, ten at a time.
  const pendingPage = usePage()
  const awardedPage = usePage()
  const [totals, setTotals] = useState({ pending: 0, awarded: 0 })

  const load = useCallback(async () => {
    try {
      const waiting = await admin.get<{ items: Array<{ id: string; nameAr: string; nameEn: string }>; total: number }>(
        `/auctions?status=PendingAward&${pendingPage.query}`,
      )
      setPending(
        await Promise.all(
          waiting.items.map(async (x) => {
            const full = await admin.get<{
              pendingCandidateBidderId: string | null
              pendingCandidateAmountMinorUnits: number | null
            }>(`/auctions/${x.id}`)
            return {
              ...x,
              candidateId: full.pendingCandidateBidderId,
              amountMinorUnits: full.pendingCandidateAmountMinorUnits,
            }
          }),
        ),
      )
      const followUp = await admin.get<{ items: Awarded[]; total: number }>(`/awards/follow-up?${awardedPage.query}`)
      setAwarded(followUp.items)
      setTotals({ pending: waiting.total, awarded: followUp.total })
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? `تعذّر التحميل (${e.status}).` : String(e))
    }
  }, [admin, pendingPage.query, awardedPage.query])
  useEffect(() => {
    void load()
  }, [load])

  const run = async (work: () => Promise<unknown>, decision = false) => {
    setBusy(true)
    setError(null)
    try {
      await (decision ? runDecision(work) : work())
      setRefusing(null)
      setReason('')
      await load()
    } catch (e) {
      setError(e instanceof ApiError && e.problems.length > 0 ? e.problems.join(' ') : e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const bidder = useBidders(session, [
    ...(pending?.map((p) => p.candidateId) ?? []),
    ...awarded.map((a) => a.award.bidderId),
  ])
  const nameOf = (id: string | null) => (id ? bidder(id)?.nameAr ?? '…' : 'لا يوجد عرض صحيح')

  return (
    <>
      <PageHead
        eyebrow="مساحة لجنة الترسية"
        title="لجنة الترسية"
        sub="اعتماد النتيجة المبدئية وإدارة خطابات الترسية."
      />
      <div className="notice ok" style={{ marginBottom: 22 }}>
        أعلى عرض صحيح يحدد الفائز المبدئي. تصبح الترسية نهائية بعد اعتماد اللجنة، ولا تُنقل إلى المزايد
        التالي تلقائياً.
      </div>
      {error && <div className="notice error">{error}</div>}
      {pending === null && <p className="muted">…</p>}
      {pending !== null && totals.pending === 0 && totals.awarded === 0 && (
        <div className="card empty-state">
          <h3>لا توجد قرارات بانتظار اللجنة</h3>
          <p className="muted">تظهر هنا المزادات فور إغلاقها وترشيح أعلى مزايد فيها.</p>
        </div>
      )}

      {pending?.map((p) => (
        <section className="card decision-card" key={p.id} data-testid="committee-pending">
          <div className="auction-summary">
            <div>
              <h2>{p.nameAr}</h2>
              <p className="muted">الفائز المبدئي: {nameOf(p.candidateId)}</p>
            </div>
            <div>
              <small className="muted">قيمة العرض</small>
              <div className="summary-price">
                <small>ر.س</small>
                <span className="num">{p.amountMinorUnits != null ? riyals(p.amountMinorUnits, 'ar') : '—'}</span>
              </div>
            </div>
            <span className="pill wait">بانتظار قرار اللجنة</span>
          </div>
          <hr className="divider" />
          {refusing === p.id ? (
            <div className="decision-cell">
              <input
                placeholder="سبب رفض النتيجة"
                aria-label={`سبب رفض نتيجة ${p.nameAr}`}
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
              <button
                className="danger small"
                disabled={busy || !reason.trim()}
                onClick={() =>
                  void run(() => admin.post(`/auctions/${p.id}/result/reject`, { reason: reason.trim() }), true)
                }
              >
                تأكيد الرفض
              </button>
              <button className="ghost small" onClick={() => setRefusing(null)}>
                رجوع
              </button>
            </div>
          ) : (
            <div className="inline-actions">
              <button className="small" onClick={() => setBidsFor({ id: p.id, nameAr: p.nameAr })}>
                سجل العروض
              </button>
              <button
                className="primary small"
                disabled={busy || !p.candidateId}
                onClick={() => void run(() => admin.post(`/auctions/${p.id}/award`, { committeeUserId }), true)}
              >
                اعتماد الترسية
              </button>
              <button className="danger small" disabled={busy} onClick={() => setRefusing(p.id)}>
                رفض مع السبب
              </button>
            </div>
          )}
        </section>
      ))}
      <Pager page={pendingPage.page} total={totals.pending} noun="نتيجة" onPage={pendingPage.setPage} />

      {awarded.map((a) => {
        const w = a.award
        const base = `/auctions/${a.auctionId}/award`
        return (
          <section className="card decision-card" key={a.auctionId} data-testid="committee-awarded">
            <div className="auction-summary">
              <div>
                <h2>{a.nameAr}</h2>
                <p className="muted">الفائز: {nameOf(w.bidderId)}</p>
              </div>
              <div>
                <small className="muted">قيمة العرض</small>
                <div className="summary-price">
                  <small>ر.س</small>
                  <span className="num">{riyals(w.amountMinorUnits, 'ar')}</span>
                </div>
              </div>
              <span className={`pill ${a.status === 'Settled' ? 'done' : 'live'}`}>
                {a.status === 'Settled' ? 'تمت التسوية' : 'ترسية معتمدة'}
              </span>
            </div>
            <hr className="divider" />
            <div className="inline-actions">
              <button className="small" onClick={() => setBidsFor({ id: a.auctionId, nameAr: a.nameAr })}>
                سجل العروض
              </button>
              {a.status === 'Awarded' && !w.letterDocumentId && (
                <button
                  className="small"
                  disabled={busy}
                  onClick={() => void run(() => admin.post(`${base}/letter`, { documentId: crypto.randomUUID() }))}
                >
                  <Icon name="file" size={15} /> إصدار خطاب الترسية
                </button>
              )}
              {a.status === 'Awarded' && w.letterDocumentId && !w.signedLetterDocumentId && (
                <button
                  className="small"
                  disabled={busy}
                  onClick={() =>
                    void run(() => admin.post(`${base}/signed-letter`, { documentId: crypto.randomUUID() }))
                  }
                >
                  رفع الخطاب الموقع
                </button>
              )}
              {a.status === 'Awarded' && w.signedLetterDocumentId && !w.winnerNotifiedAt && (
                <button className="primary small" disabled={busy} onClick={() => void run(() => admin.post(`${base}/notify`))}>
                  إشعار الفائز
                </button>
              )}
              {w.signedLetterDocumentId && <span className="pill live">الخطاب الموقع محفوظ</span>}
              {a.status === 'Awarded' && w.winnerNotifiedAt && (
                <button
                  className="primary small"
                  disabled={busy || w.remainingMinorUnits > 0}
                  title={w.remainingMinorUnits > 0 ? `المتبقي ${riyals(w.remainingMinorUnits, 'ar')} ر.س` : undefined}
                  onClick={() => void run(() => admin.post(`/auctions/${a.auctionId}/settle`), true)}
                >
                  اعتماد التسوية
                </button>
              )}
              <button className="ghost small" onClick={() => onOpenAuction(a.auctionId)}>
                تفاصيل الترسية
              </button>
            </div>
          </section>
        )
      })}
      <Pager page={awardedPage.page} total={totals.awarded} noun="ترسية" onPage={awardedPage.setPage} />

      {bidsFor && (
        <div className="modal-backdrop" role="dialog" aria-modal="true" onClick={() => setBidsFor(null)}>
          <div className="modal wide-modal" onClick={(e) => e.stopPropagation()}>
            <div className="modal-head">
              <div className="grow">
                <h2>سجل العروض</h2>
                <p>{bidsFor.nameAr}</p>
              </div>
              <button className="icon-btn" aria-label="إغلاق" onClick={() => setBidsFor(null)}>
                <Icon name="close" size={18} />
              </button>
            </div>
            <BidHistory
              session={session}
              auctionId={bidsFor.id}
              withDecisions={false}
              preview={5}
              onOpenFull={() => onOpenBids(bidsFor.id)}
            />
          </div>
        </div>
      )}
    </>
  )
}
