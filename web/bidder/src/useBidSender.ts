import { useMemo, useState } from 'react'
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
