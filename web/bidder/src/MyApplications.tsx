import { useEffect, useMemo, useState } from 'react'
import { ApiError, PageHead, Pager, Stats, api, config, usePage, when, type Session } from '@eauction/shared'
import type { AuctionSummary, Subscription, WinnerAward } from './types'
import { statusAr } from './Catalogue'

interface Props {
  session: Session
  /** The catalogue already loaded, for the auctions' names and states. */
  auctions: AuctionSummary[]
  onOpen: (auctionId: string) => void
  /** Straight into the bidding screen, for a qualified bidder on an open auction. */
  onOpenRoom: (auctionId: string) => void
  onBack: () => void
}

type StageKey = 'all' | 'live' | 'upcoming' | 'finished' | 'won'

/** The participant service's answer: the filtered rows and each chip's count. */
interface ApplicationsPage {
  total: number
  items: Array<{
    subscription: Subscription
    auctionNameAr: string | null
    stage: string
    award: WinnerAward | null
  }>
  counts: { stage: Record<string, number>; eligibility: Record<string, number> }
}
type StandingKey = 'all' | Subscription['eligibility']

const stageFilters: Array<{ key: StageKey; ar: string }> = [
  { key: 'all', ar: 'الكل' },
  { key: 'live', ar: 'الجارية' },
  { key: 'upcoming', ar: 'القادمة' },
  { key: 'finished', ar: 'المنتهية' },
  { key: 'won', ar: 'فزت بها' },
]

const standingFilters: Array<{ key: StandingKey; ar: string }> = [
  { key: 'all', ar: 'كل الحالات' },
  { key: 'Accepted', ar: 'مقبول' },
  { key: 'UnderReview', ar: 'قيد المراجعة' },
  { key: 'Rejected', ar: 'مرفوض' },
  { key: 'Incomplete', ar: 'قيد الاستكمال' },
]


const eligibilityAr: Record<Subscription['eligibility'], { ar: string; tone: string }> = {
  Incomplete: { ar: 'قيد الاستكمال', tone: 'done' },
  UnderReview: { ar: 'قيد المراجعة', tone: 'wait' },
  Accepted: { ar: 'مقبول', tone: 'live' },
  Rejected: { ar: 'مرفوض', tone: 'bad' },
}

const nextStepAr: Record<WinnerAward['nextStep'], string> = {
  AwaitingLetter: 'بانتظار خطاب الترسية',
  Pay: 'التالي: سداد مبلغ الترسية',
  Transfer: 'التالي: الإفراغ',
  Done: 'اكتملت الترسية',
  Withdrawn: 'سُحبت الترسية',
}

const depositAr: Partial<Record<Subscription['depositSettlement'], string>> = {
  Held: 'محتجز حتى انتهاء المزاد',
  ToRefund: 'قيد الرد',
  ToRelease: 'قيد تحرير الضمان',
  ToForfeit: 'مصادَر',
  AppliedToPurchase: 'احتُسب من ثمن الترسية',
  Closed: 'تمت التسوية',
}

/**
 * طلباتي — «صفحة شخصية لمتابعة الحالة» (الخاصية 09). Every auction this bidder applied
 * to in one place: where the application stands, why if it was refused, and what
 * happened to the deposit — without opening each auction to find out.
 */
export function MyApplications({ session, auctions, onOpen, onOpenRoom, onBack }: Props) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  // Filtered, searched and counted by the participant service, not here: every
  // client gets the same answer, and the counts are the server's, not this page's.
  const [stage, setStage] = useState<StageKey>('all')
  const [standing, setStanding] = useState<StandingKey>('all')
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [page, setPage] = useState<ApplicationsPage | null>(null)
  const [total, setTotal] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const paging = usePage()
  useEffect(() => paging.setPage(0), [stage, standing, debounced])

  // The search goes to the server a moment after typing stops, not per keystroke.
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(query.trim()), 300)
    return () => window.clearTimeout(t)
  }, [query])

  useEffect(() => {
    const params = new URLSearchParams()
    if (stage !== 'all') params.set('stage', stage)
    if (standing !== 'all') params.set('eligibility', standing)
    if (debounced) params.set('q', debounced)
    participant
      .get<ApplicationsPage>(`/bidders/${session.subject}/subscriptions?${params.toString()}&${paging.query}`)
      .then((r) => {
        setPage(r)
        setError(null)
        // The unfiltered total, for the heading and the empty states.
        if (stage === 'all' && standing === 'all' && !debounced) setTotal(r.total)
        else setTotal((t) => t ?? r.counts.stage.all ?? 0)
      })
      .catch((e) =>
        setError(e instanceof ApiError ? `تعذّر تحميل طلباتك (${e.status}).` : String(e)),
      )
  }, [participant, session.subject, stage, standing, debounced, paging.query])

  const byId = useMemo(() => new Map(auctions.map((a) => [a.id, a])), [auctions])
  const items = page?.items.map((x) => x.subscription) ?? null
  const visible = items ?? []
  const nameOf = (id: string) => page?.items.find((x) => x.subscription.auctionId === id)?.auctionNameAr
  const awardOf = (id: string) => page?.items.find((x) => x.subscription.auctionId === id)?.award ?? null

  return (
    <>
      <PageHead
        eyebrow="مساحة المزايد"
        title="مشاركاتي"
        sub="تابع أهليتك وتأمينك ومزايداتك في كل مزاد من مكان واحد."
        action={<button onClick={onBack}>جميع المزادات</button>}
      />

      <Stats
        items={[
          { label: 'طلبات المشاركة', value: page?.counts.stage.all ?? 0, icon: 'file' },
          { label: 'مشاركات مقبولة', value: page?.counts.eligibility.Accepted ?? 0, icon: 'shield' },
          { label: 'قيد المراجعة', value: page?.counts.eligibility.UnderReview ?? 0, icon: 'clock' },
          { label: 'مزادات فزت بها', value: page?.counts.stage.won ?? 0, icon: 'gavel' },
        ]}
      />

      <div className="card">

        {error && <div className="notice error">{error}</div>}
        {page === null && !error && <p className="muted">…</p>}
        {total !== null && total > 0 && (
          <div className="applications-tools">
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="ابحث باسم المزاد…"
              aria-label="البحث في طلباتي"
            />
            <div className="chips" role="tablist" aria-label="حالة المزاد">
              {stageFilters.map((f) => (
                <button key={f.key} role="tab" aria-selected={stage === f.key}
                  className={stage === f.key ? 'chip on' : 'chip'} onClick={() => setStage(f.key)}>
                  {f.ar} <span className="num">({page?.counts.stage[f.key] ?? 0})</span>
                </button>
              ))}
            </div>
            <div className="chips" role="tablist" aria-label="الأهلية">
              {standingFilters.map((f) => (
                <button key={f.key} role="tab" aria-selected={standing === f.key}
                  className={standing === f.key ? 'chip on' : 'chip'} onClick={() => setStanding(f.key)}>
                  {f.ar} <span className="num">({page?.counts.eligibility[f.key] ?? 0})</span>
                </button>
              ))}
            </div>
          </div>
        )}

        {total !== null && total > 0 && page !== null && visible.length === 0 && (
          <p className="muted">لا توجد طلبات مطابقة للتصفية.</p>
        )}

        {total === 0 && (
          <p className="muted">لم تشترك في أي مزاد بعد. اختر مزاداً من القائمة للبدء.</p>
        )}

        {visible.length > 0 && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>المزاد</th>
                  <th>حالة المزاد</th>
                  <th>الأهلية</th>
                  <th>التأمين</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {visible.map((s) => {
                  const a = byId.get(s.auctionId)
                  const st = a ? (statusAr[a.status] ?? { ar: a.status, tone: 'done' }) : null
                  const e = eligibilityAr[s.eligibility] ?? eligibilityAr.Incomplete
                  const won = awardOf(s.auctionId)
                  return (
                    <tr key={s.id}>
                      <td>
                        <div className="strong">{a?.nameAr ?? nameOf(s.auctionId) ?? 'مزاد غير معروض'}</div>
                        {a && <div className="muted small">يبدأ {when(a.startsAt)}</div>}
                      </td>
                      <td>
                        {st && <span className={`pill ${st.tone}`}>{st.ar}</span>}
                        {won && !won.withdrawnAt && (
                          <div style={{ marginTop: 4 }}>
                            <span className="pill live">🏆 رسا عليك</span>
                            <div className="small muted" style={{ marginTop: 2 }}>{nextStepAr[won.nextStep]}</div>
                          </div>
                        )}
                        {won?.withdrawnAt && <div className="small" style={{ color: 'var(--danger)' }}>سُحبت الترسية</div>}
                      </td>
                      <td>
                        <span className={`pill ${e.tone}`}>{e.ar}</span>
                        {s.eligibilityReason && (
                          <div className="small" style={{ color: 'var(--danger)', marginTop: 4 }}>
                            {s.eligibilityReason}
                          </div>
                        )}
                      </td>
                      <td className="small">
                        {depositAr[s.depositSettlement] ?? <span className="muted">—</span>}
                      </td>
                      <td>
                        {a && s.status === 'Eligible' && a.channel !== 'Onsite'
                          && (a.status === 'Live' || a.status === 'Scheduled') ? (
                          <button
                            className={a.status === 'Live' ? 'primary' : ''}
                            onClick={() => onOpenRoom(s.auctionId)}
                          >
                            شاشة المزايدة
                          </button>
                        ) : a ? (
                          <button className={won && !won.withdrawnAt ? 'primary' : 'ghost'} onClick={() => onOpen(s.auctionId)}>
                            {won && !won.withdrawnAt ? 'متابعة الترسية' : 'فتح المزاد'}
                          </button>
                        ) : null}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
            <Pager page={paging.page} total={page?.total ?? 0} noun="طلب" onPage={paging.setPage} />
          </div>
        )}
      </div>
    </>
  )
}
