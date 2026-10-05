import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, api, config, has, Roles, useSession } from '@eauction/shared'
import type { Auction, AuctionListItem } from './types'
import { AuctionList } from './AuctionList'
import { AuctionEditor } from './AuctionEditor'
import { AwardPanel } from './AwardPanel'

const authConfig = {
  issuer: config.issuer,
  clientId: 'admin-web',
  redirectUri: window.location.origin + '/',
}

export function App() {
  const { session, loading, error: authError, signIn, signOut } = useSession(authConfig)
  const client = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])

  const [auctions, setAuctions] = useState<AuctionListItem[]>([])
  const [selected, setSelected] = useState<Auction | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const isAdmin = has(session, Roles.auctionAdmin)
  const isCommittee = has(session, Roles.awardCommittee)

  const refreshList = useCallback(async () => {
    if (!session) return
    try {
      const page = await client.get<{ items: AuctionListItem[] }>('/auctions')
      setAuctions(page.items)
    } catch (e) {
      setError(describe(e))
    }
  }, [client, session])

  const open = useCallback(
    async (id: string) => {
      setError(null)
      try {
        setSelected(await client.get<Auction>(`/auctions/${id}`))
      } catch (e) {
        setError(describe(e))
      }
    },
    [client],
  )

  // Reload the open auction after anything that changes it. The award workflow runs
  // partly through Kafka — the processor's CandidateOffered arrives on
  // auctions.lifecycle — so the server's copy is the only one worth trusting.
  const reload = useCallback(async () => {
    if (selected) await open(selected.id)
    await refreshList()
  }, [open, refreshList, selected])

  const act = useCallback(
    async (work: () => Promise<unknown>) => {
      setBusy(true)
      setError(null)
      try {
        await work()
        await reload()
      } catch (e) {
        setError(describe(e))
      } finally {
        setBusy(false)
      }
    },
    [reload],
  )

  useEffect(() => {
    void refreshList()
  }, [refreshList])

  // The open auction refreshes itself while it is in a state only the processor can
  // move it out of.
  //
  // Live, PendingEligibilityReview and PendingAward all change without this portal
  // doing anything: the processor opens the auction, closes it, and offers a
  // candidate over auctions.lifecycle. A committee member watching a closed auction
  // for its candidate would otherwise wait on a page that had already stopped
  // asking, and would have to know to reload.
  const watching = selected !== null
    && ['Scheduled', 'Live', 'PendingEligibilityReview', 'PendingAward'].includes(selected.status)
  const watchedId = watching ? selected.id : null

  useEffect(() => {
    if (watchedId === null) return

    const timer = window.setInterval(() => {
      // Not while a mutation is in flight: reloading underneath one would replace
      // the form's auction while the clerk is mid-edit.
      if (!busy && !document.hidden) void open(watchedId)
    }, 5000)

    return () => window.clearInterval(timer)
  }, [watchedId, busy, open])

  if (loading) return <div className="centre muted">…</div>

  if (!session) {
    return (
      <div className="centre">
        <h1>إدارة المزادات</h1>
        <p className="muted">بوابة إدارة المزادات ولجنة الترسية</p>
        {authError && <div className="notice error">{authError}</div>}
        <button className="primary big" onClick={signIn}>
          تسجيل الدخول
        </button>
      </div>
    )
  }

  if (!isAdmin && !isCommittee) {
    return (
      <div className="centre">
        <div className="notice error">
          لا تملك صلاحية الدخول إلى هذه البوابة.
          <div className="small" style={{ marginTop: 8 }}>
            هذا الحساب لا يحمل دور <code>auction-admin</code> ولا{' '}
            <code>award-committee</code>.
          </div>
        </div>
        <button onClick={signOut}>تسجيل الخروج</button>
      </div>
    )
  }

  return (
    <>
      <header className="bar">
        <h1>إدارة المزادات</h1>
        <span className="grow" />
        <span className="who">
          {session.name}
          {' · '}
          {isAdmin && 'إدارة'}
          {isAdmin && isCommittee && ' + '}
          {isCommittee && 'لجنة الترسية'}
        </span>
        <button onClick={signOut}>خروج</button>
      </header>

      <div className="app">
        {error && <div className="notice error">{error}</div>}

        {selected ? (
          <>
            <div className="row" style={{ marginBottom: 14 }}>
              <button onClick={() => setSelected(null)}>← كل المزادات</button>
            </div>

            <AuctionEditor
              auction={selected}
              client={client}
              busy={busy}
              canEdit={isAdmin}
              canApprove={isCommittee}
              onAct={act}
            />

            <AwardPanel
              auction={selected}
              client={client}
              busy={busy}
              canAct={isCommittee}
              committeeUserId={session.subject}
              onAct={act}
            />
          </>
        ) : (
          <AuctionList
            auctions={auctions}
            canCreate={isAdmin}
            busy={busy}
            onOpen={open}
            onCreate={(nameAr, nameEn) =>
              act(async () => {
                const created = await client.post<Auction>('/auctions', {
                  createdByUserId: session.subject,
                  nameAr,
                  nameEn,
                })
                setSelected(created)
              })
            }
          />
        )}
      </div>
    </>
  )
}

/**
 * Turns a failure into something a civil servant can act on. The services answer
 * with `problems` (validation) or `reason` (a refusal), and showing those verbatim
 * is the difference between "the form is wrong" and "the system is broken".
 */
function describe(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.problems.length > 1) return e.problems.join(' · ')
    if (e.status === 403) return 'هذا الإجراء يتطلب صلاحية أخرى (٤٠٣).'
    if (e.status === 401) return 'انتهت الجلسة. أعد تسجيل الدخول.'
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}
