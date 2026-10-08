import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ApiError,
  api,
  config,
  day,
  parseRiyals,
  riyals,
  sar,
  stageLabel,
  when,
  type Session,
} from '@eauction/shared'
import { useBidders } from './winners'

/**
 * متابعة الترسية — the requirements' feature 11.
 *
 * The winner pays the land price, and the title passes at the notary, outside this
 * platform; integrating either is out of the first phase. What stays in scope is
 * the manual record, so every action here writes an entry against a reference a
 * person can trace — a receipt number, a deed number, a refund reference — and none
 * of them moves money.
 */

interface Receipt {
  id: string
  kind: 'Payment' | 'DepositCredit'
  amountMinorUnits: number
  paidOn: string
  reference: string
  documentId: string | null
  recordedAt: string
}

interface Award {
  id: string
  bidderId: string
  amountMinorUnits: number
  complianceDeadline: string
  settledAt: string | null
  winnerNotifiedAt: string | null
  paidMinorUnits: number
  remainingMinorUnits: number
  overdue: boolean
  receipts: Receipt[]
  transferStatus: 'NotStarted' | 'InProgress' | 'Completed'
  transferReference: string | null
  transferUpdatedAt: string | null
  transferCompletedAt: string | null
}

interface FollowUpEntry {
  auctionId: string
  nameAr: string
  status: string
  phase: string | null
  award: Award
}

interface Deposit {
  auctionId: string
  bidderId: string
  nameAr: string
  depositMethod: string | null
  depositPaymentRef: string | null
  depositSettlement: 'ToRefund' | 'ToRelease' | 'ToForfeit'
}

const transferAr: Record<Award['transferStatus'], { ar: string; tone: string }> = {
  NotStarted: { ar: 'لم يبدأ', tone: 'done' },
  InProgress: { ar: 'جارٍ', tone: 'wait' },
  Completed: { ar: 'مكتمل', tone: 'live' },
}

const depositAr: Record<Deposit['depositSettlement'], string> = {
  ToRefund: 'رد التأمين',
  ToRelease: 'تحرير الضمان',
  ToForfeit: 'مصادرة',
}

interface Props {
  session: Session
  /** Only administrators record; the committee and finance read. */
  canRecord: boolean
  /** The committee decides here too: confirm or refuse a result, approve a settlement. */
  canDecide: boolean
  committeeUserId: string
  /**
   * Runs a committee decision through the portal's step-up: confirming an award
   * asks for a fresh second factor, exactly as it does on the auction page.
   */
  runDecision: (work: () => Promise<unknown>) => Promise<unknown>
  onOpenAuction?: (id: string) => void
  /**
   * Which page this is. «لجنة الترسية»: results waiting for the committee, and the
   * awards it will settle. «التسويات والإفراغ»: receipts, the transfer, and the
   * deposits still to resolve.
   */
  mode?: 'committee' | 'settlement'
}

/** A result waiting for the committee: who the processor put forward, at what price. */
interface PendingResult {
  id: string
  nameAr: string
  candidateId: string | null
  amountMinorUnits: number | null
}

export function FollowUp({
  session,
  canRecord,
  canDecide,
  committeeUserId,
  runDecision,
  onOpenAuction,
  mode = 'settlement',
}: Props) {
  const committee = mode === 'committee'
  const admin = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const documents = useMemo(() => api({ baseUrl: config.documentsApi, session }), [session])

  const [awards, setAwards] = useState<FollowUpEntry[] | null>(null)
  const [pending, setPending] = useState<PendingResult[]>([])
  const [reasons, setReasons] = useState<Record<string, string>>({})
  const [deposits, setDeposits] = useState<Deposit[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [a, d] = await Promise.all([
        admin.get<{ items: FollowUpEntry[] }>('/awards/follow-up'),
        participant.get<{ items: Deposit[] }>('/deposits/unsettled'),
      ])
      setAwards(a.items)
      setDeposits(d.items)
      setError(null)

      // Results awaiting the committee. The list gives the auctions; each one's
      // candidate and amount come from the auction itself.
      const waiting = await admin.get<{ items: Array<{ id: string; nameAr: string }> }>(
        '/auctions?status=PendingAward&take=50',
      )
      setPending(
        await Promise.all(
          waiting.items.map(async (x) => {
            const full = await admin.get<{
              pendingCandidateBidderId: string | null
              pendingCandidateAmountMinorUnits: number | null
            }>(`/auctions/${x.id}`)
            return {
              id: x.id,
              nameAr: x.nameAr,
              candidateId: full.pendingCandidateBidderId,
              amountMinorUnits: full.pendingCandidateAmountMinorUnits,
            }
          }),
        ),
      )
    } catch (e) {
      setError(describe(e))
    }
  }, [admin, participant])

  useEffect(() => {
    void load()
  }, [load])

  const act = async (work: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await work()
      await load()
    } catch (e) {
      setError(describe(e))
    } finally {
      setBusy(false)
    }
  }

  // The committee's decisions go through the step-up runner, not straight to the API.
  const decide = (work: () => Promise<unknown>) => act(() => runDecision(work))

  // The award carries the winner's id; the name is the participant service's,
  // through its staff lookup — the committee and operators read this page too.
  const bidder = useBidders(session, [
    ...(awards?.map((a) => a.award.bidderId) ?? []),
    ...pending.map((p) => p.candidateId),
  ])

  // Every auction's name, not only the awarded ones': a deposit to settle may belong
  // to an auction that ended unsold or was cancelled.
  const [names, setNames] = useState<Map<string, string>>(new Map())
  useEffect(() => {
    admin
      .get<{ items: Array<{ id: string; nameAr: string }> }>('/auctions?take=200')
      .then((r) => setNames(new Map(r.items.map((x) => [x.id, x.nameAr]))))
      .catch(() => undefined)
  }, [admin])
  const auctionName = (id: string) =>
    names.get(id) ?? awards?.find((a) => a.auctionId === id)?.nameAr ?? id.slice(0, 8)

  const overdue = awards?.filter((a) => a.award.overdue).length ?? 0

  return (
    <>
      <div className="page-head">
        <div>
          <div className="eyebrow">مساحة الإدارة</div>
          <h1>{committee ? 'لجنة الترسية' : 'التسويات والإفراغ'}</h1>
          <p>
            {committee
              ? 'اعتماد النتيجة المبدئية أو رفضها، ومتابعة الترسيات حتى اعتماد التسوية. لا تنتقل الترسية إلى المزايد التالي تلقائياً.'
              : 'سداد ثمن الترسية والإفراغ ورد التأمينات تتم خارج المنصة؛ تُسجَّل هنا يدوياً مقابل مرجع يمكن تتبّعه.'}
          </p>
        </div>
        <span className="grow" />
        <button disabled={busy} onClick={() => void load()}>
          تحديث
        </button>
      </div>

      {error && <div className="notice error">{error}</div>}

      {overdue > 0 && (
        <div className="notice error">
          {overdue === 1 ? 'ترسية واحدة متعثرة' : `${overdue} ترسيات متعثرة`} — تجاوزت مهلة السداد
          دون سداد كامل، وتنتظر مراجعة يدوية.
        </div>
      )}

      {committee && pending.length === 0 && (
        <div className="card empty-state">
          <h3>لا توجد نتائج بانتظار قرار اللجنة</h3>
          <p className="muted">تظهر هنا المزادات فور إغلاقها وترشيح أعلى مزايد فيها.</p>
        </div>
      )}

      {committee && pending.length > 0 && (
        <div className="card" data-testid="pending-decisions">
          <div className="section-head">
            <h2>بانتظار قرار لجنة الترسية</h2>
            <span className="pill wait">{pending.length}</span>
          </div>
          <p className="lede">
            نتائج أُغلقت ورشّح المعالج أعلى مزايد فيها. تأكيد الترسية يطلب التحقق بالرمز؛ رفض النتيجة
            يتطلب سبباً ولا ينقلها إلى المزايد التالي.
          </p>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>المزاد</th>
                  <th>المرشّح</th>
                  <th>المبلغ</th>
                  <th>{canDecide ? 'القرار' : ''}</th>
                </tr>
              </thead>
              <tbody>
                {pending.map((p) => (
                  <tr key={p.id}>
                    <td>
                      <div className="strong">{p.nameAr}</div>
                      {onOpenAuction && (
                        <button className="ghost small" onClick={() => onOpenAuction(p.id)}>
                          فتح المزاد
                        </button>
                      )}
                    </td>
                    <td>{p.candidateId ? bidder(p.candidateId)?.nameAr ?? '…' : '—'}</td>
                    <td className="num">{p.amountMinorUnits != null ? sar(p.amountMinorUnits, 'ar') : '—'}</td>
                    <td>
                      {canDecide ? (
                        <div className="decision-cell">
                          <button
                            className="primary"
                            disabled={busy || !p.candidateId}
                            onClick={() =>
                              void decide(() =>
                                admin.post(`/auctions/${p.id}/award`, { committeeUserId }),
                              )
                            }
                          >
                            تأكيد الترسية
                          </button>
                          <input
                            placeholder="سبب رفض النتيجة"
                            aria-label={`سبب رفض نتيجة ${p.nameAr}`}
                            value={reasons[p.id] ?? ''}
                            onChange={(e) => setReasons({ ...reasons, [p.id]: e.target.value })}
                          />
                          <button
                            className="danger"
                            disabled={busy || !(reasons[p.id] ?? '').trim()}
                            onClick={() => {
                              if (!window.confirm('رفض النتيجة يجعل المزاد غير مُرسى ولا ينتقل للمزايد التالي. متابعة؟'))
                                return
                              void decide(() =>
                                admin.post(`/auctions/${p.id}/result/reject`, {
                                  reason: (reasons[p.id] ?? '').trim(),
                                }),
                              )
                            }}
                          >
                            رفض النتيجة
                          </button>
                        </div>
                      ) : (
                        <span className="muted small">قرار لجنة الترسية</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      <div className="card">
        <div className="section-head">
          <h2>الترسيات قيد المتابعة</h2>
          {awards && <span className="pill teal plain">{awards.length}</span>}
        </div>
        <p className="lede">الترسيات غير المسددة، أو المسددة التي لم يكتمل إفراغها.</p>

        {awards?.length === 0 && <p className="muted small">لا توجد ترسيات قيد المتابعة.</p>}

        {awards && awards.length > 0 && (
          <div className="settlement-list">
            {awards.map((e) => {
              const a = e.award
              const t = transferAr[a.transferStatus]
              const isOpen = open === e.auctionId
              return (
                <FollowUpRows
                  key={e.auctionId}
                  entry={e}
                  winner={bidder(a.bidderId)?.nameAr ?? '…'}
                  transfer={t}
                  expanded={isOpen}
                  onToggle={() => setOpen(isOpen ? null : e.auctionId)}
                  canRecord={canRecord}
                  canDecide={canDecide}
                  busy={busy}
                  onAct={act}
                  onDecide={decide}
                  admin={admin}
                  documents={documents}
                />
              )
            })}
          </div>
        )}
      </div>

      {!committee && (
        <>
      <div className="card">
        <div className="section-head">
          <h2>التأمينات غير المسوّاة</h2>
          {deposits && <span className="pill teal plain">{deposits.length}</span>}
        </div>
        <p className="lede">
          تأمينات حُسم مصيرها بعد الترسية أو عدم البيع ولم يُسجَّل بعد ردّها أو تحرير ضمانها
          أو مصادرتها. الإغلاق يتطلب رقم مرجع (مرجع الرد في بوابة الدفع، أو رقم خطاب البنك).
        </p>

        {deposits?.length === 0 && <p className="muted small">لا توجد تأمينات معلّقة.</p>}

        {deposits && deposits.length > 0 && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>المزاد</th>
                  <th>المشارك</th>
                  <th>طريقة التأمين</th>
                  <th>المطلوب</th>
                  <th>{canRecord ? 'الإغلاق' : ''}</th>
                </tr>
              </thead>
              <tbody>
                {deposits.map((d) => (
                  <DepositRow
                    key={`${d.auctionId}:${d.bidderId}`}
                    deposit={d}
                    auctionName={auctionName(d.auctionId)}
                    canRecord={canRecord}
                    busy={busy}
                    onClose={(reference) =>
                      act(() =>
                        participant.post(
                          `/auctions/${d.auctionId}/subscriptions/${d.bidderId}/deposit/close`,
                          { reference },
                        ),
                      )
                    }
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
        </>
      )}
    </>
  )
}

function FollowUpRows({
  entry,
  winner,
  transfer,
  expanded,
  onToggle,
  canRecord,
  canDecide,
  busy,
  onAct,
  onDecide,
  admin,
  documents,
}: {
  entry: FollowUpEntry
  winner: string
  transfer: { ar: string; tone: string }
  expanded: boolean
  onToggle: () => void
  canRecord: boolean
  canDecide: boolean
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
  onDecide: (work: () => Promise<unknown>) => Promise<void>
  admin: ReturnType<typeof api>
  documents: ReturnType<typeof api>
}) {
  const a = entry.award
  const settled = entry.status === 'Settled'
  const base = `/auctions/${entry.auctionId}/award`

  const [amount, setAmount] = useState('')
  const [paidOn, setPaidOn] = useState(() => new Date().toISOString().slice(0, 10))
  const [reference, setReference] = useState('')
  const [depositRef, setDepositRef] = useState('')
  const [transferRef, setTransferRef] = useState(a.transferReference ?? '')
  const file = useRef<HTMLInputElement>(null)
  const [scan, setScan] = useState<File | null>(null)

  const hasCredit = a.receipts.some((r) => r.kind === 'DepositCredit')

  const record = () =>
    onAct(async () => {
      const minor = parseRiyals(amount)
      if (minor === null) throw new Error('أدخل مبلغاً صحيحاً.')
      let documentId: string | null = null
      if (scan) {
        // Private: a citizen's payment receipt is not for the catalogue.
        documentId = (await documents.upload<{ id: string }>('/documents', scan, { access: 'Private' })).id
      }
      await admin.post(`${base}/receipts`, {
        amountMinorUnits: minor,
        paidOn: new Date(paidOn).toISOString(),
        reference: reference.trim(),
        documentId,
      })
      setAmount('')
      setReference('')
      setScan(null)
    })

  return (
    <>
      <section className={`settlement-panel${a.overdue ? ' overdue' : ''}`} data-testid="settlement-panel">
        <div className="panel-title">
          <div>
            <h2>ترسية {entry.nameAr}</h2>
            <p className="muted small">
              الفائز: {winner}
              {entry.phase && <> · {entry.phase}</>}
            </p>
          </div>
          <div className="inline-actions">
            {a.overdue && <span className="pill bad">متعثر — للمراجعة</span>}
            <span className={`pill ${t2(settled, transfer)}`}>
              {settled ? stageLabel('Settled').ar : a.transferStatus === 'Completed' ? 'اكتمل الإفراغ' : 'قيد التسوية'}
            </span>
          </div>
        </div>

        <div className="stats mini-stats">
          <div className="stat-cell">
            <div className="stat-head">قيمة الترسية</div>
            <div className="stat-figure num">{sar(a.amountMinorUnits, 'ar')}</div>
          </div>
          <div className="stat-cell">
            <div className="stat-head">المسدَّد</div>
            <div className="stat-figure num">{sar(a.paidMinorUnits, 'ar')}</div>
          </div>
          <div className="stat-cell">
            <div className="stat-head">المتبقي</div>
            <div className="stat-figure num">{sar(a.remainingMinorUnits, 'ar')}</div>
          </div>
          <div className="stat-cell">
            <div className="stat-head">مهلة السداد</div>
            <div className="stat-figure small-figure">{settled ? '—' : when(a.complianceDeadline)}</div>
          </div>
        </div>

        <div className="inline-actions">
          <button className={expanded ? 'small' : 'primary small'} onClick={onToggle} aria-expanded={expanded}>
            {expanded ? 'إخفاء التفاصيل' : canRecord ? 'تسجيل سداد والإفراغ' : 'الإيصالات والإفراغ'}
          </button>
          <span className={`pill ${transfer.tone}`}>الإفراغ: {transfer.ar}</span>
          {/* «اعتماد التسوية»: the committee's, once the price is paid in full and
              the winner notified — the same rule as on the auction page. */}
          {canDecide && entry.status === 'Awarded' && (
            <button
              className="primary small"
              disabled={busy || a.remainingMinorUnits > 0 || !a.winnerNotifiedAt}
              title={
                a.remainingMinorUnits > 0
                  ? `المتبقي ${sar(a.remainingMinorUnits, 'ar')}`
                  : !a.winnerNotifiedAt
                    ? 'لم يُشعَر الفائز بعد'
                    : undefined
              }
              onClick={() => void onDecide(() => admin.post(`/auctions/${entry.auctionId}/settle`))}
            >
              اعتماد التسوية
            </button>
          )}
        </div>
      {expanded && (
            <div className="followup-detail" style={{ marginTop: 16 }}>
              <section>
                <h3>الإيصالات</h3>
                {a.receipts.length === 0 ? (
                  <p className="muted small">لم يُسجَّل أي سداد.</p>
                ) : (
                  <ul className="doc-list">
                    {a.receipts.map((r) => (
                      <li key={r.id}>
                        <span>
                          {r.kind === 'DepositCredit' ? 'احتساب التأمين' : 'سداد'} —{' '}
                          <span className="num">{sar(r.amountMinorUnits, 'ar')}</span>
                          <span className="muted small"> · {when(r.paidOn)}</span>
                        </span>
                        <span className="mono small">{r.reference}</span>
                      </li>
                    ))}
                  </ul>
                )}

                {canRecord && !settled && a.remainingMinorUnits > 0 && (
                  <>
                    <div className="grid" style={{ marginTop: 12 }}>
                      <label>
                        <span>المبلغ (ر.س) — المتبقي {riyals(a.remainingMinorUnits)}</span>
                        <input className="ltr num" inputMode="decimal" value={amount}
                          onChange={(e) => setAmount(e.target.value)} />
                      </label>
                      <label>
                        <span>تاريخ السداد</span>
                        <input type="date" className="ltr" value={paidOn}
                          onChange={(e) => setPaidOn(e.target.value)} />
                        {paidOn && <span className="muted small">{day(paidOn)}</span>}
                      </label>
                      <label>
                        <span>رقم الإيصال / المرجع</span>
                        <input className="ltr" value={reference} placeholder="SADAD / حوالة"
                          onChange={(e) => setReference(e.target.value)} />
                      </label>
                    </div>
                    <div className="row">
                      <input ref={file} type="file" accept="application/pdf,image/*"
                        aria-label="صورة الإيصال"
                        style={{ position: 'absolute', width: 1, height: 1, opacity: 0 }}
                        onChange={(e) => setScan(e.target.files?.[0] ?? null)} />
                      <button disabled={busy} onClick={() => file.current?.click()}>
                        {scan ? `📎 ${scan.name}` : 'إرفاق صورة الإيصال (اختياري)'}
                      </button>
                      <button className="primary"
                        disabled={busy || amount.trim() === '' || reference.trim() === ''}
                        onClick={() => void record()}>
                        تسجيل السداد
                      </button>
                    </div>

                    {!hasCredit && (
                      <div className="row" style={{ marginTop: 10 }}>
                        <input className="ltr" value={depositRef} style={{ flex: '1 1 220px' }}
                          placeholder="مرجع دفع التأمين (من بوابة الدفع)"
                          aria-label="مرجع التأمين"
                          onChange={(e) => setDepositRef(e.target.value)} />
                        <button disabled={busy || depositRef.trim() === ''}
                          onClick={() => void onAct(() =>
                            admin.post(`${base}/deposit-credit`, { reference: depositRef.trim() }))}>
                          احتساب التأمين المدفوع من الثمن
                        </button>
                      </div>
                    )}
                  </>
                )}
              </section>

              <section>
                <h3>الإفراغ</h3>
                <p className="small">
                  الحالة: <span className={`pill ${transfer.tone}`}>{transfer.ar}</span>
                  {a.transferReference && (
                    <> · الصك/المرجع <span className="mono">{a.transferReference}</span></>
                  )}
                  {a.transferCompletedAt && <> · {when(a.transferCompletedAt)}</>}
                </p>

                {canRecord && a.transferStatus !== 'Completed' && (
                  <div className="row">
                    {a.transferStatus === 'NotStarted' && (
                      <button disabled={busy}
                        onClick={() => void onAct(() =>
                          admin.post(`${base}/transfer`, { status: 'InProgress' }))}>
                        بدء الإفراغ
                      </button>
                    )}
                    <input className="ltr" value={transferRef} style={{ flex: '1 1 220px' }}
                      placeholder="رقم الصك الجديد" aria-label="رقم الصك الجديد"
                      onChange={(e) => setTransferRef(e.target.value)} />
                    <button className="primary"
                      disabled={busy || transferRef.trim() === '' || a.remainingMinorUnits > 0}
                      title={a.remainingMinorUnits > 0 ? 'يكتمل الإفراغ بعد سداد كامل المبلغ' : undefined}
                      onClick={() => void onAct(() =>
                        admin.post(`${base}/transfer`, {
                          status: 'Completed',
                          reference: transferRef.trim(),
                        }))}>
                      إكمال الإفراغ
                    </button>
                  </div>
                )}
                {!settled && a.remainingMinorUnits === 0 && (
                  <p className="muted small" style={{ marginTop: 10 }}>
                    سُدِّد المبلغ كاملاً — تعتمد لجنة الترسية التسوية من صفحة المزاد.
                  </p>
                )}
              </section>
            </div>
      )}
      </section>
    </>
  )
}

/** The panel badge's tone: settled, transferred, or still being followed up. */
function t2(settled: boolean, transfer: { tone: string }): string {
  return settled ? 'done' : transfer.tone === 'live' ? 'live' : 'wait'
}

function DepositRow({
  deposit,
  auctionName,
  canRecord,
  busy,
  onClose,
}: {
  deposit: Deposit
  auctionName: string
  canRecord: boolean
  busy: boolean
  onClose: (reference: string) => Promise<void>
}) {
  const [reference, setReference] = useState('')
  return (
    <tr>
      <td className="small">{auctionName}</td>
      <td className="strong">{deposit.nameAr}</td>
      <td className="small">
        {deposit.depositMethod === 'BankGuarantee' ? 'ضمان بنكي' : 'دفع إلكتروني'}
        {deposit.depositPaymentRef && (
          <div className="mono muted">{deposit.depositPaymentRef}</div>
        )}
      </td>
      <td>
        <span className={`pill ${deposit.depositSettlement === 'ToForfeit' ? 'bad' : 'wait'}`}>
          {depositAr[deposit.depositSettlement]}
        </span>
      </td>
      <td>
        {canRecord && (
          <div className="row">
            <input className="ltr" value={reference} style={{ width: 180 }}
              placeholder="رقم المرجع" aria-label="مرجع الإغلاق"
              onChange={(e) => setReference(e.target.value)} />
            <button disabled={busy || reference.trim() === ''}
              onClick={() => void onClose(reference.trim())}>
              إغلاق
            </button>
          </div>
        )}
      </td>
    </tr>
  )
}

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.problems.length > 0) return e.problems.join(' · ')
    if (e.status === 403) return 'لا تملك صلاحية هذا الإجراء.'
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}
