import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, api, config, sar, untilText, when, type Session } from '@eauction/shared'
import type { AuctionDetail, Bidder, Subscription } from './types'
import { useLivePrice } from './useLivePrice'
import { SubscriptionSteps } from './SubscriptionSteps'
import { BidBox } from './BidBox'
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
}

export function AuctionPage({ auction, session, canBid, onBack, onSignIn, onRefresh }: Props) {
  const { price, verdicts, transport } = useLivePrice(auction.id, session)
  const participant = useMemo(
    () => api({ baseUrl: config.participantApi, session }),
    [session],
  )

  const [subscription, setSubscription] = useState<Subscription | null>(null)
  const [bidder, setBidder] = useState<Bidder | null>(null)
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
  const currentPrice = live
    ? price?.priceMinorUnits ?? auction.openingPriceMinorUnits
    : auction.openingPriceMinorUnits
  const status = statusAr[lifecycle] ?? { ar: lifecycle, tone: 'done' }
  const endsAt = price?.effectiveEndsAt ?? auction.effectiveEndsAt ?? auction.endsAt
  const eligible = subscription?.status === 'Eligible'
  const totalArea = auction.plots.reduce((sum, p) => sum + p.areaSqm, 0)

  // What the clock should say, by lifecycle. A hall auction has no clock while it
  // runs — the auctioneer closes it, not a timer (§29) — so it says so instead of
  // counting down to an end time that does not bind.
  const countdown =
    lifecycle === 'Scheduled'
      ? { label: 'يبدأ بعد', value: untilText(auction.startsAt) }
      : live && auction.channel === 'Onsite'
        ? { label: 'الإغلاق', value: 'بقرار مدير المزاد' }
        : live
          ? { label: 'يُغلق بعد', value: untilText(endsAt) }
          : { label: 'الحالة', value: status.ar }

  return (
    <>
      <button className="back-link" onClick={onBack}>
        → كل المزادات
      </button>

      {error && <div className="notice error">{error}</div>}

      <div className="card auction-hero">
        {auction.coverImageDocumentId && (
          <img
            className="hero-cover"
            src={documentUrl(auction.coverImageDocumentId)}
            alt={`صورة ${auction.nameAr}`}
            onError={(e) => {
              e.currentTarget.style.display = 'none'
            }}
          />
        )}

        <div className="row" style={{ gap: 8, marginBottom: 10 }}>
          <span className={`pill ${status.tone}`}>{status.ar}</span>
          {/* Said out loud, because the two differ in how stale the price can be
              and a bidder in a war deserves to know which they are on. */}
          {live && transport === 'stream' && (
            <span className="pill teal" title="يُحدَّث السعر فور تغيّره">
              مباشر
            </span>
          )}
          {live && transport === 'polling' && (
            <span className="pill wait" title="تعذّر البث المباشر — يُحدَّث السعر كل ثانيتين">
              تحديث دوري
            </span>
          )}
        </div>

        <h1>{auction.nameAr}</h1>
        {auction.nameEn && <div className="hero-sub ltr">{auction.nameEn}</div>}

        <div className="hero-meta">
          <span>{auction.plots.length} قطعة</span>
          <span>
            <span className="num">{area(totalArea)}</span> م²
          </span>
          <span>{auction.channel === 'Onsite' ? 'مزاد حضوري' : 'مزاد إلكتروني'}</span>
          <span>
            {auction.bidderVisibility === 'Named' ? 'أسماء المزايدين ظاهرة' : 'هوية المزايدين مخفية'}
          </span>
        </div>

        <div className="timeline">
          <div className="timeline-point">
            <span className="timeline-label">يبدأ</span>
            <span className="timeline-value">{when(auction.startsAt)}</span>
          </div>
          <span className="timeline-arrow" aria-hidden="true">
            ←
          </span>
          <div className="timeline-point">
            <span className="timeline-label">ينتهي</span>
            <span className="timeline-value">{when(endsAt)}</span>
            {price && price.extensionsUsed > 0 && (
              <span className="timeline-note">
                مُدّد {price.extensionsUsed} من {price.maxExtensions}
              </span>
            )}
          </div>
          <div className="timeline-countdown">
            <span className="timeline-label">{countdown.label}</span>
            <span className="timeline-value">{countdown.value}</span>
          </div>
        </div>

        <div className="stat-grid">
          <div className={`stat${live ? ' highlight' : ''}`}>
            <div className="stat-label">{live ? 'السعر الحالي' : 'سعر الافتتاح'}</div>
            <div className="stat-value num">
              {sar(
                live ? price?.priceMinorUnits ?? auction.openingPriceMinorUnits : auction.openingPriceMinorUnits,
                'ar',
              )}
            </div>
            {price?.leaderLabel && (
              <div className="stat-sub">
                {price.leaderIsYou ? (
                  <strong style={{ color: 'var(--accent)' }}>أنت الأعلى حالياً</strong>
                ) : (
                  // Rendered exactly as sent. The label is a pseudonym on a masked
                  // auction and a name on a named one (D-22), and the server decides
                  // which — a portal that assembled it from parts would be a second
                  // place for that decision to be wrong.
                  <>المزايد الأعلى: {price.leaderLabel}</>
                )}
              </div>
            )}
          </div>

          <div className="stat">
            <div className="stat-label">أقل مزايدة مقبولة</div>
            <div className="stat-value num">
              {sar(price?.minimumNextBidMinorUnits ?? auction.minimumNextBidMinorUnits, 'ar')}
            </div>
            <div className="stat-sub">
              أقل زيادة <span className="num">{sar(auction.minIncrementMinorUnits, 'ar')}</span>
            </div>
          </div>

          <div className="stat">
            <div className="stat-label">مبلغ التأمين</div>
            <div className="stat-value num">{sar(auction.depositMinorUnits, 'ar')}</div>
            <div className="stat-sub">يُسدَّد قبل المزايدة</div>
          </div>

          <div className="stat">
            <div className="stat-label">قيمة كراسة الشروط</div>
            <div className="stat-value num">{sar(auction.bookletPriceMinorUnits, 'ar')}</div>
            <div className="stat-sub">شرط للتسجيل في المزاد</div>
          </div>

          {/* «عرض التأمين ومبلغ الوساطة» (الخاصية 04): the brokerage is a share of the
              price won, so its amount is shown at today's price and said to move. */}
          {auction.brokerageFeePercent > 0 && (
            <div className="stat">
              <div className="stat-label">السعي (الوساطة)</div>
              <div className="stat-value num">
                {sar(Math.round((currentPrice * auction.brokerageFeePercent) / 100), 'ar')}
              </div>
              <div className="stat-sub">
                <span className="num">{auction.brokerageFeePercent}%</span> من سعر الترسية — يدفعه
                الفائز، ويتغيّر بتغيّر السعر
              </div>
            </div>
          )}
        </div>
      </div>

      {cancelled && (
        <div className="notice error" role="status">
          <strong>أُلغي هذا المزاد قبل بدئه.</strong>
          {auction.cancellationReason && <> السبب: {auction.cancellationReason}.</>} لا تُقبل
          اشتراكات أو مزايدات، ويُرد التأمين المدفوع أو يُحرَّر الضمان البنكي.
        </div>
      )}

      {auction.bidderVisibility === 'Named' && (
        // Said before the deposit, not after. The administrator may run an auction
        // in which bidders are named to each other (D-22), and someone about to
        // commit a hundred thousand riyals is entitled to know that first.
        <div className="notice info">
          في هذا المزاد يظهر اسم المزايد الأعلى لبقية المزايدين وللعامة.
        </div>
      )}

      {!session && !cancelled && (
        <div className="card">
          <h2>للمزايدة</h2>
          <p className="muted">
            يلزم الدخول بنفاذ، ثم شراء كراسة الشروط والموافقة عليها، ثم سداد التأمين.
          </p>
          <button className="primary big" onClick={onSignIn}>
            الدخول بنفاذ
          </button>
        </div>
      )}

      {session && canBid && !cancelled && (
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

      {session && canBid && eligible && !cancelled && (
        <BidBox
          auction={auction}
          session={session}
          price={price}
          verdicts={verdicts}
          participant={participant}
          onBid={onRefresh}
        />
      )}

      <div className="card">
        <div className="section-head">
          <h2>قطع الأرض</h2>
          <span className="pill teal plain">
            {auction.plots.length} قطعة · <span className="num">{area(totalArea)}</span> م²
          </span>
        </div>
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
                  {/* .num on a span, not the cell: on the cell it makes the cell
                      left-to-right, which pushes the value to the far side of its
                      column, away from the heading above it. */}
                  <td>
                    <span className="num strong">{p.deedNumber}</span>
                  </td>
                  <td>
                    <span className="num">{area(p.areaSqm)}</span> م²
                  </td>
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
      </div>

      {auction.attachments.length > 0 && (
        <div className="card">
          <h2>المستندات العامة</h2>
          <p className="lede">
            مستندات متاحة للجميع دون تسجيل. كراسة الشروط ليست منها — تُتاح بعد شرائها.
          </p>
          <ul className="doc-list">
            {auction.attachments.map((d) => (
              <li key={d.documentId}>
                {/* A plain link: these are Public, so no token or grant is needed,
                    and the service answers with Content-Disposition: attachment. */}
                <a href={documentUrl(d.documentId)} rel="noreferrer noopener">
                  📄 {d.titleAr}
                </a>
                <span className="muted small">تنزيل</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </>
  )
}
