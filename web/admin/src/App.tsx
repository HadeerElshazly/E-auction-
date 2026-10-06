import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ApiError,
  api,
  config,
  has,
  Roles,
  takePendingAction,
  useSession,
  useStepUp,
} from '@eauction/shared'
import { authConfig } from './authConfig'
import type { Auction, AuctionListItem } from './types'
import { AuctionList } from './AuctionList'
import { AuctionEditor } from './AuctionEditor'
import { AwardPanel } from './AwardPanel'
import { ClerkTerminal } from './ClerkTerminal'
import { Reports } from './Reports'
import { AuditTrail } from './AuditTrail'
import { Monitor } from './Monitor'

/**
 * Which screen is open.
 *
 * Deliberately not a router. The portal is three screens for staff behind a login,
 * and adding react-router to get a URL per screen would buy a back button and cost
 * a dependency plus the redirect-URI registration every Keycloak client here
 * already pins to one path.
 */
type View = 'auctions' | 'monitor' | 'reports' | 'audit'


export function App() {
  const { session, loading, error: authError, signIn, signOut } = useSession(authConfig)
  const client = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])

  // Whether this load followed a second-factor confirmation. Not resumed
  // automatically: awarding land should be the result of someone pressing a
  // button, not of a redirect completing.
  const [confirmed] = useState(() => takePendingAction())

  const [auctions, setAuctions] = useState<AuctionListItem[]>([])
  const [selected, setSelected] = useState<Auction | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const isAdmin = has(session, Roles.auctionAdmin)
  const isCommittee = has(session, Roles.awardCommittee)
  const isClerk = has(session, Roles.operator)

  // التقارير are management information about work the first two already do, so
  // their roles open them as well as the dedicated one (§35). سجل المراجعة is the
  // opposite: `auditor` and nothing else, because an auditor who could also approve
  // an auction would be reading the record of their own actions (§34). An
  // administrator signing in will not see that tab, and that is the design working
  // rather than a missing permission.
  const canReport = has(session, Roles.reporting) || isAdmin || isCommittee
  const canAudit = has(session, Roles.auditor)

  // المتابعة المباشرة is operational: who is running auctions right now. The three
  // roles that run them see it. Not `reporting`, whose screens are all after the
  // fact, and emphatically not `auditor`, which reads the trail and nothing else
  // by design (§34).
  const canWatch = isAdmin || isCommittee || isClerk

  const [view, setView] = useState<View>('auctions')

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

  // Confirming an award requires a second factor confirmed in the last few
  // minutes. The runner turns the service's refusal into a confirmation the
  // committee member can complete, rather than a 403 they can do nothing about.
  const stepUp = useStepUp(authConfig)

  const act = useCallback(
    async (work: () => Promise<unknown>) => {
      setBusy(true)
      setError(null)
      try {
        await stepUp.run('admin-action', work)
        await reload()
      } catch (e) {
        setError(describe(e))
      } finally {
        setBusy(false)
      }
    },
    [reload, stepUp],
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

  if (!isAdmin && !isCommittee && !isClerk && !canReport && !canAudit) {
    return (
      <div className="centre">
        <div className="notice error">
          لا تملك صلاحية الدخول إلى هذه البوابة.
          <div className="small" style={{ marginTop: 8 }}>
            هذا الحساب لا يحمل أيًّا من الأدوار: <code>auction-admin</code>،{' '}
            <code>award-committee</code>، <code>operator</code>،{' '}
            <code>reporting</code>، <code>auditor</code>.
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
          {(isAdmin || isCommittee) && isClerk && ' + '}
          {isClerk && 'قاعة المزاد'}
          {has(session, Roles.reporting) && ' التقارير'}
          {canAudit && ' المراجعة'}
        </span>

        {isClerk && (
          // A clerk's own id, where they can read it out before they have been
          // assigned to anything. An administrator has to be given it to put them
          // on the floor, and nothing in this system can list municipal staff — so
          // a clerk who could only see it from inside an auction they are already
          // running could never be assigned to their first one.
          <span className="muted small ltr-id">
            معرّفك: <code className="ltr" data-testid="clerk-user-id">{session.subject}</code>
          </span>
        )}
        <button onClick={signOut}>خروج</button>
      </header>

      <div className="shell">
        {/* The navigation rail from the proposal. Always present, even for an
            account that holds only one of these — a single item still tells a
            reader where they are, and a rail that appears and disappears with the
            signed-in role makes the product look like two different products. */}
        <nav className="rail" data-testid="nav">
          <button
            className={view === 'auctions' ? 'on' : ''}
            data-testid="nav-auctions"
            onClick={() => setView('auctions')}
          >
            <span className="icon" aria-hidden="true">⌂</span>
            المزادات
            <span className="chevron" aria-hidden="true">‹</span>
          </button>
          {canWatch && (
            <button
              className={view === 'monitor' ? 'on' : ''}
              data-testid="nav-monitor"
              onClick={() => setView('monitor')}
            >
              <span className="icon" aria-hidden="true">◉</span>
              المتابعة المباشرة
              <span className="chevron" aria-hidden="true">‹</span>
            </button>
          )}
          {canReport && (
            <button
              className={view === 'reports' ? 'on' : ''}
              data-testid="nav-reports"
              onClick={() => setView('reports')}
            >
              <span className="icon" aria-hidden="true">◴</span>
              التقارير
              <span className="chevron" aria-hidden="true">‹</span>
            </button>
          )}
          {canAudit && (
            <button
              className={view === 'audit' ? 'on' : ''}
              data-testid="nav-audit"
              onClick={() => setView('audit')}
            >
              <span className="icon" aria-hidden="true">☰</span>
              سجل المراجعة
              <span className="chevron" aria-hidden="true">‹</span>
            </button>
          )}
        </nav>

        <main>
        {error && <div className="notice error">{error}</div>}

        {confirmed && (
          <div className="notice ok">
            تم التحقق من هويتك. يمكنك الآن إكمال الإجراء الذي كنت عليه.
          </div>
        )}

        {view === 'monitor' ? (
          <Monitor session={session} />
        ) : view === 'reports' ? (
          <Reports session={session} />
        ) : view === 'audit' ? (
          <AuditTrail session={session} />
        ) : !isAdmin && !isCommittee && !isClerk ? (
          // A reader with only `reporting` or `auditor` has no business on the
          // auction screens, and the services would refuse them anyway. Saying so
          // beats a list that fails to load.
          <div className="card">
            <h2>المزادات</h2>
            <p className="muted small">
              هذا الحساب للقراءة فقط. اختر التقارير أو سجل المراجعة من الأعلى.
            </p>
          </div>
        ) : selected ? (
          <>
            <div className="row" style={{ marginBottom: 14 }}>
              <button onClick={() => setSelected(null)}>← كل المزادات</button>
            </div>

            <AuctionEditor
              auction={selected}
              client={client}
              session={session}
              busy={busy}
              canEdit={isAdmin}
              canApprove={isCommittee}
              onAct={act}
            />

            {isClerk && selected.channel === 'Onsite' && (
              <ClerkTerminal
                auction={selected}
                session={session}
                client={client}
                onAct={act}
                busy={busy}
              />
            )}

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
        </main>
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
    // A step-up challenge is not an error the user can act on by reading it: the
    // runner has already sent them to confirm their identity, so by the time this
    // renders the browser is navigating away. Say what is happening rather than
    // showing a 403.
    if (e.needsStepUp) return 'يتطلب هذا الإجراء تأكيد هويتك — جارٍ التحويل…'

    if (e.problems.length > 1) return e.problems.join(' · ')
    if (e.status === 403) return 'هذا الإجراء يتطلب صلاحية أخرى (٤٠٣).'
    if (e.status === 401) return 'انتهت الجلسة. أعد تسجيل الدخول.'
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}
