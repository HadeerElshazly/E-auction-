import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, api, config, timestamp, type Session } from '@eauction/shared'

interface Inquiry {
  id: string
  auctionId: string
  question: string
  askedAt: string
  status: 'Open' | 'Answered' | 'Closed'
  answer: string | null
  answeredAt: string | null
  closedAt: string | null
  clarification: 'None' | 'Drafted' | 'Published'
  clarificationPublishedAt: string | null
  bidderNameAr: string | null
  auctionNameAr: string | null
  clarificationQuestion: string | null
  clarificationAnswer: string | null
  clarificationDraftedBy: string | null
}

interface Page {
  items: Inquiry[]
  counts: { open: number; answered: number; closed: number; awaitingApproval: number }
}

type Filter = 'Open' | 'Answered' | 'Closed' | 'Drafted' | 'all'

const statusAr: Record<Inquiry['status'], { ar: string; tone: string }> = {
  Open: { ar: 'جديد', tone: 'wait' },
  Answered: { ar: 'تم الرد', tone: 'live' },
  Closed: { ar: 'مغلق', tone: 'done' },
}

/**
 * الاستفسارات والإجابات (المرحلة الأولى، الخاصية 10) — the staff side.
 *
 * The inquiries desk replies to the bidder who asked, privately, and sets where the
 * question stands. Where the answer is worth everyone knowing, the administrator
 * writes a public clarification in their own words — the bidder's question may
 * mention themselves — and a second member of the desk approves it before it is
 * published. The desk is its own role (inquiries), not the administrators'.
 */
export function Inquiries({ session, canAct }: { session: Session; canAct: boolean }) {
  // The desk replies, and approves a clarification someone else drafted.
  // Administrators and the committee read the same screen without the controls.
  const canReply = canAct
  const client = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const [filter, setFilter] = useState<Filter>('Open')
  const [page, setPage] = useState<Page | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)

  useEffect(() => {
    const qs =
      filter === 'all' ? '' : filter === 'Drafted' ? '?clarification=Drafted' : `?status=${filter}`
    client
      .get<Page>(`/inquiries${qs}`)
      .then((p) => {
        setPage(p)
        setError(null)
      })
      .catch((e) => setError(describe(e)))
  }, [client, filter, refresh])

  const reload = useCallback(() => setRefresh((n) => n + 1), [])

  const chips: Array<{ key: Filter; ar: string; count?: number }> = [
    { key: 'Open', ar: 'جديد', count: page?.counts.open },
    { key: 'Answered', ar: 'تم الرد', count: page?.counts.answered },
    { key: 'Closed', ar: 'مغلق', count: page?.counts.closed },
    { key: 'Drafted', ar: 'توضيحات بانتظار الاعتماد', count: page?.counts.awaitingApproval },
    { key: 'all', ar: 'الكل' },
  ]

  return (
    <div className="card">
      <div className="section-head">
        <h2>الاستفسارات</h2>
      </div>
      <p className="lede">
        أسئلة المزايدين عن المزادات. الرد يصل إلى السائل وحده؛ وما يستحق أن يعرفه الجميع يُكتب
        توضيحاً عاماً بصياغة الأمانة، ويُنشر في صفحة المزاد بعد أن يعتمده زميل غير من صاغه.
      </p>

      <div className="chips" role="tablist" aria-label="حالة الاستفسار" style={{ marginBottom: 12 }}>
        {chips.map((c) => (
          <button
            key={c.key}
            role="tab"
            aria-selected={filter === c.key}
            className={filter === c.key ? 'chip on' : 'chip'}
            onClick={() => setFilter(c.key)}
          >
            {c.ar} {c.count !== undefined && <span className="num">({c.count})</span>}
          </button>
        ))}
      </div>

      {!canAct && (
        <div className="notice info small">للاطلاع فقط — الرد والنشر من صلاحية فريق الاستفسارات.</div>
      )}
      {error && <div className="notice error">{error}</div>}
      {page && page.items.length === 0 && <p className="muted">لا توجد استفسارات هنا.</p>}

      <div className="inquiry-list">
        {page?.items.map((i) => (
          <InquiryCard
            key={i.id}
            inquiry={i}
            client={client}
            canReply={canReply}
            canApprove={canAct && i.clarificationDraftedBy !== session.subject}
            onDone={reload}
          />
        ))}
      </div>
    </div>
  )
}

function InquiryCard({
  inquiry: i,
  client,
  canReply,
  canApprove,
  onDone,
}: {
  inquiry: Inquiry
  client: ReturnType<typeof api>
  canReply: boolean
  canApprove: boolean
  onDone: () => void
}) {
  const [answer, setAnswer] = useState('')
  const [drafting, setDrafting] = useState(false)
  const [pq, setPq] = useState(i.clarificationQuestion ?? '')
  const [pa, setPa] = useState(i.clarificationAnswer ?? i.answer ?? '')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const st = statusAr[i.status]

  const run = async (work: () => Promise<unknown>) => {
    setBusy(true)
    setProblem(null)
    try {
      await work()
      onDone()
    } catch (e) {
      setProblem(describe(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="inquiry-card" data-testid="inquiry">
      <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
        <span className={`pill ${st.tone}`}>{st.ar}</span>
        <strong>{i.auctionNameAr ?? i.auctionId}</strong>
        <span className="muted small">— {i.bidderNameAr ?? 'مزايد'}</span>
        <span className="grow" />
        <span className="muted small">{timestamp(i.askedAt)}</span>
      </div>

      <p className="inquiry-question">{i.question}</p>

      {i.answer && (
        <div className="inquiry-answer">
          <div className="muted small">الرد على السائل · {timestamp(i.answeredAt)}</div>
          {i.answer}
        </div>
      )}

      {canReply && i.status !== 'Closed' && (
        <div className="inquiry-actions">
          <textarea
            rows={3}
            value={answer}
            placeholder={i.answer ? 'رد إضافي…' : 'اكتب الرد على المزايد…'}
            onChange={(e) => setAnswer(e.target.value)}
          />
          <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
            <button
              className="primary"
              disabled={busy || !answer.trim()}
              onClick={() => void run(() => client.post(`/inquiries/${i.id}/reply`, { answer, close: false }))}
            >
              إرسال الرد
            </button>
            <button
              disabled={busy || !answer.trim()}
              onClick={() => void run(() => client.post(`/inquiries/${i.id}/reply`, { answer, close: true }))}
            >
              إرسال الرد وإغلاق
            </button>
            <button
              className="ghost"
              disabled={busy}
              onClick={() => void run(() => client.post(`/inquiries/${i.id}/close`, {}))}
            >
              إغلاق دون رد
            </button>
          </div>
        </div>
      )}

      {/* The public clarification: drafted by an admin, approved by the committee. */}
      {i.clarification === 'Published' && (
        <div className="notice ok small">
          نُشر توضيح عام على صفحة المزاد · {timestamp(i.clarificationPublishedAt)}
          <div style={{ marginTop: 6 }}>
            <strong>س:</strong> {i.clarificationQuestion}
            <br />
            <strong>ج:</strong> {i.clarificationAnswer}
          </div>
        </div>
      )}

      {i.clarification === 'Drafted' && (
        <div className="notice info small">
          <strong>توضيح عام بانتظار الاعتماد</strong>
          {canReply && !canApprove && (
            <div className="muted small">صغتَه أنت — يعتمده زميل آخر من فريق الاستفسارات.</div>
          )}
          <div style={{ marginTop: 6 }}>
            <strong>س:</strong> {i.clarificationQuestion}
            <br />
            <strong>ج:</strong> {i.clarificationAnswer}
          </div>
          {canApprove && (
            <button
              className="primary"
              style={{ marginTop: 8 }}
              disabled={busy}
              onClick={() => void run(() => client.post(`/inquiries/${i.id}/clarification/approve`, {}))}
            >
              اعتماد ونشر التوضيح
            </button>
          )}
        </div>
      )}

      {canReply && i.clarification !== 'Published' && !drafting && (
        <button className="ghost small" onClick={() => setDrafting(true)}>
          {i.clarification === 'Drafted' ? 'تعديل التوضيح العام' : 'صياغة توضيح عام'}
        </button>
      )}

      {drafting && (
        <div className="inquiry-actions">
          <div className="notice info small">
            يُنشر للجميع بعد الاعتماد. صُغه بعبارتك، ولا تذكر اسم السائل أو بياناته أو أي تفاصيل
            خاصة وردت في سؤاله.
          </div>
          <label className="small">السؤال كما يُنشر</label>
          <textarea rows={2} value={pq} onChange={(e) => setPq(e.target.value)} />
          <label className="small">الإجابة كما تُنشر</label>
          <textarea rows={3} value={pa} onChange={(e) => setPa(e.target.value)} />
          <div className="row" style={{ gap: 8 }}>
            <button
              className="primary"
              disabled={busy || !pq.trim() || !pa.trim()}
              onClick={() =>
                void run(async () => {
                  await client.post(`/inquiries/${i.id}/clarification`, { questionAr: pq, answerAr: pa })
                  setDrafting(false)
                })
              }
            >
              رفع للاعتماد
            </button>
            <button className="ghost" onClick={() => setDrafting(false)}>
              إلغاء
            </button>
          </div>
        </div>
      )}

      {problem && <div className="notice error small">{problem}</div>}
    </div>
  )
}

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    return e.problems.length > 0 ? e.problems.join(' ') : `تعذّر التنفيذ (${e.status}).`
  }
  return e instanceof Error ? e.message : String(e)
}
