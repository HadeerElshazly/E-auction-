import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AppShell,
  ApiError,
  api,
  config,
  has,
  Roles,
  takePendingAction,
  useHashRoute,
  useSession,
  type NavItem,
} from '@eauction/shared'
import { authConfig } from './authConfig'
import type { AuctionDetail, AuctionSummary } from './types'
import { Catalogue } from './Catalogue'
import { AuctionPage } from './AuctionPage'
import { NotificationsBell, NotificationsPage, useNotices } from './Notifications'
import { LiveBids } from './LiveBids'
import { Profile } from './Profile'
import { MyApplications } from './MyApplications'
import { BiddingRoom } from './BiddingRoom'
import { MyInquiries } from './MyInquiries'

/**
 * The bidder portal. Every page has an address — `#catalog`, `#auction/<id>`,
 * `#auction/<id>/room`, `#applications` — so back, refresh and a shared link all
 * land where they should.
 */
export function App() {
  const { session, loading, error: authError, signIn, signOut } = useSession(authConfig)
  const [route, navigate] = useHashRoute('catalog')
  const [page, id, sub] = route

  // The catalogue is anonymous: a citizen browses before deciding to register.
  const publicClient = useMemo(() => api({ baseUrl: config.queryApi, session }), [session])

  const [auctions, setAuctions] = useState<AuctionSummary[]>([])
  const [openAuction, setOpenAuction] = useState<AuctionDetail | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Whether this page load followed a second-factor confirmation.
  //
  // Read once, on mount, because takePendingAction clears it. The action is NOT
  // resumed automatically: a payment should be the result of someone pressing a
  // button, not of a redirect completing, and a user who confirmed their identity
  // for one purpose has not thereby agreed to whatever was pending.
  const [confirmed] = useState(() => takePendingAction())

  const isBidder = has(session, Roles.bidder)
  const notices = useNotices(isBidder ? session : null)

  const load = useCallback(async () => {
    try {
      const result = await publicClient.get<{ items: AuctionSummary[] }>('/auctions')
      setAuctions(result.items)
      setError(null)
    } catch (e) {
      setError(describe(e))
    }
  }, [publicClient])

  // The auction in the address, read whenever the address names a different one.
  const auctionId = page === 'auction' ? id : undefined
  // The auction the address names that turned out not to exist.
  const [missing, setMissing] = useState<string | null>(null)
  const loadAuction = useCallback(
    async (target: string) => {
      try {
        setOpenAuction(await publicClient.get<AuctionDetail>(`/auctions/${target}`))
        setError(null)
        setMissing(null)
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) setMissing(target)
        else setError(describe(e))
      }
    },
    [publicClient],
  )
  useEffect(() => {
    if (!auctionId) return
    if (openAuction?.id !== auctionId) setOpenAuction(null)
    void loadAuction(auctionId)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auctionId, loadAuction])

  // The catalogue refreshes on its own.
  //
  // Fetching once on mount is wrong for a page whose whole subject moves: an
  // auction going live, a price rising, a sale closing. Only while the tab is
  // visible: a catalogue left open in a background tab for a week should not keep
  // polling, and every bidder in an auction has this page open at once.
  useEffect(() => {
    void load()
    let timer: number | null = null
    const schedule = () => {
      if (timer !== null) window.clearTimeout(timer)
      timer = window.setTimeout(tick, document.hidden ? 60_000 : 10_000)
    }
    const tick = async () => {
      if (!document.hidden) await load()
      schedule()
    }
    const onVisibility = () => {
      if (!document.hidden) void load()
      schedule()
    }
    schedule()
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      if (timer !== null) window.clearTimeout(timer)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [load])

  if (loading) return <div className="centre muted">…</div>

  const open = (target: string) => navigate(`auction/${target}`)
  const openRoom = (target: string) => navigate(`auction/${target}/room`)

  // The workspace this person has. A visitor sees the catalogue; a bidder their own
  // pages as well.
  const nav: NavItem[] = [
    { to: 'catalog', label: 'المزادات', icon: 'grid' },
    ...(isBidder
      ? [
          { to: 'live', label: 'مزاداتي الجارية', icon: 'gavel' },
          { to: 'applications', label: 'مشاركاتي', icon: 'file' },
          { to: 'notifications', label: 'الإشعارات', icon: 'bell', badge: notices.unread },
          { to: 'questions', label: 'الاستفسارات', icon: 'message' },
          { to: 'profile', label: 'الملف الشخصي', icon: 'user' },
        ]
      : []),
  ]
  const active = page === 'auction' ? 'catalog' : page ?? 'catalog'
  const crumb =
    page === 'auction'
      ? openAuction?.nameAr ?? 'تفاصيل المزاد'
      : nav.find((n) => n.to === active)?.label ?? 'المزادات'

  let body: React.ReactNode
  if (page === 'live' && session && isBidder) {
    body = <LiveBids session={session} auctions={auctions} onOpenRoom={openRoom} onBack={() => navigate('catalog')} />
  } else if (page === 'applications' && session && isBidder) {
    body = (
      <MyApplications
        session={session}
        auctions={auctions}
        onOpen={open}
        onOpenRoom={openRoom}
        onBack={() => navigate('catalog')}
      />
    )
  } else if (page === 'notifications' && session && isBidder) {
    body = <NotificationsPage notices={notices} onOpen={open} />
  } else if (page === 'questions' && session && isBidder) {
    body = <MyInquiries session={session} auctions={auctions} onOpen={open} />
  } else if (page === 'profile' && session && isBidder) {
    body = <Profile session={session} onBack={() => navigate('catalog')} />
  } else if (page === 'auction' && id) {
    if (missing === id) {
      body = (
          // A link to an auction that is not there — an old bookmark, or one from
          // before the data was reset — says so, rather than a 404 and a spinner.
          <div className="card empty-state" data-testid="auction-not-found">
            <h3>المزاد غير موجود</h3>
            <p className="muted">ربما حُذف أو أن الرابط قديم. اختر المزاد من القائمة.</p>
            <button className="primary" onClick={() => navigate('catalog')}>
              جميع المزادات
            </button>
          </div>
        )
    } else if (!openAuction || openAuction.id !== id) {
      body = <p className="muted">…</p>
    } else if (sub === 'room' && session) {
      body = (
        <BiddingRoom
          auction={openAuction}
          session={session}
          onBack={() => navigate(`auction/${id}`)}
          // A quiet refresh: re-reading the auction must not leave the room, or every
          // bid would drop the bidder out of the screen they bid from.
          onRefresh={() => void loadAuction(id)}
        />
      )
    } else {
      body = (
        <AuctionPage
          auction={openAuction}
          session={session}
          canBid={isBidder}
          onBack={() => {
            navigate('catalog')
            void load()
          }}
          onSignIn={signIn}
          onRefresh={() => loadAuction(id)}
          onEnterRoom={() => openRoom(id)}
        />
      )
    }
  } else {
    body = <Catalogue auctions={auctions} onOpen={open} session={session} />
  }

  return (
    <AppShell
      brand="مزادات الأراضي"
      brandSub="منصة طرح المخططات"
      nav={nav}
      active={active}
      crumb={crumb}
      session={session}
      onSignIn={signIn}
      signInLabel="الدخول بنفاذ"
      onSignOut={signOut}
      tools={isBidder && <NotificationsBell unread={notices.unread} />}
      sidebarFoot={
        <>
          <div>منصة الأمانة</div>
          <strong>مزادات الأراضي</strong>
          <div className="sidebar-foot">
            الإصدار الأول <span className="pill teal plain">تجريبي</span>
          </div>
        </>
      }
      footer="جميع المزادات المعروضة خاضعة لكراسة الشروط المعتمدة لكل مزاد."
    >
      {authError && <div className="notice error">{authError}</div>}
      {error && <div className="notice error">{error}</div>}

      {confirmed && (
        <div className="notice ok">تم التحقق من هويتك. يمكنك الآن إكمال الخطوة التي كنت عليها.</div>
      )}

      {session && !isBidder && (
        <div className="notice info staff-redirect">
          <span className="grow">
            هذا حساب موظف، وهذه بوابة المزايدين — يمكنك مشاهدة المزادات فقط. التقارير وسجل المراجعة
            وإدارة المزادات في بوابة الموظفين.
          </span>
          <a className="button primary" href={staffPortalUrl()}>
            فتح بوابة الموظفين ←
          </a>
        </div>
      )}

      {body}
    </AppShell>
  )
}

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    // A step-up challenge is not an error the user can act on by reading it: the
    // runner has already sent them to confirm their identity, so by the time this
    // renders the browser is navigating away.
    if (e.needsStepUp) return 'يتطلب هذا الإجراء تأكيد هويتك — جارٍ التحويل…'
    if (e.status === 401) return 'انتهت الجلسة. أعد تسجيل الدخول.'
    if (e.problems.length > 0) return e.problems.join(' · ')
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}

/** The staff portal: set at build, else the same host on its default port. */
function staffPortalUrl(): string {
  const configured = import.meta.env.VITE_STAFF_PORTAL as string | undefined
  return configured || `${window.location.protocol}//${window.location.hostname}:3001/`
}
