import { useCallback, useEffect, useRef, useState } from 'react'
import {
  ApiError,
  api,
  buildBidFrame,
  config,
  newClientBidId,
  newNonce,
  sar,
  type Api,
  type Session,
} from '@eauction/shared'
import type { Auction, RosterEntry } from './types'

/**
 * قاعة المزاد — the screen a clerk runs an auction from (§29).
 *
 * Everything on it is one of three jobs: enter a bid for somebody in the room,
 * move the end time, bring the hammer down. Nothing else belongs here, because a
 * clerk is using it while an auctioneer is calling prices and a room is waiting.
 *
 * The bid is signed in this browser with the clerk's own key, exactly as a bidder's
 * portal signs with theirs — the frame then carries both the bidder it is for and
 * the clerk who entered it, which is the whole evidential story of an onsite bid
 * (D-36). The key is fetched once, on the first bid rather than on mount, and never
 * leaves this tab.
 *
 * The paddle number is what the clerk actually works from. In the room a bidder
 * holds up a number, not a name, so the number is the primary thing on screen and
 * typing it is enough to select them.
 */
interface Props {
  auction: Auction
  session: Session
  client: Api
  onAct: (what: () => Promise<unknown>) => Promise<void>
  busy: boolean
}

export function ClerkTerminal({ auction, session, client, onAct, busy }: Props) {
  const catcher = api({ baseUrl: config.catcherApi, session })
  const participant = api({ baseUrl: config.participantApi, session })

  const key = useRef<string | null>(null)
  const [roster, setRoster] = useState<RosterEntry[]>([])
  const [paddle, setPaddle] = useState('')
  const [amount, setAmount] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [entered, setEntered] = useState<{ paddle: number; amount: number; offset: number }[]>([])
  const [sending, setSending] = useState(false)

  const isTheClerk = auction.clerkUserId === session.subject

  useEffect(() => {
    if (!isTheClerk) return
    let cancelled = false

    participant
      .get<{ items: RosterEntry[] }>(`/auctions/${auction.id}/subscriptions`)
      .then((page) => {
        if (!cancelled) setRoster(page.items)
      })
      .catch((e: unknown) => {
        if (!cancelled) setProblem(e instanceof Error ? e.message : String(e))
      })

    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auction.id, isTheClerk])

  const signingKey = useCallback(async (): Promise<string> => {
    if (key.current) return key.current
    const fetched = await client.get<{ secretHex: string }>(`/auctions/${auction.id}/clerk-key`)
    key.current = fetched.secretHex
    return fetched.secretHex
  }, [client, auction.id])

  const bidder = roster.find((r) => String(r.paddleNumber) === paddle.trim())
  const riyals = Number(amount.replace(/[^0-9.]/g, ''))
  const minorUnits = Number.isFinite(riyals) ? Math.round(riyals * 100) : NaN
  const canSend = !sending && bidder !== undefined && Number.isFinite(minorUnits) && minorUnits > 0

  async function send() {
    if (!bidder) return
    setSending(true)
    setProblem(null)

    try {
      const frame = await buildBidFrame({
        auctionId: auction.id,
        bidderId: bidder.bidderId,
        amountMinorUnits: minorUnits,
        clientTimestampMs: Date.now(),
        clientBidId: newClientBidId(),
        nonce: newNonce(),
        signingSecretHex: await signingKey(),
      })

      const receipt = await catcher.postFrame<{ offset: number }>('/bids', frame)

      setEntered((prior) => [
        { paddle: bidder.paddleNumber, amount: minorUnits, offset: receipt.offset },
        ...prior.slice(0, 7),
      ])
      setAmount('')
    } catch (e) {
      // The clerk is standing in front of a room. "InvalidSignature" tells them
      // nothing they can act on, so each refusal says what to do about it.
      setProblem(
        e instanceof ApiError && e.reason ? (refusals[e.reason] ?? e.reason) : describe(e),
      )
    } finally {
      setSending(false)
    }
  }

  if (!isTheClerk) {
    return (
      <div className="card">
        <h2>قاعة المزاد</h2>
        <p className="muted">
          {auction.clerkUserId
            ? 'هذا المزاد يديره موظف آخر في القاعة.'
            : 'لم يُعيَّن موظف قاعة لهذا المزاد بعد.'}
        </p>

      </div>
    )
  }

  return (
    <div className="card">
      <h2>قاعة المزاد</h2>
      <p className="muted small">
        تُوقَّع كل مزايدة بمفتاحك أنت، ويُسجَّل في السجل اسم المزايد ومَن أدخل
        المزايدة نيابةً عنه.
      </p>

      {problem && <div className="notice error">{problem}</div>}

      <div className="row" style={{ alignItems: 'flex-end' }}>
        <label style={{ flex: '0 0 120px', marginBottom: 0 }}>
          <span>رقم المجداف</span>
          <input
            className="ltr num"
            inputMode="numeric"
            aria-label="رقم المجداف"
            value={paddle}
            onChange={(e) => setPaddle(e.target.value)}
          />
        </label>

        <label style={{ flex: '1 1 200px', marginBottom: 0 }}>
          <span>المبلغ (ر.س)</span>
          <input
            className="ltr num"
            inputMode="decimal"
            aria-label="مبلغ المزايدة"
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && canSend) void send()
            }}
          />
        </label>

        <button className="primary big" disabled={!canSend} onClick={() => void send()}>
          {sending ? 'جارٍ التسجيل…' : 'تسجيل المزايدة'}
        </button>
      </div>

      <div className="small muted" style={{ marginTop: 8 }}>
        {paddle.trim() === ''
          ? 'أدخل رقم المجداف.'
          : bidder
            ? `المزايد: ${bidder.nameAr}`
            : 'لا يوجد مزايد مؤهَّل بهذا الرقم.'}
      </div>

      <h3>إدارة الجلسة</h3>
      <div className="row">
        <button disabled={busy} onClick={() => void onAct(() => extend(client, auction.id, 120))}>
          تمديد دقيقتين
        </button>
        <button
          className="primary"
          disabled={busy}
          onClick={() => void onAct(() => close(client, auction.id))}
        >
          إغلاق المزاد
        </button>
      </div>
      <p className="muted small">
        التمديد محدود بعدد مرات التمديد المعلن في شروط المزاد. الإغلاق هو الشيء
        الوحيد الذي ينهي مزاد القاعة.
      </p>

      <h3>المزايدات المُدخلة ({roster.length} مزايد مؤهَّل)</h3>
      {entered.length === 0 ? (
        <p className="muted small">لم تُدخل أي مزايدة بعد في هذه الجلسة.</p>
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>المجداف</th>
                <th>المبلغ</th>
                <th>الترتيب في السجل</th>
              </tr>
            </thead>
            <tbody>
              {entered.map((e) => (
                <tr key={e.offset}>
                  <td className="num">{e.paddle}</td>
                  <td className="num">{sar(e.amount, 'ar')}</td>
                  <td className="num small">{e.offset}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

const extend = (client: Api, id: string, seconds: number) =>
  client.post(`/auctions/${id}/extend`, { seconds })

const close = (client: Api, id: string) => client.post(`/auctions/${id}/close`)

/**
 * What a clerk should do about each refusal. The catcher's reasons are written for
 * a developer reading a log; this is the same information for somebody holding up
 * a room.
 */
const refusals: Record<string, string> = {
  NotEligible: 'هذا المزايد غير مؤهَّل — لم يكتمل التأمين أو أُلغي اشتراكه.',
  NotTheClerk: 'لم تعد مُعيَّناً على هذا المزاد. راجع إدارة المزادات.',
  BadSignature: 'انتهت صلاحية مفتاحك. أعد تحميل الصفحة.',
  OutsideWindow: 'لم يبدأ المزاد بعد.',
  BelowOpeningPrice: 'المبلغ أقل من سعر الافتتاح.',
  BelowMinimumIncrement: 'المبلغ لا يزيد على السعر الحالي بالحد الأدنى للزيادة.',
  RateLimited: 'مزايدات كثيرة بسرعة. أعد المحاولة بعد لحظة.',
  UnknownAuction: 'المزاد غير معروف لخدمة المزايدة بعد.',
  MalformedFrame: 'تعذّر بناء المزايدة. أعد تحميل الصفحة.',
}

function describe(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}
