import { useEffect, useMemo, useState } from 'react'
import { api, config, type Session } from '@eauction/shared'

/**
 * Who is winning, by name — for the people running the auction.
 *
 * The public views mask bidders (D-22); staff are the one audience that must see
 * the real identity: who leads a running auction, who won a closed one. The leader
 * comes from the same read model the bidders' screens use (QueryBff, by id, staff
 * only), and the name from the participant service, which is where names live.
 */

export interface StaffBidder {
  id: string
  nameAr: string | null
  phone: string | null
  email: string | null
}

// One cache for the whole portal: a name does not change between polls, and every
// card on the list asking for it separately would be a request per card per tick.
const known = new Map<string, StaffBidder>()
const pending = new Set<string>()
const listeners = new Set<() => void>()

async function resolve(session: Session, ids: string[]) {
  const missing = ids.filter((id) => !known.has(id) && !pending.has(id))
  if (missing.length === 0) return
  missing.forEach((id) => pending.add(id))
  try {
    const r = await api({ baseUrl: config.participantApi, session }).get<{ items: StaffBidder[] }>(
      `/staff/bidders?ids=${missing.join(',')}`,
    )
    r.items.forEach((b) => known.set(b.id, b))
    listeners.forEach((l) => l())
  } catch {
    // Unresolved ids show as ids; the next caller tries again.
  } finally {
    missing.forEach((id) => pending.delete(id))
  }
}

/** The named bidders for these ids, re-rendering as names arrive. */
export function useBidders(session: Session, ids: Array<string | null | undefined>) {
  const wanted = useMemo(
    () => [...new Set(ids.filter((x): x is string => !!x))].sort(),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [ids.join(',')],
  )
  const [, bump] = useState(0)
  useEffect(() => {
    const l = () => bump((n) => n + 1)
    listeners.add(l)
    return () => {
      listeners.delete(l)
    }
  }, [])
  useEffect(() => {
    void resolve(session, wanted)
  }, [session, wanted])
  return (id: string | null | undefined) => (id ? known.get(id) ?? null : null)
}

/** auctionId → the bidder id currently leading (or who led at the close). Polled. */
export function useLeaders(session: Session, pollMs = 3000): Record<string, string> {
  const [leaders, setLeaders] = useState<Record<string, string>>({})
  useEffect(() => {
    const client = api({ baseUrl: config.queryApi, session })
    let stop = false
    const read = () =>
      client
        .get<{ items: Array<{ auctionId: string; leaderBidderId: string }> }>('/staff/leaders')
        .then((r) => {
          if (stop) return
          setLeaders(Object.fromEntries(r.items.map((x) => [x.auctionId, x.leaderBidderId])))
        })
        .catch(() => undefined)
    void read()
    const t = window.setInterval(() => void read(), pollMs)
    return () => {
      stop = true
      window.clearInterval(t)
    }
  }, [session, pollMs])
  return leaders
}

/** A bidder by name, with the id on hover; the id itself until the name arrives. */
export function BidderName({ session, id }: { session: Session; id: string | null | undefined }) {
  const bidder = useBidders(session, [id])(id)
  if (!id) return <>—</>
  return bidder?.nameAr ? (
    <span title={id}>
      <strong>{bidder.nameAr}</strong>
      {bidder.phone && <span className="muted small"> · <span className="ltr">{bidder.phone}</span></span>}
    </span>
  ) : (
    <code className="small">{id}</code>
  )
}
