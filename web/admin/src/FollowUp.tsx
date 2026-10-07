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
}

export function FollowUp({ session, canRecord }: Props) {
  const admin = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const documents = useMemo(() => api({ baseUrl: config.documentsApi, session }), [session])

  const [awards, setAwards] = useState<FollowUpEntry[] | null>(null)
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

  // The award carries the winner's id; the name is the participant service's,
  // through its staff lookup — the committee and operators read this page too.
  const bidder = useBidders(session, awards?.map((a) => a.award.bidderId) ?? [])

  const auctionName = (id: string) =>
    awards?.find((a) => a.auctionId === id)?.nameAr ?? id.slice(0, 8)

  const overdue = awards?.filter((a) => a.award.overdue).length ?? 0

  return (
    <>
      <div className="page-head">
        <div>
          <h2>متابعة الترسية</h2>
          <p>
            سداد ثمن الترسية والإفراغ ورد التأمينات تتم خارج المنصة؛ تُسجَّل هنا يدوياً
            مقابل مرجع يمكن تتبّعه.
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

      <div className="card">
        <div className="section-head">
          <h2>الترسيات قيد المتابعة</h2>
          {awards && <span className="pill teal plain">{awards.length}</span>}
        </div>
        <p className="lede">الترسيات غير المسددة، أو المسددة التي لم يكتمل إفراغها.</p>

        {awards?.length === 0 && <p className="muted small">لا توجد ترسيات قيد المتابعة.</p>}

        {awards && awards.length > 0 && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>المزاد</th>
                  <th>الفائز</th>
                  <th>مبلغ الترسية</th>
                  <th>المسدَّد</th>
                  <th>المتبقي</th>
                  <th>مهلة السداد</th>
                  <th>الإفراغ</th>
                  <th />
                </tr>
              </thead>
              <tbody>
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
                      busy={busy}
                      onAct={act}
                      admin={admin}
                      documents={documents}
                    />
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

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
  )
}

function FollowUpRows({
  entry,
  winner,
  transfer,
  expanded,
  onToggle,
  canRecord,
  busy,
  onAct,
  admin,
  documents,
}: {
  entry: FollowUpEntry
  winner: string
  transfer: { ar: string; tone: string }
  expanded: boolean
  onToggle: () => void
  canRecord: boolean
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
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
      <tr className={a.overdue ? 'row-alert' : undefined}>
        <td>
          <div className="strong">{entry.nameAr}</div>
          {entry.phase && <div className="muted small">{entry.phase}</div>}
        </td>
        <td>{winner}</td>
        <td><span className="num">{sar(a.amountMinorUnits, 'ar')}</span></td>
        <td><span className="num">{sar(a.paidMinorUnits, 'ar')}</span></td>
        <td>
          <span className="num strong">{sar(a.remainingMinorUnits, 'ar')}</span>
        </td>
        <td className="small">
          {settled ? (
            <span className="pill done">{stageLabel('Settled').ar}</span>
          ) : a.overdue ? (
            <span className="pill bad">متعثر — للمراجعة</span>
          ) : (
            when(a.complianceDeadline)
          )}
        </td>
        <td><span className={`pill ${transfer.tone}`}>{transfer.ar}</span></td>
        <td>
          <button className="ghost" onClick={onToggle} aria-expanded={expanded}>
            {expanded ? 'إخفاء' : 'التفاصيل'}
          </button>
        </td>
      </tr>

      {expanded && (
        <tr className="detail-row">
          <td colSpan={8}>
            <div className="followup-detail">
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
          </td>
        </tr>
      )}
    </>
  )
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
