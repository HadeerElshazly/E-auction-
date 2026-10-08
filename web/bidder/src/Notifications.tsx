import { useCallback, useEffect, useMemo, useState } from 'react'
import { BellIcon, Icon, PageHead, Pager, api, config, timestamp, usePage, type Session } from '@eauction/shared'

export interface Notice {
  id: string
  auctionId: string
  kind: string
  titleAr: string
  bodyAr: string
  actionable: boolean
  createdAt: string
  readAt: string | null
}

/**
 * The bidder's notices, polled — one source for the bell, the sidebar's count and
 * the page.
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
export function useNotices(session: Session | null) {
  const client = useMemo(
    () => (session ? api({ baseUrl: config.notificationsApi, session }) : null),
    [session],
  )
  const [items, setItems] = useState<Notice[]>([])
  const [unread, setUnread] = useState(0)
  const [total, setTotal] = useState(0)
  // The page «الإشعارات» is on; the service cuts it. The bell needs only the count.
  const paging = usePage()

  const load = useCallback(async () => {
    if (!client) return
    try {
      const page = await client.get<{ items: Notice[]; unread: number; total: number }>(
        `/notifications?${paging.query}`,
      )
      setItems(page.items)
      setUnread(page.unread)
      setTotal(page.total)
    } catch {
      // A bell that cannot reach its service shows what it last knew.
    }
  }, [client, paging.query])

  useEffect(() => {
    void load()
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

  const markRead = useCallback(
    async (notice: Notice) => {
      if (client && notice.readAt === null) {
        try {
          await client.post(`/notifications/${notice.id}/read`)
        } catch {
          // Read or not, the bidder has seen it.
        }
      }
      await load()
    },
    [client, load],
  )

  const markAllRead = useCallback(async () => {
    if (!client) return
    try {
      await client.post('/notifications/read-all')
    } catch {
      /* same */
    }
    await load()
  }, [client, load])

  return { items, unread, markRead, markAllRead, total, paging }
}

/** The bell in the top bar: the unread count, and the way to the page. */
export function NotificationsBell({ unread }: { unread: number }) {
  return (
    <a
      className="icon-btn"
      href="#notifications"
      aria-label={unread > 0 ? `الإشعارات (${unread} غير مقروء)` : 'الإشعارات'}
      title="الإشعارات"
    >
      <BellIcon />
      {unread > 0 && (
        <span className="icon-badge num" aria-hidden="true">
          {unread > 9 ? '9+' : unread}
        </span>
      )}
    </a>
  )
}

/** الإشعارات — every notice, newest first, each opening its auction. */
export function NotificationsPage({
  notices,
  onOpen,
}: {
  notices: ReturnType<typeof useNotices>
  onOpen: (auctionId: string) => void
}) {
  const { items, unread, markRead, markAllRead, total, paging } = notices
  return (
    <>
      <PageHead
        eyebrow="مساحة المزايد"
        title="الإشعارات"
        sub="إشعارات المشاركة والمزايدات وقرارات الترسية."
        action={
          unread > 0 && (
            <button onClick={() => void markAllRead()}>تعليم الكل كمقروء</button>
          )
        }
      />
      <div className="card">
        {items.length === 0 ? (
          <div className="empty-state">
            <h3>أنت على اطلاع</h3>
            <p className="muted">ستظهر هنا إشعارات نشاطك في المزادات.</p>
          </div>
        ) : (
          items.map((n) => (
            <div key={n.id} className={`notice-item${n.readAt === null ? ' unread' : ''}`}>
              <div className="notice-icon">
                <Icon name="bell" size={18} />
              </div>
              <div className="grow">
                <h3>{n.titleAr}</h3>
                <p>{n.bodyAr}</p>
                <small className="muted">{timestamp(n.createdAt)}</small>
              </div>
              {n.actionable && (
                <button
                  className="ghost small"
                  onClick={() => {
                    void markRead(n)
                    onOpen(n.auctionId)
                  }}
                >
                  المزاد
                </button>
              )}
              {!n.actionable && n.readAt === null && (
                <button className="ghost small" onClick={() => void markRead(n)}>
                  مقروء
                </button>
              )}
            </div>
          ))
        )}
        <Pager page={paging.page} total={total} noun="إشعار" onPage={paging.setPage} />
      </div>
    </>
  )
}
