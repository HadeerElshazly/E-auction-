import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, api, config, sar, untilText, when, type Session } from '@eauction/shared'
import type { AuctionDetail, Bidder, Subscription } from './types'
import { useLivePrice } from './useLivePrice'
import { SubscriptionSteps } from './SubscriptionSteps'
import { BidBox } from './BidBox'

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

  const live = price?.status === 'Live'
  const endsAt = price?.effectiveEndsAt ?? auction.effectiveEndsAt ?? auction.endsAt
  const eligible = subscription?.status === 'Eligible'

  return (
    <>
      <div className="row" style={{ marginBottom: 14 }}>
        <button onClick={onBack}>← كل المزادات</button>
      </div>

      {error && <div className="notice error">{error}</div>}

      <div className="card">
        <div className="row" style={{ marginBottom: 4 }}>
          <h2 style={{ margin: 0 }}>{auction.nameAr}</h2>
          <span className="grow" />
          {/* Said out loud, because the two differ in how stale the price can be
              and a bidder in a war deserves to know which they are on. */}
          {live && transport === 'stream' && (
            <span className="pill live" title="يُحدَّث السعر فور تغيّره">
              مباشر
            </span>
          )}
          {live && transport === 'polling' && (
            <span className="pill wait" title="تعذّر البث المباشر — يُحدَّث السعر كل ثانيتين">
              تحديث دوري
            </span>
          )}
        </div>
        <div className="muted small ltr" style={{ marginBottom: 16 }}>
          {auction.nameEn}
        </div>

        <div className="grid">
          <div>
            <div className="muted small">{live ? 'السعر الحالي' : 'سعر الافتتاح'}</div>
            <div className="big-number num">
              {sar(
                live ? price?.priceMinorUnits ?? auction.openingPriceMinorUnits : auction.openingPriceMinorUnits,
                'ar',
              )}
            </div>
            {price?.leaderLabel && (
              <div className="small" style={{ marginTop: 4 }}>
                {price.leaderIsYou ? (
                  <strong style={{ color: 'var(--accent)' }}>أنت الأعلى حالياً</strong>
                ) : (
                  // Rendered exactly as sent. The label is a pseudonym on a masked
                  // auction and a name on a named one (D-22), and the server decides
                  // which — a portal that assembled it from parts would be a second
                  // place for that decision to be wrong.
                  <span className="muted">المزايد الأعلى: {price.leaderLabel}</span>
                )}
              </div>
            )}
          </div>

          <div>
            <div className="muted small">{live ? 'الوقت المتبقي' : 'يبدأ'}</div>
            <div className="big-number num">
              {live
                ? untilText(endsAt)
                : when(auction.startsAt)}
            </div>
            {price && price.extensionsUsed > 0 && (
              <div className="muted small" style={{ marginTop: 4 }}>
                مُدّد {price.extensionsUsed} من {price.maxExtensions}
              </div>
            )}
          </div>

          <div>
            <div className="muted small">أقل مزايدة مقبولة</div>
            <div className="num" style={{ fontSize: 18, fontWeight: 650 }}>
              {sar(price?.minimumNextBidMinorUnits ?? auction.minimumNextBidMinorUnits, 'ar')}
            </div>
            <div className="muted small">
              بزيادة <span className="num">{sar(auction.minIncrementMinorUnits, 'ar')}</span>
            </div>
          </div>

          <div>
            <div className="muted small">التأمين</div>
            <div className="num" style={{ fontSize: 18, fontWeight: 650 }}>
              {sar(auction.depositMinorUnits, 'ar')}
            </div>
            <div className="muted small">
              الكراسة <span className="num">{sar(auction.bookletPriceMinorUnits, 'ar')}</span>
            </div>
          </div>
        </div>
      </div>

      {auction.bidderVisibility === 'Named' && (
        // Said before the deposit, not after. The administrator may run an auction
        // in which bidders are named to each other (D-22), and someone about to
        // commit a hundred thousand riyals is entitled to know that first.
        <div className="notice info">
          في هذا المزاد يظهر اسم المزايد الأعلى لبقية المزايدين وللعامة.
        </div>
      )}

      {!session && (
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

      {session && canBid && (
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

      {session && canBid && eligible && (
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
        <h2>قطع الأرض ({auction.plots.length})</h2>
        <p className="muted small" style={{ marginTop: -8 }}>
          تُباع القطع كوحدة واحدة لا تُجزَّأ — المزايدة على المزاد كاملاً.
        </p>
        <table>
          <thead>
            <tr>
              <th>رقم الصك</th>
              <th>المساحة (م²)</th>
              <th>الموقع</th>
              <th>الوصف</th>
            </tr>
          </thead>
          <tbody>
            {auction.plots.map((p) => (
              <tr key={p.id}>
                <td className="num">{p.deedNumber}</td>
                <td className="num">{p.areaSqm}</td>
                <td className="num small">
                  {p.latitude && p.longitude ? (
                    <a
                      href={`https://www.google.com/maps?q=${p.latitude},${p.longitude}`}
                      target="_blank"
                      rel="noreferrer noopener"
                    >
                      {p.latitude}, {p.longitude}
                    </a>
                  ) : (
                    '—'
                  )}
                </td>
                <td className="small">{p.descriptionAr ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}
