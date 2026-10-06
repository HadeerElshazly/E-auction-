import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ApiError,
  api,
  config,
  has,
  Roles,
  takePendingAction,
  useSession,
} from '@eauction/shared'
import { authConfig } from './authConfig'
import type { AuctionDetail, AuctionSummary } from './types'
import { Catalogue } from './Catalogue'
import { AuctionPage } from './AuctionPage'
import { Notifications } from './Notifications'


export function App() {
  const { session, loading, error: authError, signIn, signOut } = useSession(authConfig)

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

  const load = useCallback(async () => {
    try {
      const page = await publicClient.get<{ items: AuctionSummary[] }>('/auctions')
      setAuctions(page.items)
      setError(null)
    } catch (e) {
      setError(describe(e))
    }
  }, [publicClient])

  const open = useCallback(
    async (id: string) => {
      try {
        setOpenAuction(await publicClient.get<AuctionDetail>(`/auctions/${id}`))
        setError(null)
      } catch (e) {
        setError(describe(e))
      }
    },
    [publicClient],
  )

  // The catalogue refreshes on its own.
  //
  // Fetching once on mount is wrong for a page whose whole subject moves: an
  // auction going live, a price rising, a sale closing. It also made the page
  // permanently wrong if the query BFF was still replaying its topics when the page
  // first loaded — the citizen was told there were no auctions and nothing ever
  // corrected it.
  //
  // Only while the tab is visible: a catalogue left open in a background tab for a
  // week should not keep polling, and every bidder in an auction has this page open
  // at once.
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
      // Coming back to the tab should show the truth at once, not in ten seconds.
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

  const isBidder = has(session, Roles.bidder)

  return (
    <>
      <header className="bar">
        <h1>مزادات الأراضي</h1>
        <span className="grow" />
        {session ? (
          <>
            {isBidder && <Notifications session={session} onOpen={(id) => void open(id)} />}
            <span className="who">
              {session.nameAr ?? session.name}
              {session.nationalId && <span className="ltr"> · {session.nationalId}</span>}
            </span>
            <button onClick={signOut}>خروج</button>
          </>
        ) : (
          <button className="primary" onClick={signIn}>
            الدخول بنفاذ
          </button>
        )}
      </header>

      <div className="app">
        {authError && <div className="notice error">{authError}</div>}
        {error && <div className="notice error">{error}</div>}

        {confirmed && (
          <div className="notice ok">
            تم التحقق من هويتك. يمكنك الآن إكمال الخطوة التي كنت عليها.
          </div>
        )}

        {session && !isBidder && (
          <div className="notice info">
            هذا الحساب لا يحمل دور <code>bidder</code>، فلا يمكنه المزايدة. المزادات
            معروضة للعرض فقط.
          </div>
        )}

        {openAuction ? (
          <AuctionPage
            auction={openAuction}
            session={session}
            canBid={isBidder}
            onBack={() => {
              setOpenAuction(null)
              void load()
            }}
            onSignIn={signIn}
            onRefresh={() => open(openAuction.id)}
          />
        ) : (
          <Catalogue auctions={auctions} onOpen={open} signedIn={session !== null} />
        )}
      </div>
    </>
  )
}

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    // A step-up challenge is not an error the user can act on by reading it: the
    // runner has already sent them to confirm their identity, so by the time this
    // renders the browser is navigating away. Say what is happening rather than
    // showing a 403.
    if (e.needsStepUp) return 'يتطلب هذا الإجراء تأكيد هويتك — جارٍ التحويل…'

    if (e.status === 401) return 'انتهت الجلسة. أعد تسجيل الدخول.'
    if (e.problems.length > 0) return e.problems.join(' · ')
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}
