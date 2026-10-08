import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, PageHead, Pager, api, config, day, riyals, stageLabel, usePage, type Session } from '@eauction/shared'

/**
 * التقارير, as a stakeholder reads them.
 *
 * Six reports behind one tab strip, because the questions a municipality asks are
 * six and they want them in one place: what did each auction do, what did the
 * programme raise, how many registrations became bidders, how much land is left,
 * whose money are we holding, and who defaulted.
 *
 * Every table has a تنزيل CSV beside it. That matters more than it looks: the
 * people who ask for التقارير put them in a spreadsheet, and a report they can only
 * read on a screen is a report they will ask somebody to retype.
 */

type Tab = 'revenue' | 'auctions' | 'participation' | 'plots' | 'deposits' | 'disqualifications'

const TABS: ReadonlyArray<{ id: Tab; label: string; path: string; file: string }> = [
  { id: 'revenue', label: 'الإيرادات', path: '/reports/revenue', file: 'revenue' },
  { id: 'auctions', label: 'نتائج المزادات', path: '/reports/auctions', file: 'auctions' },
  { id: 'participation', label: 'المشاركة', path: '/reports/participation', file: 'participation' },
  { id: 'plots', label: 'القطع', path: '/reports/plots', file: 'plots' },
  { id: 'deposits', label: 'التأمينات المحتجزة', path: '/reports/deposits', file: 'deposits' },
  {
    id: 'disqualifications',
    label: 'إلغاء الترسية',
    path: '/reports/disqualifications',
    file: 'disqualifications',
  },
]

interface Page<T> {
  count: number
  total: number
  items: T[]
}

interface RevenueRow {
  group: string
  auctionsSettled: number
  auctionsUnsold: number
  saleValueMinorUnits: number
  brokerageChargedMinorUnits: number
  bookletFeesChargedMinorUnits: number
  depositsForfeitedMinorUnits: number
  depositsRefundedMinorUnits: number
  depositsHeldMinorUnits: number
  collectedMinorUnits: number
}

interface AuctionRow {
  auctionId: string
  nameAr: string
  phase: string | null
  channel: string
  outcome: string
  closedAt: string | null
  bidCount: number
  plotCount: number
  totalAreaSqm: number
  finalPriceMinorUnits: number | null
  pricePerSqmMinorUnits: number
  brokerageDueMinorUnits: number | null
  cascadeStep: number
  winnerNameAr: string | null
  eligibleBidders: number
}

interface ParticipationRow {
  auctionId: string
  nameAr: string
  phase: string | null
  outcome: string
  bookletPaid: number
  depositPaid: number
  paymentRefused: number
  eligible: number
  eligibilityEnded: number
  bidCount: number
  disqualified: number
}

interface PlotRow {
  plotId: string
  plotNumber: string
  streetWidthMeters: number | null
  frontageMeters: number | null
  areaSqm: number
  phase: string | null
  auctionNameAr: string
  outcome: string
  sold: boolean
  packagePriceMinorUnits: number | null
  pricePerSqmMinorUnits: number
}

interface DepositRow {
  auctionId: string
  nameAr: string
  phase: string | null
  outcome: string
  biddersHolding: number
  heldMinorUnits: number
}

interface DisqualificationRow {
  auctionId: string
  nameAr: string
  bidderNameAr: string | null
  disqualifiedAt: string | null
  reason: string | null
  depositForfeited: boolean
  outcome: string
  finalPriceMinorUnits: number | null
}

interface PhaseRow {
  phase: string | null
  auctions: number
  plots: number
  areaSqm: number
  settled: number
}

export function Reports({ session }: { session: Session }) {
  const client = useMemo(() => api({ baseUrl: config.reportingApi, session }), [session])

  const [tab, setTab] = useState<Tab>('revenue')
  const [groupBy, setGroupBy] = useState<'phase' | 'month' | 'channel'>('phase')
  const [phase, setPhase] = useState<string>('')
  const [phases, setPhases] = useState<PhaseRow[]>([])
  /**
   * The rows and the tab they belong to, in one piece of state.
   *
   * Not two. Pressing a tab changes `tab` at once while the fetch is still in
   * flight, so with the rows held separately React renders the new tab's columns
   * against the old tab's objects for one frame — every field `undefined`, every
   * key missing, and a table of empty cells that resolves so fast a person reads it
   * as a flicker. The browser walk-through caught it as a React key warning, which
   * is the only visible trace it leaves.
   *
   * Keeping them together makes that pairing unrepresentable: a render either has
   * this tab's rows or shows that it is still loading. The tab is `null` until the
   * first fetch resolves, so the opening render says "loading" rather than "no data
   * for this report yet" — which would be a lie, and one a test could pass on.
   */
  const [data, setData] = useState<{ tab: Tab | null; rows: unknown[] }>({
    tab: null,
    rows: [],
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const active = TABS.find((t) => t.id === tab)!

  /**
   * The query string both the table and the CSV use.
   *
   * One function, because a download that filtered differently from the table above
   * it is the sort of thing nobody notices until a figure is questioned in a
   * meeting.
   */
  const query = useCallback(
    (extra?: string) => {
      const parts: string[] = []
      if (phase) parts.push(`phase=${encodeURIComponent(phase)}`)
      if (tab === 'revenue') parts.push(`groupBy=${groupBy}`)
      if (extra) parts.push(extra)
      return `?${parts.join('&')}`
    },
    [groupBy, phase, tab],
  )

  /**
   * Bumped by تحديث, so a manual refresh re-runs the fetch below without the
   * button needing its own copy of it.
   */
  const [refresh, setRefresh] = useState(0)

  // The page of the report on screen, cut by the reporting service; back to the
  // first page whenever the report or its filters change. The CSV is always whole.
  const paging = usePage()
  const [total, setTotal] = useState(0)
  useEffect(() => paging.setPage(0), [tab, phase, groupBy])

  /**
   * One fetch, in the effect that owns it, discarding its own result if something
   * superseded it.
   *
   * Two separate races live here and they need the two different mechanisms, which
   * is why neither alone was enough.
   *
   * `cancelled` is for responses that come back out of order. Press a tab, press
   * another, and both fetches are in flight; whichever lands last used to win and
   * could leave the screen showing the wrong report — or, with the tag below and
   * no cancellation, leave it showing nothing at all, because the tag would never
   * again match and nothing would re-fetch to repair it. Changing a filter raced
   * the same way: a slower response for the previous grouping could overwrite the
   * current one, and the table would show بالمخطط rows while the selector read
   * بالشهر. Discarding a superseded response closes all of it.
   *
   * The tag is for the frame *before* any response arrives. `tab` changes the
   * moment the button is pressed, so a render between the press and the response
   * would pair the new tab's columns with the old tab's objects — every field
   * `undefined`. Cancellation cannot help with that: the old rows are legitimately
   * the last thing fetched. Holding the rows with the tab they belong to makes the
   * pairing unrepresentable, and a render either has this tab's rows or says it is
   * still loading.
   */
  useEffect(() => {
    let cancelled = false

    setBusy(true)
    setError(null)

    client
      .get<Page<unknown>>(active.path + query(paging.query))
      .then((page) => {
        if (cancelled) return
        setData({ tab: active.id, rows: page.items })
        setTotal(page.total)
        setBusy(false)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(describe(e))
        // Tagged with this tab even though it holds nothing, so the screen leaves
        // the loading state. What it must not do is then claim the report is
        // empty — see the render below, which shows the failure instead.
        setData({ tab: active.id, rows: [] })
        setBusy(false)
      })

    return () => {
      cancelled = true
    }
  }, [active.id, active.path, client, query, refresh, paging.query])

  // The phase filter's options, so nobody has to type مخطط السعيد — المرحلة الأولى
  // by hand to narrow a report.
  useEffect(() => {
    void (async () => {
      try {
        const page = await client.get<{ items: PhaseRow[] }>('/reports/phases')
        setPhases(page.items)
      } catch {
        // A missing filter is a smaller problem than a blocked report.
      }
    })()
  }, [client])

  const download = useCallback(async () => {
    setError(null)
    try {
      await client.download(
        active.path + query('format=csv'),
        `eauction-${active.file}.csv`,
      )
    } catch (e) {
      setError(describe(e))
    }
  }, [active, client, query])

  return (
    <>
    <PageHead
      eyebrow="مساحة الإدارة"
      title="التقارير"
      sub="نتائج المزادات والمراحل والقطع والتأمينات — ملخص تشغيلي قابل للتصدير."
    />
    <div className="card" data-testid="reports">

      <div className="row" style={{ marginBottom: 14 }}>
        {TABS.map((t) => (
          <button
            key={t.id}
            className={t.id === tab ? 'primary' : ''}
            data-testid={`report-tab-${t.id}`}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div className="row" style={{ marginBottom: 14 }}>
        <label className="small muted">
          المخطط{' '}
          <select
            value={phase}
            data-testid="report-phase"
            onChange={(e) => setPhase(e.target.value)}
          >
            <option value="">كل المخططات</option>
            {phases
              .filter((p) => p.phase)
              .map((p) => (
                <option key={p.phase} value={p.phase!}>
                  {p.phase} ({p.plots} قطعة)
                </option>
              ))}
          </select>
        </label>

        {tab === 'revenue' && (
          <label className="small muted">
            التجميع{' '}
            <select
              value={groupBy}
              data-testid="report-groupby"
              onChange={(e) => setGroupBy(e.target.value as 'phase' | 'month' | 'channel')}
            >
              <option value="phase">بالمخطط</option>
              <option value="month">بالشهر</option>
              <option value="channel">بالقناة</option>
            </select>
          </label>
        )}

        <span className="grow" />
        <button onClick={() => setRefresh((n) => n + 1)} disabled={busy}>
          تحديث
        </button>
        <button className="primary" data-testid="report-csv" onClick={() => void download()}>
          تنزيل CSV
        </button>
      </div>

      {error && <div className="notice error">{error}</div>}

      {data.tab !== tab ? (
        <p className="muted small" data-testid="report-loading">…</p>
      ) : error ? (
        // Nothing, because the failure is already shown above. Saying "no data for
        // this report yet" under a 403 would be telling a stakeholder the report is
        // empty when in fact it never loaded.
        null
      ) : data.rows.length === 0 ? (
        <p className="muted small" data-testid="report-empty">
          لا توجد بيانات لهذا التقرير بعد.
        </p>
      ) : (
        <>
          <Table tab={tab} rows={data.rows} />
          <Pager page={paging.page} total={total} noun="صف" onPage={paging.setPage} />
        </>
      )}

      {tab === 'revenue' && data.tab === tab && data.rows.length > 0 && (
        <p className="muted small" style={{ marginTop: 12 }}>
          قيمة الأراضي المبيعة لا تُجمع مع المتحصّلات: ثمن الأرض لا يمرّ عبر هذه
          المنصة — المنصة تحصّل قيمة الكراسة والسعي والتأمينات المحتجزة فقط.
        </p>
      )}
    </div>
    </>
  )
}

function Table({ tab, rows }: { tab: Tab; rows: unknown[] }) {
  switch (tab) {
    case 'revenue':
      return (
        <div className="table-scroll">
          <table data-testid="report-table">
            <thead>
              <tr>
                <th>المجموعة</th>
                <th>مزادات مُرسّاة</th>
                <th>غير مبيعة</th>
                <th>قيمة الأراضي</th>
                <th>السعي</th>
                <th>الكراسات</th>
                <th>تأمينات محتجزة</th>
                <th>مصادرة</th>
                <th>مُعادة</th>
                <th>إجمالي المتحصّل</th>
              </tr>
            </thead>
            <tbody>
              {(rows as RevenueRow[]).map((r) => (
                <tr key={r.group}>
                  <td>{r.group}</td>
                  <td className="num">{r.auctionsSettled}</td>
                  <td className="num">{r.auctionsUnsold}</td>
                  <td className="num">{riyals(r.saleValueMinorUnits)}</td>
                  <td className="num">{riyals(r.brokerageChargedMinorUnits)}</td>
                  <td className="num">{riyals(r.bookletFeesChargedMinorUnits)}</td>
                  <td className="num">{riyals(r.depositsHeldMinorUnits)}</td>
                  <td className="num">{riyals(r.depositsForfeitedMinorUnits)}</td>
                  <td className="num">{riyals(r.depositsRefundedMinorUnits)}</td>
                  <td className="num">
                    <strong>{riyals(r.collectedMinorUnits)}</strong>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )

    case 'auctions':
      return (
        <div className="table-scroll">
          <table data-testid="report-table">
            <thead>
              <tr>
                <th>المزاد</th>
                <th>المخطط</th>
                <th>القناة</th>
                <th>الحالة</th>
                <th>القطع</th>
                <th>المساحة م²</th>
                <th>العروض</th>
                <th>المزايدون</th>
                <th>السعر النهائي</th>
                <th>ر.س/م²</th>
                <th>السعي</th>
                <th>الفائز</th>
              </tr>
            </thead>
            <tbody>
              {(rows as AuctionRow[]).map((r) => (
                <tr key={r.auctionId}>
                  <td>{r.nameAr}</td>
                  <td className="small muted">{r.phase ?? '—'}</td>
                  <td>{r.channel === 'Onsite' ? 'في الموقع' : 'إلكتروني'}</td>
                  <td>
                    <Outcome outcome={r.outcome} />
                    {r.cascadeStep > 1 && (
                      <span className="small muted"> (ترسية {r.cascadeStep})</span>
                    )}
                  </td>
                  <td className="num">{r.plotCount}</td>
                  <td className="num">{r.totalAreaSqm}</td>
                  <td className="num">{r.bidCount}</td>
                  <td className="num">{r.eligibleBidders}</td>
                  <td className="num">
                    {r.finalPriceMinorUnits === null ? '—' : riyals(r.finalPriceMinorUnits)}
                  </td>
                  <td className="num">
                    {r.pricePerSqmMinorUnits ? riyals(r.pricePerSqmMinorUnits) : '—'}
                  </td>
                  <td className="num">
                    {r.brokerageDueMinorUnits === null ? '—' : riyals(r.brokerageDueMinorUnits)}
                  </td>
                  {/* Null on a masked auction, because the topic never carried a name
                      (D-22). Shown as مزايد مجهول rather than blank so nobody reads it
                      as missing data. */}
                  <td className="small">{r.winnerNameAr ?? (r.finalPriceMinorUnits ? 'مزايد مُقنَّع' : '—')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )

    case 'participation':
      return (
        <div className="table-scroll">
          <table data-testid="report-table">
            <thead>
              <tr>
                <th>المزاد</th>
                <th>المخطط</th>
                <th>كراسة مدفوعة</th>
                <th>تأمين مدفوع</th>
                <th>دفع مرفوض</th>
                <th>مؤهّل</th>
                <th>أُلغي تأهيله</th>
                <th>عروض</th>
                <th>إلغاء ترسية</th>
              </tr>
            </thead>
            <tbody>
              {(rows as ParticipationRow[]).map((r) => (
                <tr key={r.auctionId}>
                  <td>{r.nameAr}</td>
                  <td className="small muted">{r.phase ?? '—'}</td>
                  <td className="num">{r.bookletPaid}</td>
                  <td className="num">{r.depositPaid}</td>
                  <td className="num">{r.paymentRefused}</td>
                  <td className="num">{r.eligible}</td>
                  <td className="num">{r.eligibilityEnded}</td>
                  <td className="num">{r.bidCount}</td>
                  <td className="num">{r.disqualified}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )

    case 'plots':
      return (
        <div className="table-scroll">
          <table data-testid="report-table">
            <thead>
              <tr>
                <th>رقم القطعة</th>
                <th>المساحة م²</th>
                <th>عرض الشارع</th>
                <th>الواجهة</th>
                <th>المخطط</th>
                <th>المزاد</th>
                <th>الحالة</th>
                <th>سعر الحزمة</th>
                <th>ر.س/م²</th>
              </tr>
            </thead>
            <tbody>
              {(rows as PlotRow[]).map((r) => (
                <tr key={r.plotId}>
                  <td className="ltr mono">{r.plotNumber}</td>
                  <td className="num">{r.areaSqm}</td>
                  <td className="num">{r.streetWidthMeters ?? '—'}</td>
                  <td className="num">{r.frontageMeters ?? '—'}</td>
                  <td className="small muted">{r.phase ?? '—'}</td>
                  <td className="small">{r.auctionNameAr}</td>
                  <td>
                    {r.sold ? <span className="pill done">مبيعة</span> : <Outcome outcome={r.outcome} />}
                  </td>
                  <td className="num">
                    {r.packagePriceMinorUnits === null ? '—' : riyals(r.packagePriceMinorUnits)}
                  </td>
                  <td className="num">
                    {r.pricePerSqmMinorUnits ? riyals(r.pricePerSqmMinorUnits) : '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )

    case 'deposits':
      return (
        <div className="table-scroll">
          <table data-testid="report-table">
            <thead>
              <tr>
                <th>المزاد</th>
                <th>المخطط</th>
                <th>الحالة</th>
                <th>عدد المزايدين</th>
                <th>المحتجز</th>
              </tr>
            </thead>
            <tbody>
              {(rows as DepositRow[]).map((r) => (
                <tr key={r.auctionId}>
                  <td>{r.nameAr}</td>
                  <td className="small muted">{r.phase ?? '—'}</td>
                  <td><Outcome outcome={r.outcome} /></td>
                  <td className="num">{r.biddersHolding}</td>
                  <td className="num">
                    <strong>{riyals(r.heldMinorUnits)}</strong>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )

    case 'disqualifications':
      return (
        <div className="table-scroll">
          <table data-testid="report-table">
            <thead>
              <tr>
                <th>المزاد</th>
                <th>المزايد</th>
                <th>التاريخ</th>
                <th>السبب</th>
                <th>التأمين</th>
                <th>نتيجة المزاد</th>
              </tr>
            </thead>
            <tbody>
              {(rows as DisqualificationRow[]).map((r, i) => (
                <tr key={`${r.auctionId}:${i}`}>
                  <td>{r.nameAr}</td>
                  <td className="small">{r.bidderNameAr ?? 'مزايد مُقنَّع'}</td>
                  <td className="small muted">{date(r.disqualifiedAt)}</td>
                  <td className="small">{r.reason ?? '—'}</td>
                  <td>
                    {r.depositForfeited ? (
                      <span className="pill bad">مصادر</span>
                    ) : (
                      <span className="pill done">مُعاد</span>
                    )}
                  </td>
                  <td>
                    <Outcome outcome={r.outcome} />
                    {r.finalPriceMinorUnits !== null && (
                      <span className="small muted"> {riyals(r.finalPriceMinorUnits)}</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )
  }
}

/** The outcome as a label somebody can read, in the colour it deserves. */
function Outcome({ outcome }: { outcome: string }) {
  // The same words as every other screen (shared/src/stages.ts): a report that
  // called an auction «مُرسّى» while its own page said «تمت الترسية» is two truths.
  const { ar, tone } = stageLabel(outcome)
  return <span className={`pill ${tone}`}>{ar}</span>
}

function date(value: string | null): string {
  // Hijri on screen, like every other date in the portals. The CSV export keeps
  // ISO-8601 Gregorian: it is read by spreadsheets and other systems, not people.
  return day(value)
}

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 403) return 'هذا التقرير يتطلب دور reporting أو auction-admin أو award-committee.'
    if (e.status === 401) return 'انتهت الجلسة. أعد تسجيل الدخول.'
    if (e.status === 0) return 'لا يمكن الوصول إلى خدمة التقارير. تأكد من أنها تعمل.'
    return e.problems[0] ?? e.message
  }
  return e instanceof Error ? e.message : String(e)
}
