import { useCallback, useEffect, useState } from 'react'
import { ApiError, api, config, timestamp, type Api } from '@eauction/shared'

interface Clarification {
  id: string
  questionAr: string
  answerAr: string
  publishedAt: string
}

interface MyInquiry {
  id: string
  question: string
  askedAt: string
  status: 'Open' | 'Answered' | 'Closed'
  answer: string | null
  answeredAt: string | null
}

const statusAr: Record<MyInquiry['status'], { ar: string; tone: string }> = {
  Open: { ar: 'بانتظار الرد', tone: 'wait' },
  Answered: { ar: 'تم الرد', tone: 'live' },
  Closed: { ar: 'مغلق', tone: 'done' },
}

/**
 * الاستفسارات (الخاصية 10) on the auction page: the clarifications published for
 * everyone, and — for a signed-in bidder — their own questions with the private
 * replies, and a box to ask another while the auction has not finished.
 */
export function AuctionInquiries({
  auctionId,
  participant,
  canAsk,
  open,
}: {
  auctionId: string
  /** Null when nobody is signed in: then only the public clarifications show. */
  participant: Api | null
  canAsk: boolean
  /** Questions are taken until the auction finishes. */
  open: boolean
}) {
  const [clarifications, setClarifications] = useState<Clarification[]>([])
  const [mine, setMine] = useState<MyInquiry[]>([])
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [sent, setSent] = useState(false)

  useEffect(() => {
    api({ baseUrl: config.queryApi, session: null })
      .get<{ items: Clarification[] }>(`/auctions/${auctionId}/clarifications`)
      .then((r) => setClarifications(r.items))
      .catch(() => undefined)
  }, [auctionId])

  const loadMine = useCallback(() => {
    if (!participant || !canAsk) return
    participant
      .get<{ items: MyInquiry[] }>(`/auctions/${auctionId}/inquiries/mine`)
      .then((r) => setMine(r.items))
      .catch(() => undefined)
  }, [participant, canAsk, auctionId])

  useEffect(() => loadMine(), [loadMine])

  const ask = async () => {
    if (!participant) return
    setBusy(true)
    setProblem(null)
    setSent(false)
    try {
      await participant.post(`/auctions/${auctionId}/inquiries`, { question: text })
      setText('')
      setSent(true)
      loadMine()
    } catch (e) {
      setProblem(
        e instanceof ApiError && e.problems.length > 0
          ? e.problems.join(' ')
          : 'تعذّر إرسال الاستفسار. حاول مرة أخرى.',
      )
    } finally {
      setBusy(false)
    }
  }

  if (clarifications.length === 0 && !(canAsk && (open || mine.length > 0))) return null

  return (
    <div className="card" data-testid="auction-inquiries">
      <h2>الاستفسارات</h2>

      {clarifications.length > 0 && (
        <>
          <h3 style={{ marginTop: 0 }}>توضيحات الأمانة</h3>
          {clarifications.map((c) => (
            <div key={c.id} className="clarification">
              <div className="q">س: {c.questionAr}</div>
              <div>ج: {c.answerAr}</div>
              <div className="muted small">نُشر {timestamp(c.publishedAt)}</div>
            </div>
          ))}
        </>
      )}

      {canAsk && mine.length > 0 && (
        <>
          <h3>استفساراتي</h3>
          <div className="inquiry-list">
            {mine.map((m) => {
              const st = statusAr[m.status]
              return (
                <div key={m.id} className="inquiry-card">
                  <div className="row" style={{ gap: 8 }}>
                    <span className={`pill ${st.tone}`}>{st.ar}</span>
                    <span className="grow" />
                    <span className="muted small">{timestamp(m.askedAt)}</span>
                  </div>
                  <p className="inquiry-question">{m.question}</p>
                  {m.answer && (
                    <div className="inquiry-answer">
                      <div className="muted small">رد الأمانة · {timestamp(m.answeredAt)}</div>
                      {m.answer}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        </>
      )}

      {canAsk && open && (
        <div className="inquiry-actions" style={{ marginTop: 12 }}>
          <label htmlFor="ask">اطرح سؤالاً عن هذا المزاد</label>
          <textarea
            id="ask"
            rows={3}
            maxLength={2000}
            value={text}
            placeholder="مثال: هل تشمل مساحة القطعة الارتداد؟"
            onChange={(e) => setText(e.target.value)}
          />
          <p className="muted small" style={{ margin: 0 }}>
            يصلك الرد هنا وفي الإشعارات، ولا يراه غيرك. قد تنشر الأمانة توضيحاً عاماً بصياغتها دون
            ذكر بياناتك.
          </p>
          {problem && <div className="notice error small">{problem}</div>}
          {sent && <div className="notice ok small">أُرسل استفسارك.</div>}
          <div>
            <button className="primary" disabled={busy || !text.trim()} onClick={() => void ask()}>
              إرسال الاستفسار
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
