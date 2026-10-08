import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AppShell,
  PageHead,
  useHashRoute,
  type NavItem,
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
import { Reports } from './Reports'
import { AuditTrail } from './AuditTrail'
import { FollowUp } from './FollowUp'
import { Inquiries } from './Inquiries'
import { ApplicationsQueue } from './ApplicationsQueue'
import { Committee } from './Committee'
import { BidLogPage } from './AuditViews'
import { AuctionDetail } from './AuctionDetail'
import { Monitor } from './Monitor'
import { VisitorSettings } from './VisitorSettings'

/**
 * Which screen is open.
 *
 * Deliberately not a router. The portal is three screens for staff behind a login,
 * and adding react-router to get a URL per screen would buy a back button and cost
 * a dependency plus the redirect-URI registration every Keycloak client here
 * already pins to one path.
 */


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
  // The inquiries desk: its own role, so administrators and the committee are not it.
  const canInquire = has(session, Roles.inquiries)

  // المتابعة المباشرة is operational: who is running auctions right now. The three
  // roles that run them see it. Not `reporting`, whose screens are all after the
  // fact, and emphatically not `auditor`, which reads the trail and nothing else
  // by design (§34).
  const canWatch = isAdmin || isCommittee || isClerk

  // Every page has an address: back, refresh and a shared link land where they should.
  const [route, navigate] = useHashRoute('auctions')
  const [view, routeId] = route

  // Where each person starts. A reader lands on their own screen, not on an auction
  // list they cannot use: the auditor on سجل المراجعة, reporting on التقارير, the
  // inquiries desk on الاستفسارات. A committee member back from the second-factor
  // round trip lands where the decision was being made.
  const readerOnly = !!session && !isAdmin && !isCommittee && !isClerk
  useEffect(() => {
    if (!session) return
    if (confirmed === 'followup-decision') navigate('committee')
    else if (window.location.hash) return
    else if (readerOnly)
      navigate(canInquire ? 'inquiries' : canAudit ? 'audit' : canReport ? 'reports' : 'auctions')
    // The committee's work is decisions: it starts on its own page.
    else if (isCommittee && !isAdmin) navigate('committee')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session, readerOnly])

  const refreshList = useCallback(async () => {
    if (!session) return
    try {
      const page = await client.get<{ items: AuctionListItem[] }>('/auctions')
      setAuctions(page.items)
    } catch (e) {
      setError(describe(e))
    }
  }, [client, session])

  // The auction the address names that turned out not to exist.
  const [missing, setMissing] = useState<string | null>(null)
  const loadAuction = useCallback(
    async (id: string) => {
      setError(null)
      try {
        setSelected(await client.get<Auction>(`/auctions/${id}`))
        setMissing(null)
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) setMissing(id)
        else setError(describe(e))
      }
    },
    [client],
  )
  const open = useCallback((id: string) => navigate(`auction/${id}`), [navigate])

  // The auction in the address, read whenever the address names a different one.
  const selectedId = view === 'auction' ? routeId : undefined

  useEffect(() => {
    if (!selectedId) {
      setSelected(null)
      return
    }
    // Not before the sign-in has finished: the staff endpoints refuse a request with
    // no token, and that refusal is not an expired session.
    if (!session) return
    if (selected?.id !== selectedId) setSelected(null)
    void loadAuction(selectedId)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, loadAuction, session])

  // An alert belongs to the page it happened on: moving to another clears it.
  useEffect(() => setError(null), [view, routeId])

  // Reload the open auction after anything that changes it. The award workflow runs
  // partly through Kafka — the processor's CandidateOffered arrives on
  // auctions.lifecycle — so the server's copy is the only one worth trusting.
  const reload = useCallback(async () => {
    if (selected) await loadAuction(selected.id)
    await refreshList()
  }, [loadAuction, refreshList, selected])

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
      if (!busy && !document.hidden) void loadAuction(watchedId)
    }, 5000)

    return () => window.clearInterval(timer)
  }, [watchedId, busy, loadAuction])

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

  if (!isAdmin && !isCommittee && !isClerk && !canReport && !canAudit && !canInquire) {
    return (
      <div className="centre">
        <div className="notice error">
          لا تملك صلاحية الدخول إلى هذه البوابة.
          <div className="small" style={{ marginTop: 8 }}>
            هذا الحساب لا يحمل أيًّا من الأدوار: <code>auction-admin</code>،{' '}
            <code>award-committee</code>، <code>operator</code>،{' '}
            <code>reporting</code>، <code>auditor</code>، <code>inquiries</code>.
          </div>
        </div>
        <button onClick={signOut}>تسجيل الخروج</button>
      </div>
    )
  }

  // This person's workspace: only the screens their roles open.
  const nav: NavItem[] = [
    ...(isCommittee ? [{ to: 'committee', label: 'لجنة الترسية', icon: 'gavel' }] : []),
    ...(isAdmin || isCommittee || isClerk ? [{ to: 'auctions', label: 'إدارة المزادات', icon: 'grid' }] : []),
    ...(isAdmin ? [{ to: 'applications', label: 'طلبات المشاركة', icon: 'shield' }] : []),
    ...(canWatch ? [{ to: 'monitor', label: 'المتابعة المباشرة', icon: 'live' }] : []),
    ...(canReport ? [{ to: 'followup', label: 'التسويات والإفراغ', icon: 'wallet' }] : []),
    ...(canInquire || isAdmin || isCommittee ? [{ to: 'inquiries', label: 'الاستفسارات', icon: 'message' }] : []),
    ...(canReport ? [{ to: 'reports', label: 'التقارير', icon: 'chart' }] : []),
    ...(canAudit ? [{ to: 'audit', label: 'سجل المراجعة', icon: 'file' }] : []),
    ...(isAdmin ? [{ to: 'visibility', label: 'إعدادات العرض للزوار', icon: 'lock' }] : []),
  ]
  const active = view === 'auction' ? 'auctions' : view ?? 'auctions'
  const crumb =
    view === 'auction'
      ? selected?.nameAr ?? 'تفاصيل المزاد'
      : nav.find((n) => n.to === active)?.label ?? 'المزادات'

  return (
    <AppShell
      brand="إدارة المزادات"
      brandSub="بوابة الأمانة للمزادات"
      nav={nav}
      active={active}
      crumb={crumb}
      session={session}
      onSignOut={signOut}
      tools={
        isClerk && (
          // A clerk's own id, where they can read it out before they have been
          // assigned to anything: an administrator needs it to put them on the
          // floor, and nothing here can list municipal staff (§29).
          <span className="muted small ltr-id">
            معرّفك: <code className="ltr" data-testid="clerk-user-id">{session.subject}</code>
          </span>
        )
      }
      sidebarFoot={
        <>
          <div>بوابة الموظفين</div>
          <strong>مزادات الأراضي</strong>
          <div className="sidebar-foot">
            الإصدار الأول <span className="pill teal plain">تجريبي</span>
          </div>
        </>
      }
      footer="كل إجراء في هذه البوابة يُسجَّل في سجل المراجعة باسم من نفّذه."
    >
        {error && <div className="notice error">{error}</div>}

        {confirmed && (
          <div className="notice ok">
            تم التحقق من هويتك. يمكنك الآن إكمال الإجراء الذي كنت عليه.
          </div>
        )}

        {view === 'monitor' && canWatch ? (
          <Monitor session={session} onOpen={open} />
        ) : view === 'applications' && isAdmin ? (
          <ApplicationsQueue session={session} onOpenAuction={open} />
        ) : view === 'committee' && isCommittee ? (
          <Committee
            session={session}
            committeeUserId={session.subject}
            runDecision={(work) => stepUp.run('followup-decision', work)}
            onOpenAuction={open}
            onOpenBids={(id) => navigate(`bids/${id}`)}
          />
        ) : view === 'bids' && routeId && (isCommittee || isAdmin || canAudit) ? (
          <BidLogPage
            session={session}
            auctionId={routeId}
            nameAr={auctions.find((a) => a.id === routeId)?.nameAr ?? null}
            onBack={() => window.history.back()}
          />
        ) : view === 'followup' ? (
          <FollowUp
            session={session}
            canRecord={isAdmin}
            canDecide={isCommittee}
            committeeUserId={session.subject}
            runDecision={(work) => stepUp.run('followup-decision', work)}
            onOpenAuction={open}
          />
        ) : view === 'inquiries' ? (
          <Inquiries session={session} canAct={canInquire} />
        ) : view === 'reports' ? (
          <Reports session={session} />
        ) : view === 'audit' ? (
          <AuditTrail session={session} />
        ) : view === 'visibility' && isAdmin ? (
          <VisitorSettings session={session} />
        ) : !isAdmin && !isCommittee && !isClerk ? (
          // A reader with only `reporting` or `auditor` has no business on the
          // auction screens, and the services would refuse them anyway. Saying so
          // beats a list that fails to load.
          <div className="card">
            <h2>المزادات</h2>
            <p className="muted small">
              هذا الحساب للقراءة فقط. اختر التقارير أو سجل المراجعة من القائمة الجانبية.
            </p>
          </div>
        ) : view === 'auction' && routeId && missing === routeId ? (
          // A link to an auction that is not there — an old bookmark, or one from
          // before the data was reset — says so, rather than a 404 and a spinner.
          <div className="card empty-state" data-testid="auction-not-found">
            <h3>المزاد غير موجود</h3>
            <p className="muted">ربما حُذف أو أن الرابط قديم. اختر المزاد من القائمة.</p>
            <button className="primary" onClick={() => navigate('auctions')}>
              جميع المزادات
            </button>
          </div>
        ) : view === 'auction' && !selected ? (
          <p className="muted">…</p>
        ) : selected ? (
          <>
            <PageHead
              eyebrow={selected.phase || 'مزاد أرض'}
              title={selected.nameAr}
              sub={`${selected.totalAreaSqm} م² · ${selected.channel === 'Onsite' ? 'مزاد حضوري' : 'مزاد إلكتروني'}`}
              action={<button onClick={() => navigate('auctions')}>جميع المزادات</button>}
            />

            <AuctionDetail
              auction={selected}
              client={client}
              session={session}
              busy={busy}
              isAdmin={isAdmin}
              isCommittee={isCommittee}
              isClerk={isClerk}
              onAct={act}
              onOpen={open}
            />
          </>
        ) : (
          <AuctionList
            session={session}
            auctions={auctions}
            canCreate={isAdmin}
            busy={busy}
            onOpen={open}
            onCreate={(body) =>
              act(async () => {
                // The draft, its terms and its plot in one request, saved together.
                const created = await client.post<Auction>('/auctions', {
                  createdByUserId: session.subject,
                  ...body,
                })
                setSelected(created)
                navigate(`auction/${created.id}`)
              })
            }
          />
        )}
    </AppShell>
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
