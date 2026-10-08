import { useEffect, useMemo, useState } from 'react'
import { ApiError, PAGE_SIZE, PageHead, Pager, api, config, sar, timestamp, usePage, type Session } from '@eauction/shared'
import { useBidders } from './winners'

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi

/**
 * Text from the trail with every bidder id in it replaced by the bidder's name,
 * where the id is a bidder's. An id that is not one — a document, a receipt —
 * is left as it is.
 */
export function WithNames({ session, text }: { session: Session; text: string | null }) {
  const ids = useMemo(() => (text ? [...new Set(text.match(GUID) ?? [])] : []), [text])
  const bidder = useBidders(session, ids)
  if (!text) return <>—</>
  const parts = text.split(GUID)
  const found = text.match(GUID) ?? []
  return (
    <>
      {parts.map((p, i) => (
        <span key={i}>
          {p}
          {found[i] && (bidder(found[i])?.nameAr ? <strong title={found[i]}>{bidder(found[i])!.nameAr}</strong> : <code className="small">{found[i].slice(0, 8)}…</code>)}
        </span>
      ))}
    </>
  )
}

/** Auction names, for the dropdown and the tables. Any member of staff may list auctions. */
export function useAuctionNames(session: Session) {
  const [list, setList] = useState<Array<{ id: string; nameAr: string; status: string }>>([])
  useEffect(() => {
    api({ baseUrl: config.adminApi, session })
      .get<{ items: Array<{ id: string; nameAr: string; status: string }> }>('/auctions?take=200')
      .then((r) => setList(r.items))
      .catch(() => undefined)
  }, [session])
  const byId = useMemo(() => new Map(list.map((a) => [a.id, a.nameAr])), [list])
  return { list, nameOf: (id: string | null | undefined) => (id ? byId.get(id) ?? null : null) }
}

// --- أحداث النظام ------------------------------------------------------------

interface SystemEvent {
  id: number
  kind: 'EligibilityGranted' | 'EligibilityWithdrawn' | 'Payment'
  auctionId: string | null
  bidderId: string | null
  purpose: string | null
  outcome: string | null
  amountMinorUnits: number | null
  reference: string | null
  reason: string | null
  at: string
}

const purposeAr: Record<string, string> = { Booklet: 'كراسة الشروط', Deposit: 'التأمين', Brokerage: 'السعي' }
const outcomeAr: Record<string, { ar: string; tone: string }> = {
  Charged: { ar: 'سداد', tone: 'live' },
  Refused: { ar: 'رفض الدفع', tone: 'bad' },
  Refunded: { ar: 'استرداد', tone: 'wait' },
  Forfeited: { ar: 'مصادرة', tone: 'bad' },
  AppliedToPurchase: { ar: 'احتُسب من الثمن', tone: 'done' },
}

function describeEvent(e: SystemEvent): { ar: string; tone: string } {
  if (e.kind === 'EligibilityGranted') return { ar: 'أصبح مؤهّلاً للمزايدة', tone: 'live' }
  if (e.kind === 'EligibilityWithdrawn') return { ar: 'سُحبت الأهلية', tone: 'bad' }
  const o = outcomeAr[e.outcome ?? ''] ?? { ar: e.outcome ?? 'دفعة', tone: 'done' }
  return { ar: `${o.ar} — ${purposeAr[e.purpose ?? ''] ?? e.purpose ?? ''}`, tone: o.tone }
}

const PAGE = 25

/**
 * أحداث النظام — what the platform did by itself, which no member of staff did and
 * so the staff trail cannot show: a bidder qualifying when their deposit settled,
 * a booklet paid for, a refund, a forfeit. The actor is «النظام».
 */
export function SystemEvents({ session }: { session: Session }) {
  const client = useMemo(() => api({ baseUrl: config.auditApi, session }), [session])
  const { list, nameOf } = useAuctionNames(session)
  const [auctionId, setAuctionId] = useState('')
  const [kind, setKind] = useState('')
  const [index, setIndex] = useState(0)
  const [page, setPage] = useState<{ total: number; items: SystemEvent[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const bidder = useBidders(session, page?.items.map((e) => e.bidderId) ?? [])

  useEffect(() => setIndex(0), [auctionId, kind])
  useEffect(() => {
    const qs = new URLSearchParams({ take: String(PAGE), skip: String(index * PAGE) })
    if (auctionId) qs.set('auctionId', auctionId)
    if (kind) qs.set('kind', kind)
    client
      .get<{ total: number; items: SystemEvent[] }>(`/audit/system?${qs}`)
      .then((p) => {
        setPage(p)
        setError(null)
      })
      .catch((e) => setError(e instanceof ApiError ? `تعذّر التحميل (${e.status}).` : String(e)))
  }, [client, auctionId, kind, index])

  const pages = Math.max(1, Math.ceil((page?.total ?? 0) / PAGE))

  return (
    <div className="card">
      <h2>أحداث النظام</h2>
      <p className="lede">
        ما تغيّر دون إجراء من موظف: تأهّل مزايد بعد سداد التأمين، مدفوعات الكراسة والتأمين، الاسترداد
        والمصادرة. المنفّذ فيها «النظام»، ومصدرها سجلات خدمات المشاركين والمدفوعات.
      </p>
      <div className="row" style={{ gap: 8, flexWrap: 'wrap', marginBottom: 12 }}>
        <select value={auctionId} onChange={(e) => setAuctionId(e.target.value)}>
          <option value="">كل المزادات</option>
          {list.map((a) => (
            <option key={a.id} value={a.id}>{a.nameAr}</option>
          ))}
        </select>
        <select value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="">كل الأحداث</option>
          <option value="EligibilityGranted">التأهّل</option>
          <option value="EligibilityWithdrawn">سحب الأهلية</option>
          <option value="Payment">المدفوعات</option>
        </select>
      </div>
      {error && <div className="notice error">{error}</div>}
      {page && page.items.length === 0 && <p className="muted">لا توجد أحداث.</p>}
      {page && page.items.length > 0 && (
        <>
          <div className="table-scroll">
            <table data-testid="system-events">
              <thead>
                <tr>
                  <th>التاريخ</th>
                  <th>الحدث</th>
                  <th>المزاد</th>
                  <th>المزايد</th>
                  <th>المبلغ</th>
                  <th>المرجع / السبب</th>
                  <th>المنفّذ</th>
                </tr>
              </thead>
              <tbody>
                {page.items.map((e) => {
                  const d = describeEvent(e)
                  return (
                    <tr key={e.id}>
                      <td className="small muted">{timestamp(e.at)}</td>
                      <td><span className={`pill ${d.tone}`}>{d.ar}</span></td>
                      <td className="small">{nameOf(e.auctionId) ?? '—'}</td>
                      <td className="small">{bidder(e.bidderId)?.nameAr ?? (e.bidderId ? `${e.bidderId.slice(0, 8)}…` : '—')}</td>
                      <td className="num small">{e.amountMinorUnits != null && e.kind === 'Payment' ? sar(e.amountMinorUnits, 'ar') : '—'}</td>
                      <td className="small ltr">{e.reason ?? e.reference ?? '—'}</td>
                      <td className="small muted">النظام</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <div className="pager">
            <span className="muted small">
              <span className="num">{page.total}</span> حدث
            </span>
            <span className="grow" />
            <button className="ghost" disabled={index === 0} onClick={() => setIndex(index - 1)}>السابق</button>
            <span className="small">صفحة <span className="num">{index + 1}</span> من <span className="num">{pages}</span></span>
            <button className="ghost" disabled={index >= pages - 1} onClick={() => setIndex(index + 1)}>التالي</button>
          </div>
        </>
      )}
    </div>
  )
}

// --- سجل المزايدات -----------------------------------------------------------

interface BidRow {
  offset: number
  at: string
  bidderId: string
  amountMinorUnits: number
  channel: 'Online' | 'Onsite'
  enteredBy: string | null
  accepted: boolean
  rejectionReason: string | null
}

interface BidHistoryPage {
  total: number
  recorded: number
  read: number
  acceptedCount: number
  rejectedCount: number
  leader: BidRow | null
  items: BidRow[]
}

const rejectionAr: Record<string, string> = {
  BelowMinimumIncrement: 'أقل من الحد الأدنى للزيادة',
  BelowOpeningPrice: 'أقل من سعر الافتتاح',
  SelfOutbid: 'المزايد متصدّر أصلاً',
  DuplicateBidId: 'مزايدة مكررة',
  AuctionClosed: 'بعد إغلاق المزاد',
  OutsideWindow: 'خارج وقت المزاد',
  NotEligible: 'غير مؤهّل',
  NotTheClerk: 'ليس موظف القاعة',
  RateLimited: 'تجاوز حد المحاولات',
  BadSignature: 'توقيع غير صالح',
  MalformedFrame: 'بيانات تالفة',
  UnknownAuction: 'مزاد غير معروف',
}

/** Award-stage actions, to show beside the bids: how the result became an award. */
const AWARD_ACTIONS: Record<string, string> = {
  CloseAuction: 'إغلاق المزاد (القاعة)',
  ExtendAuction: 'تمديد المزاد',
  RejectPreliminaryResult: 'رفض النتيجة الأولية',
  ConfirmAward: 'تأكيد الترسية',
  DisqualifyWinner: 'إلغاء ترسية الفائز',
  ReferToNextBidder: 'إحالة إلى المزايد التالي',
  MarkAuctionUnsold: 'إعلان عدم البيع',
  GenerateAwardLetter: 'إصدار خطاب الترسية',
  UploadSignedAwardLetter: 'رفع خطاب موقّع',
  NotifyWinner: 'إشعار الفائز',
  RecordAwardPayment: 'تسجيل دفعة',
  CreditDepositToAward: 'احتساب التأمين',
  SettleAuction: 'اعتماد التسوية',
  UpdateTransfer: 'تحديث الإفراغ',
  CancelAuction: 'إلغاء المزاد',
}

/**
 * سجل المزايدات — one auction's bids in the order they were recorded, each with the
 * processor's verdict, then the decisions that turned the result into an award.
 * Read-only by construction: the bid log is append-only and this only reads it.
 */
export function BidHistory({
  session,
  auctionId,
  withDecisions = true,
  preview,
  onOpenFull,
}: {
  session: Session
  auctionId: string
  withDecisions?: boolean
  /**
   * In a dialog: only the latest few bids, and — when there are more — a button
   * that opens the whole log on its own screen, rather than a dialog that scrolls
   * for ever.
   */
  preview?: number
  onOpenFull?: () => void
}) {
  const audit = useMemo(() => api({ baseUrl: config.auditApi, session }), [session])
  const [page, setPage] = useState<BidHistoryPage | null>(null)
  const paging = usePage(preview ?? PAGE_SIZE)
  const [decisions, setDecisions] = useState<Array<{ offset: number; at: string; action: string; details: string | null; payload?: string }> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const bidder = useBidders(session, page?.items.map((b) => b.bidderId) ?? [])

  useEffect(() => {
    setPage(null)
    setError(null)
    audit
      .get<BidHistoryPage>(`/audit/auctions/${auctionId}/bids?${paging.query}`)
      .then(setPage)
      .catch((e) => setError(e instanceof ApiError ? `تعذّر تحميل سجل المزايدات (${e.status}).` : String(e)))
    if (withDecisions)
      audit
        .get<{ items: Array<{ offset: number; at: string; action: string; details: string | null; payload?: string }> }>(
          `/audit?subject=${encodeURIComponent(`auction/${auctionId}`)}&take=200`,
        )
        .then((r) => setDecisions(r.items.filter((x) => AWARD_ACTIONS[x.action]).reverse()))
        .catch(() => setDecisions(null))
  }, [audit, auctionId, withDecisions, paging.query])

  const nameOf = (id: string) => bidder(id)?.nameAr ?? `${id.slice(0, 8)}…`
  const actorOf = (payload?: string) => {
    try {
      return (JSON.parse(payload ?? '{}') as { actorName?: string }).actorName ?? null
    } catch {
      return null
    }
  }

  return (
    <div data-testid="bid-history">
      {error && <div className="notice error">{error}</div>}
      {!page && !error && <p className="muted">…</p>}
      {page && (
        <>
          <div className="stat-grid compact" style={{ marginBottom: 12 }}>
            <div className="stat">
              <div className="stat-label">المزايدات المسجّلة</div>
              <div className="stat-value num">{page.read}</div>
            </div>
            <div className="stat">
              <div className="stat-label">المقبولة</div>
              <div className="stat-value num">{page.acceptedCount}</div>
            </div>
            <div className="stat">
              <div className="stat-label">المرفوضة</div>
              <div className="stat-value num">{page.rejectedCount}</div>
            </div>
            {page.leader && (
              <div className="stat highlight">
                <div className="stat-label">آخر مزايدة مقبولة</div>
                <div className="stat-value num">{sar(page.leader.amountMinorUnits, 'ar')}</div>
                <div className="stat-sub">{nameOf(page.leader.bidderId)}</div>
              </div>
            )}
          </div>
          {page.read < page.recorded && (
            <div className="notice info small">قُرئت {page.read} من {page.recorded} مزايدة — أعد التحميل للباقي.</div>
          )}
          {page.items.length === 0 ? (
            <p className="muted">لا مزايدات في هذا المزاد.</p>
          ) : (
            <div className="table-scroll">
              <table data-testid="bid-history-table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>الوقت</th>
                    <th>المزايد</th>
                    <th>المبلغ</th>
                    <th>القناة</th>
                    <th>النتيجة</th>
                  </tr>
                </thead>
                <tbody>
                  {page.items.map((b) => (
                    <tr key={b.offset} className={page.leader?.offset === b.offset ? 'row-lead' : undefined}>
                      <td className="num mono">{b.offset + 1}</td>
                      <td className="small muted">{timestamp(b.at)}</td>
                      <td className="small">{nameOf(b.bidderId)}</td>
                      <td className="num">{sar(b.amountMinorUnits, 'ar')}</td>
                      <td className="small">{b.channel === 'Onsite' ? 'القاعة (موظف)' : 'إلكتروني'}</td>
                      <td>
                        {b.accepted ? (
                          <span className="pill live">مقبولة</span>
                        ) : (
                          <span className="pill bad" title={b.rejectionReason ?? ''}>
                            مرفوضة — {rejectionAr[b.rejectionReason ?? ''] ?? b.rejectionReason}
                          </span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {preview ? (
            page.total > preview &&
            onOpenFull && (
              <div className="row" style={{ justifyContent: 'center', marginTop: 12 }}>
                <button onClick={onOpenFull}>
                  عرض السجل كاملاً (<span className="num">{page.total}</span> مزايدة)
                </button>
              </div>
            )
          ) : (
            <Pager page={paging.page} total={page.total} noun="مزايدة" onPage={paging.setPage} />
          )}
        </>
      )}

      {withDecisions && decisions && decisions.length > 0 && (
        <>
          <h3>من النتيجة إلى الترسية</h3>
          <ol className="decision-list">
            {decisions.map((d) => (
              <li key={d.offset}>
                <div>
                  <strong>{AWARD_ACTIONS[d.action]}</strong>
                  <span className="muted small"> · {timestamp(d.at)} · {actorOf(d.payload) ?? 'موظف'}</span>
                </div>
                {d.details && (
                  <div className="small">
                    <WithNames session={session} text={d.details} />
                  </div>
                )}
              </li>
            ))}
          </ol>
        </>
      )}
    </div>
  )
}

/** The auditor's tab: pick an auction, read its bids and decisions. */
export function BidHistoryScreen({ session }: { session: Session }) {
  const { list } = useAuctionNames(session)
  // Where the bids are: running, awaiting the committee, awarded — then the rest.
  const order = ['Live', 'PendingAward', 'Awarded', 'Settled', 'WinnerDisqualified', 'Unsold']
  const rank = (s: string) => (order.indexOf(s) === -1 ? order.length : order.indexOf(s))
  const opened = list
    .filter((a) => !['Draft', 'PendingReview', 'Rejected', 'Approved'].includes(a.status))
    .sort((x, y) => rank(x.status) - rank(y.status))
  const [auctionId, setAuctionId] = useState('')
  useEffect(() => {
    if (!auctionId && opened.length > 0) setAuctionId(opened[0]!.id)
  }, [auctionId, opened])

  return (
    <div className="card">
      <h2>سجل المزايدات</h2>
      <p className="lede">
        تسلسل المزايدات كما سُجّلت — الوقت والمزايد والمبلغ وقرار المعالج — ثم قرارات اللجنة حتى
        الترسية. للاطلاع فقط: لا يمكن لأي صلاحية تعديل مزايدة مسجّلة.
      </p>
      <select value={auctionId} onChange={(e) => setAuctionId(e.target.value)} style={{ marginBottom: 12 }}>
        {opened.map((a) => (
          <option key={a.id} value={a.id}>{a.nameAr}</option>
        ))}
      </select>
      {auctionId && <BidHistory session={session} auctionId={auctionId} />}
    </div>
  )
}

/**
 * سجل العروض on a screen of its own: the whole log of one auction, paged by the
 * audit service — what the dialog's «عرض السجل كاملاً» opens.
 */
export function BidLogPage({
  session,
  auctionId,
  nameAr,
  onBack,
}: {
  session: Session
  auctionId: string
  nameAr: string | null
  onBack: () => void
}) {
  return (
    <>
      <PageHead
        eyebrow="سجل العروض"
        title={nameAr ?? 'سجل المزايدات'}
        sub="كل مزايدة سُجّلت على هذا المزاد، الأحدث أولاً، مع نتيجتها."
        action={<button onClick={onBack}>رجوع</button>}
      />
      <div className="card">
        <BidHistory session={session} auctionId={auctionId} withDecisions={false} />
      </div>
    </>
  )
}
