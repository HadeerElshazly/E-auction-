import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, CountdownPanel, Icon, PageHead, api, config, sar, when, type Session } from '@eauction/shared'
import type { AuctionDetail, Bidder, Subscription, WinnerAward } from './types'
import { WinnerPanel } from './WinnerPanel'
import { useLivePrice } from './useLivePrice'
import { SubscriptionSteps } from './SubscriptionSteps'
import { AuctionInquiries } from './AuctionInquiries'
import { documentUrl, statusAr } from './Catalogue'

const areaFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 })

/** 1234.5 -> "1,234.5", Latin digits like the dates and the countdown. */
function area(sqm: number): string {
  return areaFormat.format(sqm)
}

interface Props {
  auction: AuctionDetail
  session: Session | null
  canBid: boolean
  onBack: () => void
  onSignIn: () => void
  onRefresh: () => void
  onEnterRoom: () => void
}

export function AuctionPage({
  auction, session, canBid, onBack, onSignIn, onEnterRoom,
}: Props) {
  const { price, transport } = useLivePrice(auction.id, session)
  const participant = useMemo(
    () => api({ baseUrl: config.participantApi, session }),
    [session],
  )

  const [subscription, setSubscription] = useState<Subscription | null>(null)
  const [bidder, setBidder] = useState<Bidder | null>(null)
  const [award, setAward] = useState<WinnerAward | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Both, because the steps depend on both: a 404 on either simply means that part
  // has not been done yet, which is the common case rather than a failure.
  const loadParticipant = useCallback(async () => {
    if (!session || !canBid) return

    const read = async <T,>(path: string): Promise<T | null> => {
      try {
        return await participant.get<T>(path)
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null
        setError(e instanceof Error ? e.message : String(e))
        return null
      }
    }

    setBidder(await read<Bidder>(`/bidders/${session.subject}`))
    setSubscription(
      await read<Subscription>(`/auctions/${auction.id}/subscriptions/${session.subject}`),
    )
    // 404 for everyone but the winner, which is most of the time.
    setAward(await read<WinnerAward>(`/auctions/${auction.id}/award`))
  }, [participant, auction.id, session, canBid])

  useEffect(() => {
    void loadParticipant()
  }, [loadParticipant])

  // A ticking clock, so the countdown moves between price polls.
  const [, setNow] = useState(0)
  useEffect(() => {
    const t = window.setInterval(() => setNow((n) => n + 1), 1000)
    return () => window.clearInterval(t)
  }, [])

  const lifecycle = price?.status ?? auction.status
  const live = lifecycle === 'Live'
  const cancelled = lifecycle === 'Cancelled'
  // Bidding is still ahead or under way. Anything after it — closed, awarded,
  // settled — has nothing to enter, and a «شاشة المزايدة» button there misleads.
  const biddingOpen = live || lifecycle === 'Scheduled' || lifecycle === 'Approved'
  const closed = !biddingOpen && !cancelled
  const closingPrice = closed ? price?.priceMinorUnits ?? null : null
  const currentPrice = live
    ? price?.priceMinorUnits ?? auction.openingPriceMinorUnits
    : auction.openingPriceMinorUnits
  const status = statusAr[lifecycle] ?? { ar: lifecycle, tone: 'done' }
  const endsAt = price?.effectiveEndsAt ?? auction.effectiveEndsAt ?? auction.endsAt
  const eligible = subscription?.status === 'Eligible'
  const totalArea = auction.plots.reduce((sum, p) => sum + p.areaSqm, 0)

  // A hall auction is bid in the hall: the clerk types what the room calls out and
  // the catcher refuses an online frame for it with NotTheClerk (§29). Qualifying
  // is still done from here — the booklet and the deposit are the same online — so
  // what the channel changes is only the last step.
  const onsite = auction.channel === 'Onsite'

  // Which part of the page is open. «المشاركة» first for a signed-in bidder who has
  // started but not finished qualifying, since that is the thing they came back for.
  const [tab, setTab] = useState<'info' | 'participation' | 'plots' | 'inquiries'>('info')
  const canParticipate = !!session && canBid && !cancelled
  useEffect(() => {
    if (canParticipate && subscription && subscription.status !== 'Eligible' && biddingOpen) setTab('participation')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subscription?.status])

  const shownPrice = live
    ? price?.priceMinorUnits ?? auction.openingPriceMinorUnits
    : closingPrice ?? auction.openingPriceMinorUnits
  const priceLabel = live
    ? price?.priceMinorUnits != null ? 'أعلى مزايدة حالية' : 'سعر الافتتاح'
    : closingPrice !== null ? 'أعلى سعر عند الإغلاق' : 'سعر الافتتاح'

  const tabs: Array<{ key: typeof tab; label: string }> = [
    { key: 'info', label: 'تفاصيل المزاد' },
    ...(canParticipate ? [{ key: 'participation' as const, label: 'المشاركة' }] : []),
    { key: 'plots', label: `قطع الأرض (${auction.plots.length})` },
    { key: 'inquiries', label: 'الاستفسارات' },
  ]

  return (
    <>
      <PageHead
        eyebrow={auction.nameEn || 'مزاد أرض'}
        title={auction.nameAr}
        sub={
          <>
            {auction.plots.length} قطعة · <span className="num">{area(totalArea)}</span> م² ·{' '}
            {onsite ? 'مزاد حضوري' : 'مزاد إلكتروني'}
          </>
        }
        action={
          <button onClick={onBack}>
            جميع المزادات
          </button>
        }
      />

      {error && <div className="notice error">{error}</div>}

      {cancelled && (
        <div className="notice error" role="status">
          <strong>أُلغي هذا المزاد قبل بدئه.</strong>
          {auction.cancellationReason && <> السبب: {auction.cancellationReason}.</>} لا تُقبل
          اشتراكات أو مزايدات، ويُرد التأمين المدفوع أو يُحرَّر الضمان البنكي.
        </div>
      )}

      {session && canBid && award && (
        <WinnerPanel
          award={award}
          auctionId={auction.id}
          session={session}
          participant={participant}
          onError={setError}
        />
      )}

      {session && canBid && eligible && closed && !award && (
        // A bidder who took part and did not win is told so, rather than left with a
        // page that looks the same as before the auction.
        <div className="notice info" role="status" data-testid="closed-notice">
          {['Awarded', 'Settled'].includes(lifecycle)
            ? 'انتهى المزاد ورسا على مزايد آخر. يُرد تأمينك أو يُحرَّر ضمانك البنكي بعد تسوية المزاد.'
            : lifecycle === 'Unsold'
              ? 'انتهى المزاد دون ترسية، ويُرد تأمينك أو يُحرَّر ضمانك البنكي.'
              : 'انتهت المزايدة في هذا المزاد. تُعلن النتيجة بعد اعتماد لجنة الترسية، وسيصلك إشعار.'}
        </div>
      )}

      <div className="split">
        <section>
          <div className="detail-photo">
            {auction.coverImageDocumentId && (
              <img
                src={documentUrl(auction.coverImageDocumentId)}
                alt={`صورة ${auction.nameAr}`}
                onError={(e) => {
                  e.currentTarget.style.display = 'none'
                }}
              />
            )}
            <span className={`pill ${status.tone}`}>{status.ar}</span>
          </div>

          <div className="detail-tabs" role="tablist" aria-label="أقسام المزاد">
            {tabs.map((t) => (
              <button
                key={t.key}
                role="tab"
                aria-selected={tab === t.key}
                className={`tab${tab === t.key ? ' active' : ''}`}
                onClick={() => setTab(t.key)}
              >
                {t.label}
              </button>
            ))}
          </div>

          <div className="detail-content">
            {tab === 'info' && (
              <>
                <h2>تفاصيل المزاد</h2>
                <div className="spec-grid">
                  <div><small>سعر الافتتاح</small><b className="num">{sar(auction.openingPriceMinorUnits, 'ar')}</b></div>
                  {/* The increment, not a "minimum next bid" figure: a bidder reasons
                      from the price they see plus the step. */}
                  <div><small>الحد الأدنى للزيادة</small><b className="num">{sar(auction.minIncrementMinorUnits, 'ar')}</b></div>
                  <div><small>مبلغ التأمين</small><b className="num">{sar(auction.depositMinorUnits, 'ar')}</b></div>
                  <div><small>قيمة كراسة الشروط</small><b className="num">{auction.bookletPriceMinorUnits === 0 ? 'مجانية' : sar(auction.bookletPriceMinorUnits, 'ar')}</b></div>
                  {/* «عرض التأمين ومبلغ الوساطة» (الخاصية 04): a share of the price won,
                      shown at today's price and said to move. */}
                  {auction.brokerageFeePercent > 0 && (
                    <div>
                      <small>السعي (الوساطة) — {auction.brokerageFeePercent}%</small>
                      <b className="num">{sar(Math.round((currentPrice * auction.brokerageFeePercent) / 100), 'ar')}</b>
                    </div>
                  )}
                  <div><small>المساحة الإجمالية</small><b><span className="num">{area(totalArea)}</span> م²</b></div>
                  <div><small>يبدأ</small><b>{when(auction.startsAt)}</b></div>
                  <div><small>ينتهي</small><b>{when(endsAt)}</b></div>
                  <div>
                    <small>التمديد عند المزايدة المتأخرة</small>
                    <b>{auction.quietPeriodSeconds ? `${auction.quietPeriodSeconds} ثانية، حتى ${auction.maxExtensions} مرات` : 'دون تمديد'}</b>
                  </div>
                </div>

                {onsite && !cancelled && (
                  // Before the deposit: a bidder about to commit a hundred thousand
                  // riyals needs to know they must be in the room on the day.
                  <div className="notice info">
                    مزاد حضوري — تُقدّم المزايدات في قاعة المزاد ويُسجّلها موظف القاعة برقم مجدافك.
                    التأهّل — الكراسة والتأمين — يتم من هنا، أما المزايدة نفسها فلا تُقبل إلا من القاعة.
                  </div>
                )}
                {auction.bidderVisibility === 'Named' && (
                  // Said before the deposit, not after (D-22).
                  <div className="notice info">في هذا المزاد يظهر اسم المزايد الأعلى لبقية المزايدين وللعامة.</div>
                )}

                {auction.attachments.length > 0 && (
                  <>
                    <h3>المستندات العامة</h3>
                    {auction.attachments.map((d) => (
                      // Public: no token or grant, and the service answers with
                      // Content-Disposition: attachment.
                      <div key={d.documentId} className="document-row">
                        <span className="doc-icon"><Icon name="file" /></span>
                        <div className="grow">
                          <strong>{d.titleAr}</strong>
                          <small>مستند عام متاح للجميع</small>
                        </div>
                        <a className="button small" href={documentUrl(d.documentId)} rel="noreferrer noopener">
                          <Icon name="download" size={16} /> تنزيل
                        </a>
                      </div>
                    ))}
                  </>
                )}
                <div className="document-row">
                  <span className="doc-icon"><Icon name="file" /></span>
                  <div className="grow">
                    <strong>كراسة الشروط</strong>
                    <small>تُتاح للتنزيل بعد شرائها من قسم «المشاركة».</small>
                  </div>
                  {canParticipate && (
                    <button className="small" onClick={() => setTab('participation')}>المشاركة</button>
                  )}
                </div>
              </>
            )}

            {tab === 'participation' && canParticipate && session && (
              <SubscriptionSteps
                auction={auction}
                session={session}
                subscription={subscription}
                bidder={bidder}
                participant={participant}
                onChanged={loadParticipant}
                onError={setError}
              />
            )}

            {tab === 'plots' && (
              <>
                <h2>قطع الأرض</h2>
                <p className="lede">تُباع القطع كوحدة واحدة لا تُجزَّأ — المزايدة على المزاد كاملاً.</p>
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>رقم الصك</th>
                        <th>المساحة</th>
                        <th>الموقع</th>
                        <th>الوصف</th>
                      </tr>
                    </thead>
                    <tbody>
                      {auction.plots.map((p) => (
                        <tr key={p.id}>
                          <td><span className="num strong">{p.deedNumber}</span></td>
                          <td><span className="num">{area(p.areaSqm)}</span> م²</td>
                          <td>
                            {p.latitude && p.longitude ? (
                              <a
                                href={`https://www.google.com/maps?q=${p.latitude},${p.longitude}`}
                                target="_blank"
                                rel="noreferrer noopener"
                                title={`${p.latitude}, ${p.longitude}`}
                              >
                                عرض على الخريطة ↗
                              </a>
                            ) : (
                              <span className="muted">غير محدد</span>
                            )}
                          </td>
                          <td>{p.descriptionAr ?? <span className="muted">لا يوجد وصف</span>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}

            {tab === 'inquiries' && (
              <>
                <AuctionInquiries
                  auctionId={auction.id}
                  participant={session ? participant : null}
                  canAsk={!!session && canBid}
                  open={biddingOpen}
                />
                {!session && (
                  <p className="muted">
                    تظهر هنا التوضيحات التي تنشرها الأمانة. لطرح سؤال، سجّل الدخول بنفاذ.
                  </p>
                )}
              </>
            )}
          </div>
        </section>

        <aside>
          <div className="card auction-box" data-testid="auction-box">
            <div className="kv">
              <span>{priceLabel}</span>
              <span className={`pill ${status.tone}`}>{status.ar}</span>
            </div>
            <div className="box-price num">{sar(shownPrice, 'ar')}</div>
            {price?.leaderLabel && (
              <div className="small">
                {price.leaderIsYou ? (
                  <strong style={{ color: 'var(--accent)' }}>
                    {live ? 'أنت صاحب أعلى مزايدة حالياً' : 'كنت الأعلى عند الإغلاق'}
                  </strong>
                ) : (
                  // Rendered exactly as sent: a pseudonym on a masked auction, a name
                  // on a named one (D-22) — the server decides which.
                  <span className="muted">المزايد الأعلى: {price.leaderLabel}</span>
                )}
              </div>
            )}
            {live && transport === 'polling' && (
              <span className="pill wait" title="تعذّر البث المباشر — يُحدَّث السعر كل ثانيتين">تحديث دوري</span>
            )}

            {/* حتى البدء / حتى الإغلاق — ticking. A hall auction has no closing clock (§29). */}
            {lifecycle === 'Scheduled' || lifecycle === 'Approved' ? (
              <CountdownPanel target={auction.startsAt} label="حتى البدء" />
            ) : live && !onsite ? (
              <CountdownPanel target={endsAt} label="حتى الإغلاق" />
            ) : live && onsite ? (
              <div className="notice info small">جارٍ في القاعة — يُغلق بقرار مدير المزاد.</div>
            ) : null}

            <div className="kv"><span>تأمين المشاركة</span><b className="num">{sar(auction.depositMinorUnits, 'ar')}</b></div>
            <div className="kv"><span>الحد الأدنى للزيادة</span><b className="num">{sar(auction.minIncrementMinorUnits, 'ar')}</b></div>
            {price && price.extensionsUsed > 0 && (
              <div className="kv"><span>التمديد</span><b className="num">{price.extensionsUsed} من {price.maxExtensions}</b></div>
            )}
            <hr className="divider" />

            {/* The one thing to do next, by where this person stands. */}
            {!session && !cancelled && biddingOpen ? (
              <>
                <button className="primary wide" onClick={onSignIn}>الدخول بنفاذ للمشاركة</button>
                <p className="box-help">يلزم الدخول بنفاذ، ثم شراء كراسة الشروط والموافقة عليها، ثم سداد التأمين.</p>
              </>
            ) : canParticipate && eligible && biddingOpen && !onsite ? (
              // The bidding itself has its own screen (شاشة المزايدة); here the way in.
              <>
                <div className="bid-status">أهليتك معتمدة والتأمين مؤكد</div>
                <button className="primary wide" onClick={onEnterRoom}>
                  <Icon name="gavel" size={18} /> شاشة المزايدة
                </button>
                <p className="box-help">
                  {live
                    ? 'المزاد مفتوح الآن — زايد بضغطة واحدة من شاشة المزايدة.'
                    : 'تُفتح المزايدة في الشاشة تلقائياً عند بدء المزاد.'}
                </p>
              </>
            ) : canParticipate && eligible && biddingOpen && onsite ? (
              <div className="notice info small">
                المزايدة تجري في القاعة: ارفع مجدافك ويُسجّل موظف القاعة المبلغ باسمك فور إعلانه.
              </div>
            ) : canParticipate && biddingOpen ? (
              <>
                <button className="primary wide" onClick={() => setTab('participation')}>
                  {subscription ? 'استكمال المشاركة' : 'اشترك في المزاد'}
                </button>
                <p className="box-help">الكراسة، ثم الموافقة على الشروط، ثم التأمين.</p>
              </>
            ) : (
              <p className="box-help">
                {cancelled ? 'أُلغي هذا المزاد.' : 'انتهت المزايدة في هذا المزاد.'}
              </p>
            )}
            <p className="box-help">
              <Icon name="shield" size={16} /> تُسجَّل كل مزايدة موقّعة ولا يمكن تعديلها.
            </p>
          </div>
        </aside>
      </div>
    </>
  )
}
