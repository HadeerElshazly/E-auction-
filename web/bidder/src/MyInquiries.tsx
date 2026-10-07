import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, Icon, PageHead, api, config, timestamp, type Session } from '@eauction/shared'
import type { AuctionSummary } from './types'

interface Row {
  inquiry: {
    id: string
    auctionId: string
    question: string
    askedAt: string
    status: 'Open' | 'Answered' | 'Closed'
    answer: string | null
    answeredAt: string | null
  }
  auctionNameAr: string | null
}

const statusAr: Record<Row['inquiry']['status'], { ar: string; tone: string }> = {
  Open: { ar: 'بانتظار الرد', tone: 'wait' },
  Answered: { ar: 'تمت الإجابة', tone: 'live' },
  Closed: { ar: 'مغلق', tone: 'done' },
}

/**
 * الاستفسارات — every question this bidder has asked, on any auction, with where
 * each stands and the reply. Replies are theirs alone; what the municipality
 * publishes for everyone is on each auction's own page.
 */
export function MyInquiries({
  session,
  auctions,
  onOpen,
}: {
  session: Session
  auctions: AuctionSummary[]
  onOpen: (auctionId: string) => void
}) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const [rows, setRows] = useState<Row[] | null>(null)
  const [asking, setAsking] = useState(false)
  const [auctionId, setAuctionId] = useState('')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // Questions are taken while an auction has not finished.
  const askable = auctions.filter((a) => ['Scheduled', 'Approved', 'Live'].includes(a.status))

  const load = useCallback(() => {
    participant
      .get<{ items: Row[] }>('/inquiries/mine')
      .then((r) => setRows(r.items))
      .catch(() => setRows([]))
  }, [participant])

  useEffect(() => load(), [load])

  const send = async () => {
    setBusy(true)
    setProblem(null)
    try {
      await participant.post(`/auctions/${auctionId}/inquiries`, { question: text })
      setText('')
      setAsking(false)
      load()
    } catch (e) {
      setProblem(
        e instanceof ApiError && e.problems.length > 0 ? e.problems.join(' ') : 'تعذّر إرسال الاستفسار.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHead
        eyebrow="مساحة المزايد"
        title="الاستفسارات"
        sub="تابع أسئلتك الخاصة وردود الأمانة عليها."
        action={
          !asking && (
            <button className="primary" onClick={() => setAsking(true)} disabled={askable.length === 0}>
              <Icon name="plus" size={18} /> استفسار جديد
            </button>
          )
        }
      />

      {asking && (
        <div className="card inquiry-actions">
          <h2>استفسار عن مزاد</h2>
          <label>
            <span>المزاد</span>
            <select value={auctionId} onChange={(e) => setAuctionId(e.target.value)}>
              <option value="">اختر المزاد…</option>
              {askable.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nameAr}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>سؤالك</span>
            <textarea
              rows={4}
              maxLength={2000}
              value={text}
              placeholder="اكتب استفسارك عن المزاد أو شروط المشاركة"
              onChange={(e) => setText(e.target.value)}
            />
          </label>
          <p className="muted small" style={{ margin: 0 }}>
            يصلك الرد هنا وفي الإشعارات، ولا يراه غيرك.
          </p>
          {problem && <div className="notice error small">{problem}</div>}
          <div className="row" style={{ gap: 8 }}>
            <button className="primary" disabled={busy || !auctionId || !text.trim()} onClick={() => void send()}>
              إرسال الاستفسار
            </button>
            <button className="ghost" onClick={() => setAsking(false)}>
              إلغاء
            </button>
          </div>
        </div>
      )}

      <div className="card">
        {rows === null ? (
          <p className="muted">…</p>
        ) : rows.length === 0 ? (
          <div className="empty-state">
            <h3>لا توجد استفسارات</h3>
            <p className="muted">يمكنك إرسال سؤال من هنا أو من صفحة المزاد، قسم «الاستفسارات».</p>
          </div>
        ) : (
          rows.map(({ inquiry: q, auctionNameAr }) => {
            const st = statusAr[q.status]
            return (
              <div key={q.id} className="qa-item">
                <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
                  <button className="link strong" onClick={() => onOpen(q.auctionId)}>
                    {auctionNameAr ?? 'مزاد'}
                  </button>
                  <span className={`pill ${st.tone}`}>{st.ar}</span>
                  <span className="grow" />
                  <span className="muted small">{timestamp(q.askedAt)}</span>
                </div>
                <p>{q.question}</p>
                {q.answer && (
                  <div className="inquiry-answer">
                    <div className="muted small">رد الأمانة · {timestamp(q.answeredAt)}</div>
                    {q.answer}
                  </div>
                )}
                <small className="muted">سؤال خاص</small>
              </div>
            )
          })
        )}
      </div>
    </>
  )
}
