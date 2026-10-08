import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, CountdownPanel, Icon, PageHead, PhotoGallery, facingAr, PlotMap, landUseAr, timestamp, api, config, sar, type Session } from '@eauction/shared'
import type { AuctionDetail, Bidder, Subscription, WinnerAward } from './types'
import { WinnerPanel } from './WinnerPanel'
import { BidBox, loadSubmitted } from './BidBox'
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
  auction, session, canBid, onBack, onSignIn, onRefresh, onEnterRoom,
}: Props) {
  // What «إعدادات العرض للزوار» kept from this visitor; empty once signed in.
  const hidden = (key: string) => auction.hidden.includes(key)
  const { price, transport, verdicts } = useLivePrice(auction.id, session, !hidden('livePrice'))
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
  const [tab, setTab] = useState<'info' | 'photos' | 'documents' | 'bids' | 'participation' | 'inquiries'>('info')
  // «تفاصيل» — everything recorded about the plot, over the page.
  const [plotOpen, setPlotOpen] = useState(false)
  const canParticipate = !!session && canBid && !cancelled
  useEffect(() => {
    if (canParticipate && subscription && subscription.status !== 'Eligible' && biddingOpen) setTab('participation')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subscription?.status])

  // The land itself, for «تفاصيل الأرض»: the first plot's survey figures, and every
  // use the auction's plots are zoned for.
  const firstPlot = auction.plots[0]

  // The gallery's photos and the documents' list. One attached before the two were
  // told apart may be either: offered to both, and the gallery drops what is not a
  // picture it can draw.
  const photos = auction.attachments.filter((d) => d.kind !== 'Document')
  const papers = auction.attachments.filter((d) => d.kind !== 'Photo')
  const plotUses =
    [...new Set(auction.plots.map((p) => p.landUse).filter(Boolean))].map((u) => landUseAr(u)).join('، ') ||
    'غير محدد'

  // This bidder's own bids on this auction, as the bid box recorded them.
  const myBids = session ? loadSubmitted(auction.id, session.subject) : []


  const shownPrice = live
    ? price?.priceMinorUnits ?? auction.openingPriceMinorUnits
    : closingPrice ?? auction.openingPriceMinorUnits
  const priceLabel = live
    ? price?.priceMinorUnits != null ? 'أعلى مزايدة حالية' : 'سعر الافتتاح'
    : closingPrice !== null ? 'أعلى سعر عند الإغلاق' : 'سعر الافتتاح'

  const tabs: Array<{ key: typeof tab; label: string }> = [
    { key: 'info', label: 'تفاصيل القطعة' },
    { key: 'photos', label: 'الصور' },
    { key: 'documents', label: 'المستندات' },
    { key: 'bids', label: 'المزايدات' },
    ...(canParticipate ? [{ key: 'participation' as const, label: 'المشاركة' }] : []),
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
          <strong>أُلغي هذا المزاد دون ترسية.</strong>
          {auction.cancellationReason && <> السبب: {auction.cancellationReason}.</>} لا تُقبل
          اشتراكات أو مزايدات،{' '}
          {auction.cancellationRefunds === false
            ? 'ولا يُرد التأمين ولا رسم كراسة الشروط بقرار من الأمانة.'
            : 'ويُرد التأمين المدفوع ورسم كراسة الشروط، أو يُحرَّر الضمان البنكي.'}
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
          {/* The cover at the top; the rest of the photos are «معرض الصور» below. */}
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
                {/* As the prototype has it: the land, its six figures, and the papers.
                    The deposit, the step and the dates are in the box beside it. */}
                <h2>تفاصيل الأرض</h2>
                <p className="muted" style={{ margin: '6px 0 0' }}>
                  {firstPlot?.descriptionAr ??
                    (firstPlot ? `قطعة رقم ${firstPlot.plotNumber} ضمن ${auction.nameAr}.` : auction.nameAr)}
                </p>
                <div className="spec-grid">
                  <div><small>رقم القطعة</small><b className="num">{firstPlot?.plotNumber ?? '—'}</b></div>
                  <div><small>المساحة</small><b><span className="num">{area(totalArea)}</span> م²</b></div>
                  <div><small>الاستخدام</small><b>{plotUses}</b></div>
                  <div>
                    <small>عرض الشارع</small>
                    <b>{firstPlot?.streetWidthMeters != null ? <><span className="num">{firstPlot.streetWidthMeters}</span> متر</> : '—'}</b>
                  </div>
                  <div>
                    <small>الواجهة</small>
                    <b>{facingAr(firstPlot?.facing)}</b>
                  </div>
                  <div><small>سعر البداية</small><b className="num">{money(auction.openingPriceMinorUnits)}</b></div>
                  <div><small>زيادة المزايدة</small><b className="num">{money(auction.minIncrementMinorUnits)}</b></div>
                </div>

                {firstPlot && (
                  <div className="document-row">
                    <span className="doc-icon"><Icon name="pin" /></span>
                    <div className="grow">
                      <strong>موقع القطعة</strong>
                      <small>الموقع على الخريطة ووصف القطعة.</small>
                    </div>
                    <button className="ghost small" onClick={() => setPlotOpen(true)} data-testid="plot-details">
                      عرض
                    </button>
                  </div>
                )}
              </>
            )}

            {tab === 'photos' && (
              <>
                <h2>صور القطعة</h2>
                {hidden('photos') ? (
                  <p className="muted"><Locked /></p>
                ) : photos.length === 0 ? (
                  <p className="muted">لا توجد صور مرفقة لهذا المزاد.</p>
                ) : (
                  <PhotoGallery
                    images={photos.map((d) => ({ id: d.documentId, url: documentUrl(d.documentId), title: d.titleAr }))}
                  />
                )}
              </>
            )}

            {tab === 'documents' && (
              <>
                <h2>المستندات</h2>
                <div className="document-row">
                  <span className="doc-icon"><Icon name="file" /></span>
                  <div className="grow">
                    <strong>كراسة الشروط</strong>
                    <small>
                      {auction.bookletPriceMinorUnits === 0
                        ? 'مجانية — تُتاح للتنزيل بعد الدخول من قسم «المشاركة».'
                        : auction.bookletPriceMinorUnits == null
                          ? 'تُتاح للتنزيل بعد شرائها من قسم «المشاركة».'
                          : `${sar(auction.bookletPriceMinorUnits, 'ar')} — تُتاح للتنزيل بعد شرائها من قسم «المشاركة».`}
                    </small>
                  </div>
                  {canParticipate && (
                    <button className="small" onClick={() => setTab('participation')}>المشاركة</button>
                  )}
                </div>

                {!hidden('documents') &&
                  papers.map((d) => (
                    // Public: no token or grant, and the service answers with
                    // Content-Disposition: attachment.
                    <div key={d.documentId} className="document-row">
                      <span className="doc-icon"><Icon name="file" /></span>
                      <div className="grow">
                        <strong>{d.titleAr}</strong>
                        <small>مستند عام متاح للجميع</small>
                      </div>
                      <a className="button ghost small" href={documentUrl(d.documentId)} rel="noreferrer noopener">
                        <Icon name="download" size={16} /> تنزيل
                      </a>
                    </div>
                  ))}

                {hidden('documents') && (
                  <p className="muted small" style={{ marginTop: 12 }}>
                    <Locked /> — المستندات المرفقة.
                  </p>
                )}
              </>
            )}

            {tab === 'bids' && (
              <>
                <div className="panel-title">
                  <h2>المزايدات</h2>
                  <span className={`pill ${status.tone}`}>{status.ar}</span>
                </div>
                <div className="spec-grid">
                  <div><small>{priceLabel}</small><b className="num">{money(shownPrice)}</b></div>
                  <div>
                    <small>المزايد الأعلى</small>
                    <b>{hidden('livePrice') ? <Locked /> : price?.leaderIsYou ? 'أنت' : price?.leaderLabel ?? 'لا مزايدات بعد'}</b>
                  </div>
                  <div><small>زيادة المزايدة</small><b className="num">{money(auction.minIncrementMinorUnits)}</b></div>
                </div>
                {/* Only this bidder's own bids: who else bid what is not theirs to see (D-22). */}
                {session && canBid ? (
                  <>
                    <h3>مزايداتك</h3>
                    {myBids.length === 0 ? (
                      <p className="muted">لم تقدّم مزايدة في هذا المزاد من هذا الجهاز.</p>
                    ) : (
                      <ul className="bid-history">
                        {myBids.map((b) => {
                          const v = verdicts.find((x) => x.clientBidId === b.clientBidId)
                          return (
                            <li key={b.clientBidId}>
                              <div>
                                <b>{timestamp(b.at)}</b>
                                <small>
                                  {v ? (v.accepted ? 'مقبولة' : `مرفوضة${v.reason ? ` — ${v.reason}` : ''}`) : 'سُجّلت'}
                                </small>
                              </div>
                              <span className="num strong">{sar(b.amount, 'ar')}</span>
                            </li>
                          )
                        })}
                      </ul>
                    )}
                  </>
                ) : (
                  <p className="muted">سجّل الدخول وتأهّل للمزاد لتظهر هنا مزايداتك.</p>
                )}
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
            <div className="box-price num">{shownPrice == null ? <Locked /> : sar(shownPrice, 'ar')}</div>
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
            {(lifecycle === 'Scheduled' || lifecycle === 'Approved') && auction.startsAt ? (
              <CountdownPanel target={auction.startsAt} label="حتى البدء" />
            ) : live && !onsite && endsAt ? (
              <CountdownPanel target={endsAt} label="حتى الإغلاق" />
            ) : live && onsite ? (
              <div className="notice info small">جارٍ في القاعة — يُغلق بقرار مدير المزاد.</div>
            ) : null}

            <div className="kv"><span>تأمين المشاركة</span><b className="num">{sar(auction.depositMinorUnits, 'ar')}</b></div>
            <div className="kv"><span>الحد الأدنى للزيادة</span><b className="num">{money(auction.minIncrementMinorUnits)}</b></div>
            {price && price.extensionsUsed > 0 && (
              <div className="kv"><span>التمديد</span><b className="num">{price.extensionsUsed} من {price.maxExtensions}</b></div>
            )}
            <hr className="divider" />

            {/* Said before the deposit, not after: where the bidding happens (§29),
                and whether the leader's name is shown to the other bidders (D-22). */}
            {onsite && !cancelled && (
              <p className="box-help">مزاد حضوري — المزايدة في قاعة المزاد، والتأهّل (الكراسة والتأمين) من هنا.</p>
            )}
            {auction.bidderVisibility === 'Named' && (
              <p className="box-help">يظهر اسم المزايد الأعلى لبقية المزايدين المشاركين.</p>
            )}

            {/* The one thing to do next, by where this person stands. */}
            {!session && !cancelled && biddingOpen ? (
              <>
                <button className="primary wide" onClick={onSignIn}>الدخول بنفاذ للمشاركة</button>
                <p className="box-help">يلزم الدخول بنفاذ، ثم شراء كراسة الشروط والموافقة عليها، ثم سداد التأمين.</p>
              </>
            ) : canParticipate && eligible && live && !onsite && session ? (
              // The prototype's flow: bid right here, from the auction page. The full
              // bidding screen is a tap away for a war fought by the second.
              <>
                <div className="bid-status">
                  {price?.leaderIsYou ? 'أنت صاحب أعلى مزايدة حالياً' : 'أهليتك معتمدة والتأمين مؤكد'}
                </div>
                <BidBox
                  auction={auction}
                  session={session}
                  price={price}
                  verdicts={verdicts}
                  participant={participant}
                  onBid={onRefresh}
                />
                <button className="ghost wide" onClick={onEnterRoom}>
                  <Icon name="gavel" size={18} /> شاشة المزايدة الكاملة
                </button>
              </>
            ) : canParticipate && eligible && biddingOpen && !onsite ? (
              // Not open yet: the way in to the bidding screen, which opens itself.
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
      {plotOpen && firstPlot && <PlotDialog plot={firstPlot} onClose={() => setPlotOpen(false)} />}
    </>
  )
}

/** «تفاصيل» — the plot in full: its figures, its description and its place on the map. */
function PlotDialog({ plot, onClose }: { plot: AuctionDetail['plots'][number]; onClose: () => void }) {
  const located = !!plot.latitude && !!plot.longitude && !isNaN(Number(plot.latitude)) && !isNaN(Number(plot.longitude))
  const metres = (v: number | null) => (v != null ? <><span className="num">{v}</span> متر</> : 'غير محدد')
  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label="تفاصيل القطعة" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <button className="icon-btn" aria-label="إغلاق النافذة" onClick={onClose}>✕</button>
          <div className="grow" style={{ textAlign: 'start' }}>
            <h2>قطعة رقم {plot.plotNumber}</h2>
            <p>تفاصيل القطعة</p>
          </div>
        </div>
        <div className="spec-grid" style={{ marginTop: 0 }}>
          <div><small>المساحة</small><b><span className="num">{area(plot.areaSqm)}</span> م²</b></div>
          <div><small>الاستخدام</small><b>{landUseAr(plot.landUse)}</b></div>
          <div><small>عرض الشارع</small><b>{metres(plot.streetWidthMeters)}</b></div>
          <div><small>الواجهة</small><b>{facingAr(plot.facing)}</b></div>
          <div><small>طول الواجهة</small><b>{metres(plot.frontageMeters)}</b></div>
        </div>
        {plot.descriptionAr && <p>{plot.descriptionAr}</p>}
        {located && (
          <>
            <PlotMap points={[{ lat: Number(plot.latitude), lng: Number(plot.longitude) }]} height={260} />
            <p className="small" style={{ marginTop: 8 }}>
              <a href={`https://www.google.com/maps?q=${plot.latitude},${plot.longitude}`} target="_blank" rel="noreferrer noopener">
                فتح الموقع في خرائط Google ↗
              </a>
            </p>
          </>
        )}
      </div>
    </div>
  )
}

/** Where a figure is hidden from a visitor: said, rather than left blank. */
function Locked() {
  return (
    <span className="locked-value">
      <Icon name="lock" size={14} /> بعد تسجيل الدخول
    </span>
  )
}

/** A riyal figure, or the lock when it was not sent to this visitor. */
function money(minorUnits: number | null) {
  return minorUnits == null ? <Locked /> : sar(minorUnits, 'ar')
}
