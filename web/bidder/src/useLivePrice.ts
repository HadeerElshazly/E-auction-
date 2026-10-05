import { useEffect, useRef, useState } from 'react'
import { api, config, readEventStream, type Session } from '@eauction/shared'
import type { BidVerdict, LivePrice } from './types'

export interface LiveFeed {
  price: LivePrice | null
  /** Verdicts for this bidder's own bids, newest first. */
  verdicts: BidVerdict[]
  /** 'stream' while pushed, 'polling' when degraded, 'connecting' before either. */
  transport: 'connecting' | 'stream' | 'polling'
}

/**
 * The authoritative price and this bidder's own verdicts.
 *
 * Pushed, with polling as the fallback rather than the design. The reason is load:
 * at the platform's target of 10,000 concurrent bidders, a one-second poll through
 * the last minute of an auction is ~10,000 requests a second on the query BFF — the
 * same order as the bid load itself — to learn a number that changed perhaps fifty
 * times. The stream costs one message per actual change.
 *
 * Polling stays because a stream can be defeated by things outside this code: a
 * corporate proxy that buffers responses, a mobile network that kills long
 * connections, a misconfigured ingress. Degrading to a slow poll is visibly worse
 * than a stream and much better than a dead price panel, and `transport` says which
 * is in use so the portal can admit it.
 */
export function useLivePrice(auctionId: string, session: Session | null): LiveFeed {
  const [price, setPrice] = useState<LivePrice | null>(null)
  const [verdicts, setVerdicts] = useState<BidVerdict[]>([])
  const [transport, setTransport] = useState<LiveFeed['transport']>('connecting')

  // Whether the stream has ever delivered. Polling only starts if it has not, so a
  // working stream never doubles up with a poll.
  const streaming = useRef(false)

  useEffect(() => {
    const controller = new AbortController()
    streaming.current = false
    setTransport('connecting')

    void readEventStream({
      url: `${config.queryApi}/auctions/${auctionId}/stream`,
      token: session?.accessToken,
      signal: controller.signal,
      onOpen: () => {
        streaming.current = true
        setTransport('stream')
      },
      onError: () => {
        streaming.current = false
        // Not 'polling' yet: the reader is about to retry, and flipping the label
        // on every blip would make a healthy stream look broken.
      },
      onEvent: (event, data) => {
        try {
          if (event === 'snapshot' || event === 'price') {
            setPrice(JSON.parse(data) as LivePrice)
          } else if (event === 'verdict') {
            const verdict = JSON.parse(data) as BidVerdict
            setVerdicts((prior) =>
              // The server replays recent verdicts on reconnect, so the same one
              // can arrive twice. The client bid id is what makes it the same one.
              prior.some((v) => v.clientBidId === verdict.clientBidId)
                ? prior
                : [verdict, ...prior].slice(0, 20),
            )
          }
        } catch {
          // A frame this client cannot parse is a frame from a newer server. Drop
          // it rather than tearing down a working stream.
        }
      },
    })

    return () => controller.abort()
  }, [auctionId, session])

  // The fallback. Runs only while the stream is not delivering.
  useEffect(() => {
    const client = api({ baseUrl: config.queryApi, session })
    let cancelled = false
    let timer: number | null = null
    let latest: LivePrice | null = null

    const tick = async () => {
      if (cancelled) return

      if (streaming.current) {
        // The stream is working; check again later in case it drops.
        timer = window.setTimeout(tick, 5000)
        return
      }

      try {
        latest = await client.get<LivePrice>(`/auctions/${auctionId}/price`)
        if (!cancelled) {
          setPrice(latest)
          setTransport('polling')
        }
      } catch {
        // A failed poll is not worth showing: the next one is seconds away, and a
        // banner flashing on and off during a bidding war is worse than a price a
        // moment stale.
      }

      if (!cancelled) timer = window.setTimeout(tick, pollInterval(latest))
    }

    // A short grace period, so a stream that is about to connect is not raced by
    // a poll on every page load.
    timer = window.setTimeout(tick, 2000)

    return () => {
      cancelled = true
      if (timer !== null) window.clearTimeout(timer)
    }
  }, [auctionId, session])

  return { price, verdicts, transport }
}

/**
 * How often to poll when the stream is unavailable.
 *
 * Still tightening towards the close — a degraded client in a bidding war needs the
 * price more than an idle one does — but this is the path the fan-out exists to
 * avoid, so it is deliberately slower than the one-second poll it replaced.
 */
function pollInterval(price: LivePrice | null): number {
  if (!price) return 5000
  if (price.status !== 'Live') return 20_000

  const remaining = new Date(price.effectiveEndsAt).getTime() - Date.now()
  return remaining < 120_000 ? 2000 : 10_000
}
