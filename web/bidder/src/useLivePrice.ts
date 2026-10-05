import { useEffect, useRef, useState } from 'react'
import { api, config, type Session } from '@eauction/shared'
import type { LivePrice } from './types'

/**
 * The authoritative price, from the query BFF.
 *
 * Polled, because the push channel is not built yet (docs/ARCHITECTURE.md §7.2).
 * The shape here is the one a push channel would deliver unchanged, so the rest of
 * the portal does not know or care which it is — swapping polling for a socket means
 * replacing the body of this hook and nothing else.
 *
 * The interval is deliberately not constant. A bidding war is decided in the last
 * thirty seconds, and that is also when every bidder in the auction has the page
 * open, so the two pressures point in opposite directions: poll fast enough to be
 * useful at the close, slow enough not to be a self-inflicted load test on a
 * catalogue that is mostly idle.
 */
export function useLivePrice(auctionId: string, session: Session | null): LivePrice | null {
  const [price, setPrice] = useState<LivePrice | null>(null)
  const timer = useRef<number | null>(null)

  useEffect(() => {
    let cancelled = false
    const client = api({ baseUrl: config.queryApi, session })

    const tick = async () => {
      try {
        const next = await client.get<LivePrice>(`/auctions/${auctionId}/price`)
        if (!cancelled) setPrice(next)
        return next
      } catch {
        // A failed poll is not worth showing: the next one is a second away, and a
        // red banner flashing on and off during a bidding war is worse than a
        // price that is briefly a second stale.
        return null
      }
    }

    const schedule = (latest: LivePrice | null) => {
      if (cancelled) return
      timer.current = window.setTimeout(run, intervalFor(latest))
    }

    const run = async () => {
      schedule(await tick())
    }

    void run()

    return () => {
      cancelled = true
      if (timer.current !== null) window.clearTimeout(timer.current)
    }
  }, [auctionId, session])

  return price
}

function intervalFor(price: LivePrice | null): number {
  if (!price) return 3000
  if (price.status !== 'Live') return 15000

  const remaining = new Date(price.effectiveEndsAt).getTime() - Date.now()
  if (remaining < 60_000) return 1000
  if (remaining < 600_000) return 3000
  return 10_000
}
