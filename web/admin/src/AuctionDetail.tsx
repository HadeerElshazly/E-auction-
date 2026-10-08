import { useEffect, useState } from 'react'
import {
  CountdownPanel,
  Icon,
  PhotoGallery,
  Pager,
  usePage,
  facingAr,
  landUseAr,
  api,
  config,
  riyals,
  sar,
  timestamp,
  when,
  type Api,
  type Session,
} from '@eauction/shared'
import type { Auction } from './types'
import { label } from './types'
import { AuctionEditor, usePublicPrice } from './AuctionEditor'
import { Applicants } from './Applicants'
import { AwardPanel } from './AwardPanel'
import { ClerkTerminal } from './ClerkTerminal'
import { BidHistory } from './AuditViews'
import { BidderName, useLeaders } from './winners'
import { EndAuction, Reoffer, ReviewDecision, SubmitForApproval } from './AuctionActions'
import { PlotsPanel } from './PlotsPanel'

type Tab = 'info' | 'gallery' | 'documents' | 'bids' | 'inquiries' | 'setup' | 'applicants' | 'award' | 'hall'

const opened = (status: string) =>
  !['Draft', 'PendingReview', 'Rejected', 'Approved', 'Scheduled', 'Cancelled'].includes(status)

/**
 * An auction, as staff see it — the prototype's page: the subject on one side
 * (photo, details, bids, questions), the box with the figure that matters and the
 * result on the other, and the work of each role in its own tab beside them.
 */
export function AuctionDetail({
  auction,
  client,
  session,
  busy,
  isAdmin,
  isCommittee,
  isClerk,
  onAct,
  onOpen,
}: {
  auction: Auction
  client: Api
  session: Session
  busy: boolean
  isAdmin: boolean
  isCommittee: boolean
  isClerk: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
  /** Opens another auction — the new draft a re-offer creates. */
  onOpen: (id: string) => void
}) {
  const l = label(auction.status)
  const figures = usePublicPrice(auction)
  const leaderId = useLeaders(session)[auction.id]
  const award = auction.currentAward ?? auction.followUpAward
  const live = auction.status === 'Live'
  const onsite = auction.channel === 'Onsite'

  const tabs: Array<{ key: Tab; label: string }> = [
    { key: 'info', label: 'تفاصيل القطعة' },
    { key: 'gallery', label: 'الصور' },
    { key: 'documents', label: 'المستندات' },
    ...(opened(auction.status) ? [{ key: 'bids' as const, label: 'المزايدات' }] : []),
    { key: 'inquiries', label: 'الاستفسارات' },
    ...(isAdmin ? [{ key: 'setup' as const, label: 'الإعداد والاعتماد' }] : []),
    ...(isAdmin ? [{ key: 'applicants' as const, label: 'المتقدّمون' }] : []),
    ...(isClerk && onsite ? [{ key: 'hall' as const, label: 'القاعة' }] : []),
    { key: 'award', label: 'النتيجة والترسية' },
  ]

  // The part this person came for: an admin's draft to finish, a result waiting for
  // the committee, the clerk's hall — else the auction itself.
  const preferred: Tab =
    isAdmin && ['Draft', 'Rejected'].includes(auction.status)
      ? 'setup'
      : isCommittee && ['PendingAward', 'WinnerDisqualified', 'Awarded'].includes(auction.status)
          ? 'award'
          : isClerk && onsite
            ? 'hall'
            : 'info'
  const [tab, setTab] = useState<Tab>(preferred)
  const [tabFor, setTabFor] = useState(auction.id)
  if (tabFor !== auction.id) {
    setTabFor(auction.id)
    setTab(preferred)
  }
  const current = tabs.some((t) => t.key === tab) ? tab : 'info'

  // How many bids were recorded — the box's «عدد المزايدات».
  const [bidCount, setBidCount] = useState<number | null>(null)
  useEffect(() => {
    if (!opened(auction.status)) return
    api({ baseUrl: config.auditApi, session })
      .get<{ read: number }>(`/audit/auctions/${auction.id}/bids?take=1`)
      .then((r) => setBidCount(r.read))
      .catch(() => setBidCount(null))
  }, [auction.id, auction.status, session])

  const price = figures?.priceMinorUnits ?? null
  const priceLabel = live
    ? price != null ? 'أعلى مزايدة حالية' : 'سعر البداية'
    : award
      ? 'مبلغ الترسية'
      : price != null ? 'أعلى سعر عند الإغلاق' : 'سعر البداية'
  const shown = award?.amountMinorUnits ?? price ?? auction.openingPriceMinorUnits
  const endsAt = figures?.effectiveEndsAt ?? auction.endsAt

  return (
    <div className="split">
      <section>
        {/* The cover above the tabs, set right here while the auction is prepared. */}
        <CoverPhoto
          auction={auction}
          client={client}
          session={session}
          busy={busy}
          canEdit={isAdmin && ['Draft', 'Rejected'].includes(auction.status)}
          onAct={onAct}
          badge={<span className={`pill ${l.tone}`}>{l.ar}</span>}
        />

        <div className="detail-tabs" role="tablist" aria-label="أقسام المزاد">
          {tabs.map((t) => (
            <button
              key={t.key}
              role="tab"
              aria-selected={current === t.key}
              className={`tab${current === t.key ? ' active' : ''}`}
              onClick={() => setTab(t.key)}
              data-testid={`auction-tab-${t.key}`}
            >
              {t.label}
            </button>
          ))}
        </div>

        <div className="detail-content">
          {current === 'info' && (
            <Info
              auction={auction}
              client={client}
              busy={busy}
              canEdit={isAdmin && ['Draft', 'Rejected'].includes(auction.status)}
              onAct={onAct}
            />
          )}
          {current === 'documents' && <Documents auction={auction} />}
          {current === 'gallery' && (
            <Gallery auction={auction} client={client} session={session} busy={busy} canAdd={isAdmin} onAct={onAct} />
          )}
          {current === 'bids' && (
            <>
              <div className="panel-title">
                <h2>سجل المزايدات</h2>
                {bidCount != null && <span className="muted">{bidCount} مزايدة</span>}
              </div>
              <BidHistory session={session} auctionId={auction.id} withDecisions={false} live={live} />
            </>
          )}
          {current === 'inquiries' && <AuctionQuestions auctionId={auction.id} session={session} />}
          {current === 'setup' && (
            <AuctionEditor
              auction={auction}
              client={client}
              session={session}
              busy={busy}
              canEdit={isAdmin}
              canApprove={isCommittee}
              onAct={onAct}
            />
          )}
          {current === 'applicants' && isAdmin && (
            <Applicants auction={auction} session={session} busy={busy} onAct={onAct} />
          )}
          {current === 'hall' && isClerk && onsite && (
            <ClerkTerminal auction={auction} session={session} client={client} onAct={onAct} busy={busy} />
          )}
          {current === 'award' && (
            <AwardPanel
              session={session}
              auction={auction}
              client={client}
              busy={busy}
              canAct={isCommittee}
              committeeUserId={session.subject}
              onAct={onAct}
            />
          )}
        </div>
      </section>

      <aside>
        <div className="card auction-box" data-testid="auction-box">
          <div className="kv">
            <span>{priceLabel}</span>
            <span className={`pill ${l.tone}`}>{l.ar}</span>
          </div>
          <div className="summary-price">
            <small>ر.س</small>
            <span className="num">{riyals(shown, 'ar')}</span>
          </div>

          {(auction.status === 'Scheduled' || auction.status === 'Approved') && auction.startsAt && (
            <CountdownPanel target={auction.startsAt} label="حتى البدء" />
          )}
          {live && !onsite && endsAt && <CountdownPanel target={endsAt} label="حتى الإغلاق" />}

          <div className="kv"><span>تأمين المشاركة</span><b className="num">{sar(auction.depositMinorUnits, 'ar')}</b></div>
          {bidCount != null && <div className="kv"><span>عدد المزايدات</span><b className="num">{bidCount}</b></div>}
          <div className="kv"><span>الحد الأدنى للزيادة</span><b className="num">{sar(auction.minIncrementMinorUnits, 'ar')}</b></div>
          {/* What staff need besides the prototype's three rows, once each. */}
          <div className="kv"><span>رسوم الكراسة</span><b>{auction.bookletPriceMinorUnits === 0 ? 'مجانية' : sar(auction.bookletPriceMinorUnits, 'ar')}</b></div>
          <div className="kv"><span>نسبة السعي</span><b className="num">{auction.brokerageFeePercent}%</b></div>
          <div className="kv">
            <span>التمديد عند المزايدة المتأخرة</span>
            <b>{auction.quietPeriodSeconds ? `${auction.quietPeriodSeconds} ث، حتى ${auction.maxExtensions} مرات` : 'بلا تمديد'}</b>
          </div>
          {!(auction.status === 'Scheduled' || auction.status === 'Approved' || live) && auction.startsAt && (
            <div className="kv"><span>الموعد</span><b className="small">{when(auction.startsAt)} ← {endsAt ? when(endsAt) : '—'}</b></div>
          )}
          {figures && figures.extensionsUsed > 0 && (
            <div className="kv"><span>التمديد</span><b className="num">{figures.extensionsUsed} من {figures.maxExtensions}</b></div>
          )}
          <hr className="divider" />

          {/* What the auction's state means, in one sentence, with the person behind it. */}
          <div className="result-note">
            {live ? (
              <>
                المزاد جارٍ الآن.
                {leaderId && (
                  <>
                    <br />
                    المزايد الأعلى: <BidderName session={session} id={leaderId} />
                  </>
                )}
              </>
            ) : ['PendingAward', 'PendingEligibilityReview', 'Closing'].includes(auction.status) ? (
              <>
                أُغلق المزاد. النتيجة مبدئية حتى اعتماد اللجنة.
                {(auction.pendingCandidateBidderId ?? leaderId) && (
                  <>
                    <br />
                    الفائز المبدئي: <BidderName session={session} id={auction.pendingCandidateBidderId ?? leaderId} />
                  </>
                )}
              </>
            ) : award && ['Awarded', 'Settled'].includes(auction.status) ? (
              <>
                {auction.status === 'Settled' ? 'اكتملت التسوية.' : 'اعتمدت اللجنة الترسية.'}
                <br />
                الفائز: <BidderName session={session} id={award.bidderId} />
              </>
            ) : auction.status === 'Unsold' ? (
              <>انتهى المزاد دون ترسية.{auction.resultRejectionReason && <><br />السبب: {auction.resultRejectionReason}</>}</>
            ) : auction.status === 'Cancelled' ? (
              <>
                أُلغي المزاد
                {auction.cancellationRefunded === false
                  ? ' دون رد التأمين والكراسة.'
                  : auction.cancellationRefunded
                    ? ' مع رد التأمين والكراسة للمزايدين.'
                    : '.'}
                {auction.cancellationReason && <><br />السبب: {auction.cancellationReason}</>}
              </>
            ) : auction.status === 'Draft' || auction.status === 'Rejected' ? (
              <>مسودة — تُستكمل البيانات ثم تُرفع للاعتماد.</>
            ) : auction.status === 'PendingReview' ? (
              <>بانتظار اعتماد لجنة الترسية.</>
            ) : (
              <>معتمد — يفتح في موعده.</>
            )}
          </div>
          {/* The administrator's way out of a running auction, and back into an
              unsold one. */}
          {isAdmin && live && <EndAuction auction={auction} client={client} busy={busy} onAct={onAct} />}
          {isAdmin && ['Draft', 'Rejected'].includes(auction.status) && (
            <SubmitForApproval auction={auction} client={client} busy={busy} onAct={onAct} />
          )}
          {isCommittee && auction.status === 'PendingReview' && (
            <ReviewDecision auction={auction} client={client} busy={busy} onAct={onAct} />
          )}
          {isAdmin && auction.status === 'Unsold' && (
            <Reoffer auction={auction} client={client} busy={busy} onAct={onAct} onOpen={onOpen} />
          )}
          <p className="box-help">
            <Icon name="shield" size={16} /> كل إجراء هنا يُسجَّل في سجل المراجعة باسم من نفّذه.
          </p>
          <p className="box-help">
            رقم المزاد: <code className="muted small ltr">{auction.id}</code>
          </p>
        </div>
      </aside>
    </div>
  )
}

/**
 * تفاصيل القطعة — for every staff role. The auction's terms are edited under
 * الإعداد; the plots are added right here, while it is a draft.
 */
function Info({
  auction,
  client,
  busy,
  canEdit,
  onAct,
}: {
  auction: Auction
  client: Api
  busy: boolean
  /** The plots are added and removed here while the auction is a draft. */
  canEdit: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}) {
  return (
    <>
      {auction.rejectionReason && <div className="notice error">سبب الرفض: {auction.rejectionReason}</div>}
      {auction.cancellationReason && (
        <div className="notice error">
          أُلغي المزاد{auction.cancelledAt && <> في {when(auction.cancelledAt)}</>} — السبب: {auction.cancellationReason}
        </div>
      )}
      {/* As the prototype has it: the land, its six figures, and the papers. The
          deposit, the step, the dates and the fees are in the box beside it. */}
      <h2>تفاصيل الأرض</h2>
      <p className="muted">
        {auction.plots[0]?.descriptionAr ??
          (auction.plots[0]
            ? `قطعة رقم ${auction.plots[0].plotNumber}${auction.phase ? ` ضمن ${auction.phase}` : ''}.`
            : 'لم تُضف القطعة بعد.')}
      </p>
      <div className="spec-grid">
        <div><small>رقم القطعة</small><b className="num">{auction.plots[0]?.plotNumber ?? '—'}</b></div>
        <div><small>المساحة</small><b><span className="num">{auction.totalAreaSqm}</span> م²</b></div>
        <div>
          <small>الاستخدام</small>
          <b>
            {[...new Set(auction.plots.map((p) => p.landUse).filter(Boolean))].map((u) => landUseAr(u)).join('، ') ||
              'غير محدد'}
          </b>
        </div>
        <div>
          <small>عرض الشارع</small>
          <b>{auction.plots[0]?.streetWidthMeters != null ? <><span className="num">{auction.plots[0].streetWidthMeters}</span> متر</> : '—'}</b>
        </div>
        <div>
          <small>الواجهة</small>
          <b>{facingAr(auction.plots[0]?.facing)}</b>
        </div>
        <div><small>سعر البداية</small><b className="num">{sar(auction.openingPriceMinorUnits, 'ar')}</b></div>
        <div><small>زيادة المزايدة</small><b className="num">{sar(auction.minIncrementMinorUnits, 'ar')}</b></div>
      </div>

      <div className="document-row">
        <span className="doc-icon"><Icon name="pin" /></span>
        <div className="grow">
          <strong>موقع القطعة</strong>
          <small>
            {auction.plots[0] ? 'الموقع على الخريطة وخطا الطول والعرض.' : 'لم تُضف القطعة بعد.'}
          </small>
        </div>
        <PlotsPanel auction={auction} client={client} busy={busy} canEdit={canEdit} onAct={onAct} />
      </div>
    </>
  )
}

interface Question {
  id: string
  question: string
  askedAt: string
  status: 'Open' | 'Answered' | 'Closed'
  answer: string | null
  clarification: 'None' | 'Drafted' | 'Published'
  clarificationQuestion: string | null
  clarificationAnswer: string | null
  bidderNameAr: string | null
}

/** This auction's questions, read-only: the inquiries desk answers them on its own page. */
function AuctionQuestions({ auctionId, session }: { auctionId: string; session: Session }) {
  const [rows, setRows] = useState<Question[] | null>(null)
  const [total, setTotal] = useState(0)
  const paging = usePage()
  useEffect(() => {
    api({ baseUrl: config.participantApi, session })
      .get<{ items: Question[]; total: number }>(`/inquiries?auctionId=${auctionId}&${paging.query}`)
      .then((r) => {
        setRows(r.items)
        setTotal(r.total)
      })
      .catch(() => setRows([]))
  }, [auctionId, session, paging.query])
  const published = rows?.filter((q) => q.clarification === 'Published') ?? []
  return (
    <>
      <div className="panel-title">
        <h2>استفسارات هذا المزاد</h2>
        <a className="button small" href="#inquiries">الاستفسارات</a>
      </div>
      {rows === null && <p className="muted">…</p>}
      {published.map((q) => (
        <div key={`c-${q.id}`} className="qa-item">
          <strong>{q.clarificationQuestion}</strong>
          <div className="inquiry-answer">{q.clarificationAnswer}</div>
          <small className="muted">توضيح عام منشور</small>
        </div>
      ))}
      {rows?.map((q) => (
        <div key={q.id} className="qa-item">
          <div className="row" style={{ gap: 8 }}>
            <span className="pill plain">سؤال خاص</span>
            <span className="muted small">{q.bidderNameAr} · {timestamp(q.askedAt)}</span>
          </div>
          <p>{q.question}</p>
          {q.answer ? <div className="inquiry-answer">{q.answer}</div> : <span className="muted small">بانتظار الرد</span>}
        </div>
      ))}
      {rows && rows.length === 0 && <p className="muted">لا توجد استفسارات على هذا المزاد.</p>}
      <Pager page={paging.page} total={total} noun="استفسار" onPage={paging.setPage} />
    </>
  )
}

/** A document in the document service, by its public address. */
const documentUrl = (id: string) => `${config.documentsApi.replace(/\/$/, '')}/documents/${id}`

/**
 * معرض الصور — the land's photos: the cover and every image attached to the
 * auction. Photos are added while the auction is a draft; once it is published,
 * what bidders were shown is not changed under them.
 */
function Gallery({
  auction,
  client,
  session,
  busy,
  canAdd,
  onAct,
}: {
  auction: Auction
  client: Api
  session: Session
  busy: boolean
  canAdd: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}) {
  // The land's photos; the cover is the picture at the top of the page.
  const images = auction.attachments
    .filter((d) => d.kind !== 'Document')
    .map((d) => ({ id: d.documentId, url: documentUrl(d.documentId), title: d.titleAr }))
  const [title, setTitle] = useState('')
  const draft = ['Draft', 'Rejected'].includes(auction.status)

  const add = (file: File) =>
    onAct(async () => {
      // Public: it is shown on the catalogue to anyone, signed in or not.
      const uploaded = await api({ baseUrl: config.documentsApi, session }).upload<{ id: string }>('/documents', file, {
        access: 'Public',
      })
      await client.post(`/auctions/${auction.id}/attachments`, {
        documentId: uploaded.id,
        titleAr: title.trim() || 'صورة القطعة',
        // A photo: «معرض الصور» in the visitor settings decides who sees it.
        kind: 'Photo',
      })
      setTitle('')
    })

  return (
    <>
      <h2>صور القطعة</h2>
      <PhotoGallery images={images} empty={<p className="muted">لا توجد صور لهذا المزاد بعد.</p>} />
      {canAdd && draft && (
        <div className="row" style={{ gap: 8, marginTop: 14, flexWrap: 'wrap' }}>
          <input
            placeholder="عنوان الصورة — مثل: صورة جوية للقطعة"
            aria-label="عنوان الصورة"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            style={{ flex: '1 1 260px' }}
          />
          <label className={`button${busy ? ' disabled' : ''}`}>
            <Icon name="plus" size={16} /> إضافة صورة
            <input
              type="file"
              accept="image/png,image/jpeg,image/webp"
              hidden
              disabled={busy}
              onChange={(e) => {
                const f = e.target.files?.[0]
                e.target.value = ''
                if (f) void add(f)
              }}
            />
          </label>
        </div>
      )}
      {canAdd && !draft && (
        <p className="muted small" style={{ marginTop: 12 }}>
          تُضاف الصور والمستندات العامة أثناء إعداد المزاد؛ بعد نشره لا يتغيّر ما عُرض على المزايدين.
        </p>
      )}
    </>
  )
}

/**
 * صورة الغلاف — the picture at the top of the auction page, and of its card on the
 * catalogue. While the auction is prepared, an administrator sets or replaces it
 * right where it shows.
 */
function CoverPhoto({
  auction,
  client,
  session,
  busy,
  canEdit,
  onAct,
  badge,
}: {
  auction: Auction
  client: Api
  session: Session
  busy: boolean
  canEdit: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
  badge: React.ReactNode
}) {
  const [failed, setFailed] = useState(false)
  const id = auction.coverImageDocumentId
  const set = (file: File) =>
    onAct(async () => {
      // Public: the catalogue shows it to visitors before they sign in.
      const uploaded = await api({ baseUrl: config.documentsApi, session }).upload<{ id: string }>('/documents', file, {
        access: 'Public',
      })
      await client.post(`/auctions/${auction.id}/cover-image`, { documentId: uploaded.id })
      setFailed(false)
    })

  return (
    <div className="detail-photo" data-testid="cover-photo">
      {id && !failed && <img key={id} src={documentUrl(id)} alt={`صورة ${auction.nameAr}`} onError={() => setFailed(true)} />}
      {badge}
      {canEdit && (
        <label className={`button cover-button${busy ? ' disabled' : ''}`}>
          <Icon name="image" size={16} /> {id ? 'تغيير الغلاف' : 'إرفاق غلاف'}
          <input
            type="file"
            accept="image/png,image/jpeg,image/webp"
            aria-label="ملف صورة الغلاف"
            hidden
            disabled={busy}
            onChange={(e) => {
              const f = e.target.files?.[0]
              e.target.value = ''
              if (f) void set(f)
            }}
          />
        </label>
      )}
    </div>
  )
}

/**
 * المستندات — the booklet and the public documents of the auction, on their own
 * tab. They are attached under «الإعداد والاعتماد» while the auction is prepared.
 */
function Documents({ auction }: { auction: Auction }) {
  const papers = auction.attachments.filter((d) => d.kind !== 'Photo')
  return (
    <>
      <h2>المستندات</h2>
      <div className="document-row">
        <span className="doc-icon"><Icon name="file" /></span>
        <div className="grow">
          <strong>كراسة الشروط</strong>
          <small>{auction.bookletDocumentId ? 'مرفقة — تُتاح للمزايد بعد شرائها' : 'لم تُرفق بعد'}</small>
        </div>
      </div>
      {auction.attachments.filter((d) => d.kind !== 'Photo').map((d) => (
        <div key={d.documentId} className="document-row">
          <span className="doc-icon"><Icon name="file" /></span>
          <div className="grow">
            <strong>{d.titleAr}</strong>
            <small>مستند عام متاح للجميع</small>
          </div>
          <a
            className="button small"
            href={`${config.documentsApi.replace(/\/$/, '')}/documents/${d.documentId}`}
            rel="noreferrer noopener"
          >
            <Icon name="download" size={16} /> تنزيل
          </a>
        </div>
      ))}
      {papers.length === 0 && <p className="muted small" style={{ marginTop: 12 }}>لا توجد مستندات عامة مرفقة.</p>}
    </>
  )
}
