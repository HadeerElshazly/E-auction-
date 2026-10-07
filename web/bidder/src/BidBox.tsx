import { useEffect, useState } from 'react'
import {
  ApiError,
  api,
  buildBidFrame,
  clock,
  config,
  newClientBidId,
  newNonce,
  parseRiyals,
  riyals,
  finishedStages,
  sar,
  type Api,
  type Session,
  when,
} from '@eauction/shared'
import { reasons } from './reasons'
import type { AuctionDetail, BidReceipt, BidVerdict, LivePrice } from './types'
import { useSigningKey } from './useSigningKey'
import { Certificate } from './Certificate'

interface Props {
  auction: AuctionDetail
  session: Session
  price: LivePrice | null
  /** This bidder's own verdicts, pushed by the fan-out. */
  verdicts: BidVerdict[]
  participant: Api
  onBid: () => void
}

interface Submitted {
  clientBidId: string
  amount: number
  offset: number
  at: Date
}

/**
 * The bidder's own submitted bids, kept in this browser per auction and bidder so
 * «عروض المستخدم المقبولة» survive a reload (الخاصية 06).
 *
 * Browser storage, not the server: the platform keeps the latest price per auction,
 * not each bidder's history, and a durable per-bidder history is a processor change
 * of its own. So this is per device — honest about it in the panel — and every read
 * is guarded, because storage can be absent or full.
 */
const KEEP = 20

function storageKey(auctionId: string, bidderId: string): string {
  return `eauction:bids:${auctionId}:${bidderId}`
}

function loadSubmitted(auctionId: string, bidderId: string): Submitted[] {
  try {
    const raw = window.localStorage.getItem(storageKey(auctionId, bidderId))
    if (!raw) return []
    const rows = JSON.parse(raw) as Array<Omit<Submitted, 'at'> & { at: string }>
    return rows.map((r) => ({ ...r, at: new Date(r.at) }))
  } catch {
    return []
  }
}

function saveSubmitted(auctionId: string, bidderId: string, rows: Submitted[]): void {
  try {
    window.localStorage.setItem(storageKey(auctionId, bidderId), JSON.stringify(rows))
  } catch {
    // Private mode or a full quota: the list still works for this visit.
  }
}

/** What the services call a rejection, in Arabic a bidder can act on. */

/**
 * The processor's actual ruling on one submitted bid.
 *
 * Previously inferred by comparing against the price, because the processor's ruling
 * went to `bids.rejected` and nothing delivered it to a browser. The fan-out now
 * does, so each bid gets its own verdict matched by client bid id — and a bid
 * refused as a self-outbid or a duplicate is named as such instead of being guessed
 * at from a price that happens not to have moved.
 *
 * Inference survives only as the fallback for a bid whose verdict has not arrived,
 * which on a polling connection is every bid.
 */
function outcome(bid: Submitted, price: LivePrice | null, verdicts: BidVerdict[]) {
  const verdict = verdicts.find((v) => v.clientBidId === bid.clientBidId)

  if (verdict && !verdict.accepted) {
    return (
      <span className="pill bad" title={verdict.reason ?? undefined}>
        {verdict.reason ? (reasons[verdict.reason] ?? verdict.reason) : 'مرفوضة'}
      </span>
    )
  }

  if (price?.leaderIsYou && price.yourWinningBidId === bid.clientBidId) {
    return <span className="pill live">الأعلى</span>
  }

  if (price?.priceMinorUnits != null && price.priceMinorUnits >= bid.amount) {
    return <span className="pill done">تجاوزها غيرك</span>
  }

  return <span className="pill done">مُسجَّلة</span>
}


export function BidBox({ auction, session, price, verdicts, participant, onBid }: Props) {
  const key = useSigningKey(participant, auction.id, session.subject)
  const catcher = api({ baseUrl: config.catcherApi, session })

  const minimum = price?.minimumNextBidMinorUnits ?? auction.minimumNextBidMinorUnits
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [submitted, setSubmitted] = useState<Submitted[]>(() =>
    loadSubmitted(auction.id, session.subject),
  )
  useEffect(() => {
    saveSubmitted(auction.id, session.subject, submitted)
  }, [auction.id, session.subject, submitted])

  /// The log offset whose certificate is open, or null. An offset rather than the
  /// submitted row: the certificate is issued from the record, and the offset is
  /// the only thing this page holds that points at one.
  const [certificateFor, setCertificateFor] = useState<number | null>(null)

  // Keep the box prefilled with the cheapest winning bid, but never overwrite what
  // the bidder is typing — the price moves during a war and a box that resets
  // mid-keystroke costs them the auction.
  useEffect(() => {
    setText((current) => (current === '' ? riyals(minimum) : current))
  }, [minimum])

  const amount = parseRiyals(text)
  const tooLow = amount !== null && amount < minimum
  const increment = auction.minIncrementMinorUnits
  const current = price?.priceMinorUnits ?? null

  // One tap per raise, each showing the exact amount it sends. Built on the price
  // as it stands, so a press always bids above it: the first is the smallest raise
  // the rules allow, the others jump further for a bidder who wants to stop a war.
  const quick = [1, 2, 5].map((steps) => ({
    steps,
    amount: minimum + (steps - 1) * increment,
  }))

  // Said against the current price, not as a "minimum next bid" figure to compare
  // with: a bidder thinks "the price plus at least the increment", so that is what
  // the refusal says.
  const tooLowText = current === null
    ? `يجب ألا يقل المبلغ عن سعر الافتتاح ${sar(auction.openingPriceMinorUnits, 'ar')}.`
    : `يجب أن يزيد المبلغ على السعر الحالي بـ ${sar(increment, 'ar')} على الأقل.`
  // Only a lifecycle that has ended is closed. "Scheduled" is not Live either, and
  // treating it as closed told an eligible bidder their upcoming auction was over.
  const closed = price !== null && finishedStages.includes(price.status)
  const notStarted = price !== null && price.status === 'Scheduled'

  // B-04: the engine rejects a leader raising their own bid — it is almost always a
  // double-click and it costs the bidder money for nothing. The portal knows it is
  // leading (the BFF tells it, without naming anyone else), so this is refused here
  // rather than accepted with a 202 and discarded silently by the processor, which
  // is what happened before: the bid sat on screen marked "recorded" for ever.
  const alreadyLeading = price?.leaderIsYou === true

  const submit = async (value: number | null = amount) => {
    if (value === null) {
      setProblem('أدخل مبلغاً صحيحاً.')
      return
    }

    setBusy(true)
    setProblem(null)

    try {
      // Fetched on first use, held in memory only. See useSigningKey.
      const secretHex = await key.get()

      const clientBidId = newClientBidId()
      const frame = await buildBidFrame({
        auctionId: auction.id,
        // The bidder id in the frame must be this caller's own subject: the catcher
        // compares the two and refuses a mismatch with 403.
        bidderId: session.subject,
        amountMinorUnits: value,
        clientBidId,
        clientTimestampMs: Date.now(),
        nonce: newNonce(),
        signingSecretHex: secretHex,
      })

      const receipt = await catcher.postFrame<BidReceipt>('/bids', frame)

      // 202, not 200: recorded, not yet judged. The processor's verdict arrives
      // separately, which is why this says "recorded" and not "you are winning".
      setSubmitted((prior) => [
        { clientBidId, amount: value, offset: receipt.offset, at: new Date() },
        ...prior.slice(0, KEEP - 1),
      ])
      setText('')
      onBid()
    } catch (e) {
      if (e instanceof ApiError && e.reason) {
        setProblem(reasons[e.reason] ?? `رُفضت المزايدة: ${e.reason}`)
      } else {
        setProblem(e instanceof Error ? e.message : String(e))
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h2>المزايدة</h2>

      {closed ? (
        <div className="notice info">أُغلق المزاد — لا تُقبل مزايدات جديدة.</div>
      ) : notStarted ? (
        <div className="notice info">
          لم يبدأ المزاد بعد — تُفتح المزايدة في{' '}
          <span dir="rtl">{when(auction.startsAt)}</span>.
        </div>
      ) : (
        <>
          {problem && <div className="notice error">{problem}</div>}

          <div className="quick-raise" role="group" aria-label="زيادة سريعة">
            {quick.map((q) => (
              <button
                key={q.steps}
                className="quick-raise-btn"
                disabled={busy || alreadyLeading}
                aria-label={`مزايدة بـ ${riyals(q.amount)}`}
                onClick={() => void submit(q.amount)}
              >
                <span className="quick-raise-step">
                  {current !== null ? (
                    <span className="num">+{riyals(q.amount - current)}</span>
                  ) : q.amount === minimum ? (
                    'سعر الافتتاح'
                  ) : (
                    <>
                      <span className="num">+{riyals(q.amount - minimum)}</span> على الافتتاح
                    </>
                  )}
                </span>
                <span className="quick-raise-amount num">{sar(q.amount, 'ar')}</span>
              </button>
            ))}
          </div>

          <div className="row" style={{ alignItems: 'flex-end' }}>
            <label style={{ flex: '1 1 240px', marginBottom: 0 }}>
              <span>مبلغ آخر (ر.س)</span>
              <input
                className="ltr num"
                inputMode="decimal"
                value={text}
                aria-label="مبلغ المزايدة"
                onChange={(e) => {
                  setText(e.target.value)
                  setProblem(null)
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !busy && amount !== null && !tooLow) void submit()
                }}
              />
            </label>

            <button
              className="primary big"
              disabled={busy || amount === null || tooLow || alreadyLeading}
              onClick={() => void submit()}
            >
              {busy ? 'جارٍ الإرسال…' : 'إرسال المزايدة'}
            </button>
          </div>

          {tooLow && (
            <div className="small" style={{ color: 'var(--danger)', marginTop: 8 }}>
              {tooLowText}
            </div>
          )}

          {alreadyLeading && !tooLow && (
            <div className="small muted" style={{ marginTop: 8 }}>
              أنت الأعلى بالفعل — لا حاجة للمزايدة حتى يتجاوزك غيرك.
            </div>
          )}

          <p className="muted small" style={{ marginTop: 14 }}>
            تُوقَّع المزايدة في متصفحك بمفتاحك الخاص قبل إرسالها، فلا يستطيع أحد — ولا
            النظام نفسه — إرسال مزايدة باسمك.
          </p>
        </>
      )}

      {submitted.length > 0 && (
        <>
          <h3>مزايداتك في هذا المزاد</h3>
          <p className="muted small" style={{ marginTop: -6 }}>
            محفوظة على هذا الجهاز — لا تظهر إن دخلت من جهاز آخر.
          </p>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>المبلغ</th>
                  <th>الوقت</th>
                  <th>الترتيب في السجل</th>
                  <th>الحالة</th>
                  <th>الشهادة</th>
                </tr>
              </thead>
              <tbody>
                {submitted.map((b) => (
                  <tr key={b.clientBidId}>
                    <td className="num">{sar(b.amount, 'ar')}</td>
                    <td className="num small">{clock(b.at)}</td>
                    <td className="num small">{b.offset}</td>
                    <td className="small">{outcome(b, price, verdicts)}</td>
                    <td>
                      <button className="small" onClick={() => setCertificateFor(b.offset)}>
                        شهادة
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted small">
            «مُسجَّلة» تعني أن المزايدة حُفظت في السجل ولم يصل حكمها بعد. الحكم يصدر
            من خدمة المعالجة بترتيب السجل، لا بوقت جهازك.
          </p>
        </>
      )}

      {certificateFor !== null && (
        <Certificate
          client={catcher}
          auctionId={auction.id}
          auctionNameAr={auction.nameAr}
          offset={certificateFor}
          onClose={() => setCertificateFor(null)}
        />
      )}
    </div>
  )
}
