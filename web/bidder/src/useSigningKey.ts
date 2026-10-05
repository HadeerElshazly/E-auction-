import { useCallback, useRef, useState } from 'react'
import type { Api } from '@eauction/shared'
import type { SigningKey } from './types'

/**
 * The bidder's signing key, held in memory for as long as the tab is open.
 *
 * This is the most dangerous value in the portal: it signs bids, and a bid signed
 * with it is indistinguishable from one the bidder made. So:
 *
 *   - It lives in a ref, never in state that could end up in a devtools snapshot of
 *     a rendered tree, and never in localStorage or sessionStorage.
 *   - It is fetched once per auction, on the first bid, not at page load — a tab left
 *     open on the catalogue never holds one.
 *   - `forget()` drops it, and signing out drops it with the rest of the session.
 *
 * It is still a secret in a browser's heap, which is a real exposure: an XSS on this
 * page can bid as the user for as long as the tab lives. That is the cost of the
 * bidder holding their own key, and the alternative — the server signing on their
 * behalf — would mean no bid could ever be attributed to the bidder rather than to
 * the platform. The mitigation is the usual one: no innerHTML, no eval, a strict CSP
 * in front of this bundle, and a key scoped to one auction so the blast radius of a
 * compromise is one auction's bidding.
 */
export function useSigningKey(client: Api, auctionId: string, bidderId: string) {
  const key = useRef<{ auctionId: string; secretHex: string; epoch: number } | null>(null)
  const [held, setHeld] = useState(false)

  const get = useCallback(async (): Promise<string> => {
    if (key.current?.auctionId === auctionId) return key.current.secretHex

    const fetched = await client.get<SigningKey>(
      `/auctions/${auctionId}/subscriptions/${bidderId}/signing-key`,
    )
    key.current = {
      auctionId,
      secretHex: fetched.secretHex,
      epoch: fetched.keyEpoch,
    }
    setHeld(true)
    return fetched.secretHex
  }, [client, auctionId, bidderId])

  const forget = useCallback(() => {
    key.current = null
    setHeld(false)
  }, [])

  return { get, forget, held, epoch: key.current?.epoch ?? null }
}
