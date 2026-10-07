import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, api, config, timestamp as when, type Session } from '@eauction/shared'
import { BidHistoryScreen, SystemEvents, WithNames } from './AuditViews'

/**
 * سجل المراجعة — the staff audit trail (§34).
 *
 * The screen a government stakeholder asks for without using the word "audit": who
 * approved this, who changed the reserve, who accepted that bank guarantee, and how
 * do we know the record has not been edited since.
 *
 * The last part is why the تحقّق button is here and not buried in an API. The whole
 * claim of §34 is that the trail is tamper-evident rather than merely stored, and a
 * claim nobody can check on a screen is a claim nobody believes.
 */

interface Entry {
  offset: number
  actorSubject: string | null
  actorRoles: string | null
  action: string | null
  subject: string | null
  details: string | null
  sourceAddress: string | null
  at: string | null
  malformed: boolean
  hash: string
  /** The event as published; newer entries carry who acted and on what, by name. */
  payload?: string
}

/** The names the recording service put beside the IDs, when it did. */
function namesOf(e: Entry): { actor: string | null; subject: string | null } {
  try {
    const p = JSON.parse(e.payload ?? '{}') as { actorName?: string; subjectLabel?: string }
    return { actor: p.actorName || null, subject: p.subjectLabel || null }
  } catch {
    return { actor: null, subject: null }
  }
}

interface Verdict {
  intact: boolean
  checkedEntries: number
  head: string
  brokeAt: number | null
  broke: string | null
  gaps: Array<{ after: number; before: number }>
  storedThrough: number
  topicEnd: number
  missingTail: number
}

/** What each action is called, for a reader who does not know the endpoints. */
const ACTIONS: Record<string, string> = {
  CreateAuctionDraft: 'إنشاء مزاد',
  UpdateAuctionDetails: 'تعديل بيانات المزاد',
  AddPlot: 'إضافة قطعة',
  RemovePlot: 'إزالة قطعة',
  AttachBooklet: 'إرفاق كراسة الشروط',
  AttachCoverImage: 'إرفاق صورة',
  SubmitAuctionForReview: 'رفع للمراجعة',
  ApproveAuction: 'اعتماد المزاد',
  RejectAuction: 'رفض المزاد',
  ChangeAuctionLifecycle: 'تغيير حالة المزاد',
  AssignClerk: 'تعيين مُدخل القاعة',
  UnassignClerk: 'إلغاء تعيين مُدخل القاعة',
  ReadClerkSigningKey: 'استلام مفتاح التوقيع',
  ExtendAuction: 'تمديد المزاد',
  CloseAuction: 'إغلاق المزاد',
  OfferAwardCandidate: 'عرض مرشّح للترسية',
  ConfirmAward: 'تأكيد الترسية',
  GenerateAwardLetter: 'إصدار خطاب الترسية',
  UploadSignedAwardLetter: 'رفع خطاب موقّع',
  NotifyWinner: 'إشعار الفائز',
  DisqualifyWinner: 'إلغاء ترسية الفائز',
  MarkAuctionUnsold: 'إعلان عدم البيع',
  SettleAuction: 'إتمام البيع',
  VerifyBankGuarantee: 'قبول ضمان بنكي',
  RevokeEligibility: 'إلغاء تأهيل مزايد',
  RotateBidderKey: 'تدوير مفتاح مزايد',
  ReadDocument: 'فتح مستند',
  ReadDocumentMetadata: 'عرض بيانات مستند',
  AddPublicDocument: 'إضافة مرفق عام',
  RemovePublicDocument: 'إزالة مرفق عام',
  CancelAuction: 'إلغاء المزاد',
  RecordAwardPayment: 'تسجيل دفعة من ثمن الترسية',
  CreditDepositToAward: 'احتساب التأمين من الثمن',
  UpdateTransfer: 'تحديث حالة الإفراغ',
  RejectPreliminaryResult: 'رفض النتيجة الأولية',
  ReferToNextBidder: 'إحالة إلى المزايد التالي',
  RejectBankGuarantee: 'رفض ضمان بنكي',
  CloseDeposit: 'تسوية التأمين',
  ReplyToInquiry: 'الرد على استفسار',
  ReplyAndCloseInquiry: 'الرد على استفسار وإغلاقه',
  CloseInquiry: 'إغلاق استفسار',
  DraftClarification: 'صياغة توضيح عام',
  PublishClarification: 'اعتماد ونشر توضيح عام',
}

type Tab = 'staff' | 'system' | 'bids'

/**
 * سجل المراجعة, in three parts (الخاصية 14): what staff did (hash-chained), what
 * the platform did by itself, and each auction's bids with the award that followed.
 */
export function AuditTrail({ session }: { session: Session }) {
  const [tab, setTab] = useState<Tab>('staff')
  return (
    <>
      <div className="chips" role="tablist" aria-label="أقسام السجل" style={{ marginBottom: 12 }}>
        {(
          [
            ['staff', 'إجراءات الموظفين'],
            ['system', 'أحداث النظام'],
            ['bids', 'سجل المزايدات'],
          ] as Array<[Tab, string]>
        ).map(([key, ar]) => (
          <button
            key={key}
            role="tab"
            aria-selected={tab === key}
            className={tab === key ? 'chip on' : 'chip'}
            data-testid={`audit-tab-${key}`}
            onClick={() => setTab(key)}
          >
            {ar}
          </button>
        ))}
      </div>
      {tab === 'staff' ? (
        <StaffTrail session={session} />
      ) : tab === 'system' ? (
        <SystemEvents session={session} />
      ) : (
        <BidHistoryScreen session={session} />
      )}
    </>
  )
}

function StaffTrail({ session }: { session: Session }) {
  const client = useMemo(() => api({ baseUrl: config.auditApi, session }), [session])

  const [entries, setEntries] = useState<Entry[]>([])
  const [total, setTotal] = useState(0)
  const [action, setAction] = useState('')
  const [subject, setSubject] = useState('')
  const [actions, setActions] = useState<Array<{ action: string; count: number }>>([])
  const [verdict, setVerdict] = useState<Verdict | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Auction names for entries recorded before the payload carried a label.
  const [auctionNames, setAuctionNames] = useState<Map<string, string>>(new Map())
  useEffect(() => {
    api({ baseUrl: config.adminApi, session })
      .get<{ items: Array<{ id: string; nameAr: string }> }>('/auctions?take=200')
      .then((r) => setAuctionNames(new Map(r.items.map((a) => [a.id, a.nameAr]))))
      .catch(() => undefined)
  }, [session])
  const subjectOf = (e: Entry) => {
    const named = namesOf(e).subject
    if (named) return named
    const m = /^auction\/([0-9a-f-]+)/i.exec(e.subject ?? '')
    return m ? (auctionNames.get(m[1] ?? "") ?? null) : null
  }

  // Pages of PAGE_SIZE, newest first. Back to the first page whenever the filter
  // changes, so a narrower search never opens on an empty page 7.
  const [pageIndex, setPageIndex] = useState(0)
  useEffect(() => setPageIndex(0), [action, subject])
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))

  /** Bumped by تحديث, so a manual refresh re-runs the fetch below. */
  const [refresh, setRefresh] = useState(0)

  /**
   * Discards its own result if a filter changed while it was in flight.
   *
   * Without this, two filters applied in quick succession race, and the slower
   * response wins whichever order they were asked for: the table shows one
   * action's entries while the filter above it names another. On a screen whose
   * entire purpose is answering "who did this", entries displayed under the wrong
   * question are worse than no answer — somebody reads an entry, attributes it to
   * the wrong search, and is confidently wrong about who did what.
   *
   * The columns here never change, so unlike التقارير this needs no second
   * mechanism: the rows on screen always belong under the headings above them,
   * whichever filter fetched them.
   */
  useEffect(() => {
    let cancelled = false

    setBusy(true)
    setError(null)

    const parts = [`take=${PAGE_SIZE}`, `skip=${pageIndex * PAGE_SIZE}`]
    if (action) parts.push(`action=${encodeURIComponent(action)}`)
    if (subject) parts.push(`subject=${encodeURIComponent(subject)}`)

    client
      .get<{ total: number; items: Entry[] }>(`/audit?${parts.join('&')}`)
      .then((page) => {
        if (cancelled) return
        setEntries(page.items)
        setTotal(page.total)
        setBusy(false)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(describe(e))
        setEntries([])
        setBusy(false)
      })

    return () => {
      cancelled = true
    }
  }, [action, client, subject, refresh, pageIndex])

  useEffect(() => {
    void (async () => {
      try {
        const page = await client.get<{ items: Array<{ action: string; count: number }> }>(
          '/audit/actions',
        )
        setActions(page.items)
      } catch {
        // A missing filter is a smaller problem than a blocked trail.
      }
    })()
  }, [client])

  const verify = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      setVerdict(await client.get<Verdict>('/audit/verify'))
    } catch (e) {
      setError(describe(e))
    } finally {
      setBusy(false)
    }
  }, [client])

  return (
    <div className="card" data-testid="audit">
      <h2>سجل المراجعة</h2>
      <p className="muted small" style={{ marginTop: -8 }}>
        كل إجراء مؤثّر قام به موظّف، مكتوب في معاملة واحدة مع التغيير نفسه، ومربوط
        بسلسلة تجزئة تجعل أي تعديل قابلاً للكشف.
      </p>

      <div className="row" style={{ margin: '14px 0' }}>
        <label className="small muted">
          الإجراء{' '}
          <select
            value={action}
            data-testid="audit-action"
            onChange={(e) => setAction(e.target.value)}
          >
            <option value="">كل الإجراءات</option>
            {actions.map((a) => (
              <option key={a.action} value={a.action}>
                {(ACTIONS[a.action] ?? a.action) + ` (${a.count})`}
              </option>
            ))}
          </select>
        </label>

        <label className="small muted">
          المحلّ{' '}
          <select
            value={subject}
            data-testid="audit-subject"
            onChange={(e) => setSubject(e.target.value)}
          >
            <option value="">الكل</option>
            <option value="auction/">المزادات</option>
            <option value="subscription/">الاشتراكات</option>
            <option value="document/">المستندات</option>
          </select>
        </label>

        <span className="grow" />
        <button onClick={() => setRefresh((n) => n + 1)} disabled={busy}>
          تحديث
        </button>
        <button className="primary" data-testid="audit-verify" onClick={() => void verify()}>
          تحقّق من السلسلة
        </button>
      </div>

      {error && <div className="notice error">{error}</div>}

      {verdict && <Verification verdict={verdict} />}

      {entries.length === 0 ? (
        <p className="muted small" data-testid="audit-empty">
          لا توجد إجراءات مسجّلة بعد.
        </p>
      ) : (
        <>
          <Pager index={pageIndex} pages={pages} total={total} onGo={setPageIndex} />
          <div className="table-scroll">
            <table data-testid="audit-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>التاريخ</th>
                  <th>الإجراء</th>
                  <th>المحلّ</th>
                  <th>الموظّف</th>
                  <th>الأدوار</th>
                  <th>التفاصيل</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((e) => (
                  <tr key={e.offset}>
                    <td className="num mono">{e.offset}</td>
                    <td className="small muted">{when(e.at)}</td>
                    <td>
                      {e.malformed ? (
                        <span className="pill bad">سجل غير مقروء</span>
                      ) : (
                        (ACTIONS[e.action ?? ''] ?? e.action ?? '—')
                      )}
                    </td>
                    {/* The name the recording service stamped on the event, else the
                        auction's current name, else the raw id. The audit service
                        itself still does not know what an auction is. */}
                    <td className="small" title={e.subject ?? undefined}>
                      {subjectOf(e) ?? <span className="ltr mono">{short(e.subject)}</span>}
                    </td>
                    <td className="small" title={e.actorSubject ?? undefined}>
                      {namesOf(e).actor ?? <span className="ltr mono">{short(e.actorSubject)}</span>}
                    </td>
                    <td className="small muted">{e.actorRoles ?? '—'}</td>
                    <td className="small">
                      <WithNames session={session} text={e.details} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {pages > 1 && <Pager index={pageIndex} pages={pages} total={total} onGo={setPageIndex} />}
        </>
      )}
    </div>
  )
}

const PAGE_SIZE = 25

/** Newest first: page 1 is the latest PAGE_SIZE actions. */
function Pager({
  index,
  pages,
  total,
  onGo,
}: {
  index: number
  pages: number
  total: number
  onGo: (i: number) => void
}) {
  const from = total === 0 ? 0 : index * PAGE_SIZE + 1
  const to = Math.min(total, (index + 1) * PAGE_SIZE)
  return (
    <div className="pager" data-testid="audit-pager">
      <span className="muted small">
        <span className="num">{from}–{to}</span> من <span className="num">{total}</span> إجراء
      </span>
      <span className="grow" />
      <button className="ghost" disabled={index === 0} onClick={() => onGo(0)} aria-label="الصفحة الأولى">«</button>
      <button className="ghost" disabled={index === 0} onClick={() => onGo(index - 1)}>السابق</button>
      <span className="small">
        صفحة <span className="num">{index + 1}</span> من <span className="num">{pages}</span>
      </span>
      <button className="ghost" disabled={index >= pages - 1} onClick={() => onGo(index + 1)}>التالي</button>
      <button className="ghost" disabled={index >= pages - 1} onClick={() => onGo(pages - 1)} aria-label="الصفحة الأخيرة">»</button>
    </div>
  )
}

/**
 * The verdict, which is the point of the screen.
 *
 * Three numbers are reported separately rather than folded into one verdict,
 * because they answer different questions: whether the chain holds, whether any
 * entry is missing from the middle, and whether the service is simply behind the
 * topic. A single green tick would conflate "nothing was tampered with" and
 * "nothing is missing", and the second is the one a hash chain cannot see on its
 * own.
 */
function Verification({ verdict }: { verdict: Verdict }) {
  return (
    <div
      className={`notice ${verdict.intact ? 'ok' : 'error'}`}
      data-testid="audit-verdict"
      data-intact={verdict.intact ? 'true' : 'false'}
    >
      {verdict.intact ? (
        <>
          <strong>السلسلة سليمة.</strong> أُعيد حساب {verdict.checkedEntries} تجزئة من
          الحِمل المخزّن بجانبها، وكل حلقة تتبع سابقتها.
        </>
      ) : (
        <>
          <strong>السلسلة مكسورة.</strong>{' '}
          {verdict.brokeAt !== null && <>أول خلل عند السجل #{verdict.brokeAt} ({verdict.broke}). </>}
          {verdict.gaps[0] && (
            <>
              فجوة بين #{verdict.gaps[0].after} و #{verdict.gaps[0].before}.{' '}
            </>
          )}
        </>
      )}

      <div className="small" style={{ marginTop: 8 }}>
        <code className="ltr">{verdict.head.slice(0, 32)}…</code>
      </div>

      {verdict.missingTail > 0 && (
        <div className="small" style={{ marginTop: 8 }}>
          {verdict.missingTail} سجل على المسار لم يُكتب بعد (حتى #{verdict.storedThrough} من
          #{verdict.topicEnd}) — قد تكون الخدمة متأخّرة ثوانٍ، أو تكون النهاية
          مقطوعة.
        </div>
      )}
    </div>
  )
}

/** A guid is unreadable in a table. The first segment is enough to match two rows. */
function short(value: string | null): string {
  if (!value) return '—'
  const slash = value.indexOf('/')
  const id = slash >= 0 ? value.slice(slash + 1) : value
  const prefix = slash >= 0 ? value.slice(0, slash + 1) : ''
  return prefix + (id.length > 12 ? id.slice(0, 8) + '…' : id)
}


function describe(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 403)
      return 'سجل المراجعة يتطلب دور auditor — وهو دور لا يُمنح لمن يدير المزادات، بالتصميم.'
    if (e.status === 401) return 'انتهت الجلسة. أعد تسجيل الدخول.'
    if (e.status === 0) return 'لا يمكن الوصول إلى خدمة المراجعة. تأكد من أنها تعمل.'
    return e.problems[0] ?? e.message
  }
  return e instanceof Error ? e.message : String(e)
}
