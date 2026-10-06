import { useCallback, useEffect, useRef, useState } from 'react'
import { api, config, type Session } from '@eauction/shared'

interface Notice {
  id: string
  auctionId: string
  kind: string
  titleAr: string
  bodyAr: string
  actionable: boolean
  createdAt: string
  readAt: string | null
}

interface Props {
  session: Session
  /** Opens the auction a notice is about. */
  onOpen: (auctionId: string) => void
}

/**
 * The bell, and the list behind it.
 *
 * This is the delivered channel. SMS and push both need contracts that do not
 * exist (P-7), and the notification service says so plainly rather than pretending
 * — but a bidder who was outbid while their tab was closed still has to find out,
 * and this is where they do.
 *
 * It polls rather than streaming. The auction's own price has a dedicated SSE
 * stream because a second matters there; a notification is a thing you catch up on,
 * and a second connection held open per signed-in bidder for the whole session is a
 * real cost for no benefit.
 */
export function Notifications({ session, onOpen }: Props) {
  const [items, setItems] = useState<Notice[]>([])
  const [unread, setUnread] = useState(0)
  const [open, setOpen] = useState(false)
  const panel = useRef<HTMLDivElement>(null)

  const client = api({ baseUrl: config.notificationsApi, session })

  const load = useCallback(async () => {
    try {
      const page = await client.get<{ items: Notice[]; unread: number }>('/notifications?take=30')
      setItems(page.items)
      setUnread(page.unread)
    } catch {
      // A bell that cannot load is not worth an error banner over the auction a
      // bidder is actually looking at. It retries on the next tick.
    }
    // client is rebuilt each render from the same session; depending on it would
    // reschedule the interval every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session])

  useEffect(() => {
    void load()

    // Backed off while the tab is hidden, like the catalogue: a phone in a pocket
    // does not need a request every twenty seconds, and the notice is still there
    // when it comes back.
    const tick = () => {
      if (!document.hidden) void load()
    }

    const timer = window.setInterval(tick, 20_000)
    document.addEventListener('visibilitychange', tick)

    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', tick)
    }
  }, [load])

  // Closing on an outside click, because a panel that only closes by its own
  // button is a panel that covers the bid box.
  useEffect(() => {
    if (!open) return

    const close = (e: MouseEvent) => {
      if (!panel.current?.contains(e.target as Node)) setOpen(false)
    }

    // Deferred to the next frame: the click that opened the panel is still
    // propagating, and without this it closes immediately.
    const id = window.setTimeout(() => document.addEventListener('click', close), 0)

    return () => {
      window.clearTimeout(id)
      document.removeEventListener('click', close)
    }
  }, [open])

  const markRead = async (notice: Notice) => {
    if (notice.readAt === null) {
      try {
        await client.post(`/notifications/${notice.id}/read`)
      } catch {
        // Marking read is a convenience. Failing it must not stop the bidder
        // opening the auction the notice is about.
      }
    }

    if (notice.actionable) {
      setOpen(false)
      onOpen(notice.auctionId)
    }

    await load()
  }

  const markAllRead = async () => {
    try {
      await client.post('/notifications/read-all')
    } catch {
      /* same */
    }
    await load()
  }

  return (
    <div ref={panel} style={{ position: 'relative' }}>
      <button
        aria-label={unread > 0 ? `الإشعارات (${unread} غير مقروء)` : 'الإشعارات'}
        onClick={() => setOpen((was) => !was)}
      >
        الإشعارات
        {unread > 0 && (
          <span className="pill warn num" style={{ marginInlineStart: 6 }}>
            {unread}
          </span>
        )}
      </button>

      {open && (
        <div className="card notifications-panel">
          <div className="row" style={{ marginBottom: 8 }}>
            <strong>الإشعارات</strong>
            <span className="grow" />
            {unread > 0 && (
              <button className="small" onClick={() => void markAllRead()}>
                تعليم الكل كمقروء
              </button>
            )}
          </div>

          {items.length === 0 ? (
            <p className="muted small" style={{ margin: 0 }}>
              لا توجد إشعارات.
            </p>
          ) : (
            <ul className="notifications">
              {items.map((notice) => (
                <li key={notice.id} className={notice.readAt === null ? 'unread' : ''}>
                  <button
                    className="notice-row"
                    onClick={() => void markRead(notice)}
                    aria-label={notice.titleAr}
                  >
                    <span className="notice-title">{notice.titleAr}</span>
                    <span className="notice-body muted small">{notice.bodyAr}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}
