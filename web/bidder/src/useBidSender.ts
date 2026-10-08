import { useEffect, useMemo, useState } from 'react'
import {
  ApiError,
  api,
  buildBidFrame,
  config,
  newClientBidId,
  newNonce,
  type Api,
  type Session,
} from '@eauction/shared'
import { reasons } from './reasons'
import type { BidReceipt } from './types'
import { useSigningKey } from './useSigningKey'

export interface SentBid {
  clientBidId: string
  amount: number
  offset: number
  at: Date
}

// One open connection to the catcher, shared by every bid box on the page. Opening
// a connection is the slow part of a first bid (seconds, on some networks); a small
// unauthenticated GET now and then keeps one ready, so the click pays only the bid.
let warmers = 0
let warmTimer: number | undefined
const pingCatcher = () => void fetch(`${config.catcherApi}/health/live`, { cache: 'no-store' }).catch(() => undefined)
function warmCatcher(): () => void {
  if (warmers++ === 0) {
    pingCatcher()
    warmTimer = window.setInterval(pingCatcher, 45_000)
  }
  return () => {
    if (--warmers === 0) window.clearInterval(warmTimer)
  }
}

/**
 * Signs and sends one bid — the whole of it, in one place for every screen that
 * bids: the bidding screen and the cards on «مزاداتي الجارية».
 *
 * The frame is signed in the browser with the bidder's own derived key, fetched on
 * first use and held in memory only (see useSigningKey). The catcher answers 202:
 * recorded, not yet judged; the processor's verdict arrives separately.
 */
export function useBidSender(auctionId: string, session: Session, participant: Api) {
  const key = useSigningKey(participant, auctionId, session.subject)
  const catcher = useMemo(() => api({ baseUrl: config.catcherApi, session }), [session])
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // Only mounted where this bidder may bid now, so get ready before the first click:
  // the signing key in hand and the catcher's connection open.
  const getKey = key.get
  useEffect(() => {
    void getKey().catch(() => undefined)
    return warmCatcher()
  }, [getKey])

  const send = async (amountMinorUnits: number): Promise<SentBid | null> => {
    setBusy(true)
    setProblem(null)
    try {
      const secretHex = await key.get()
      const clientBidId = newClientBidId()
      const frame = await buildBidFrame({
        auctionId,
        // Must be this caller's own subject: the catcher refuses a mismatch with 403.
        bidderId: session.subject,
        amountMinorUnits,
        clientBidId,
        clientTimestampMs: Date.now(),
        nonce: newNonce(),
        signingSecretHex: secretHex,
      })
      const receipt = await catcher.postFrame<BidReceipt>('/bids', frame)
      return { clientBidId, amount: amountMinorUnits, offset: receipt.offset, at: new Date() }
    } catch (e) {
      setProblem(
        e instanceof ApiError && e.reason
          ? (reasons[e.reason] ?? `رُفضت المزايدة: ${e.reason}`)
          : e instanceof Error
            ? e.message
            : String(e),
      )
      return null
    } finally {
      setBusy(false)
    }
  }

  return { send, busy, problem, setProblem, catcher }
}
