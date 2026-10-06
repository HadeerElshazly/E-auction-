import { useEffect, useState } from 'react'
import { sar, useStepUp, type Api, type Session } from '@eauction/shared'
import { authConfig } from './authConfig'
import type { AuctionDetail, Bidder, Subscription } from './types'

interface Props {
  auction: AuctionDetail
  session: Session
  subscription: Subscription | null
  bidder: Bidder | null
  participant: Api
  onChanged: () => Promise<void>
  onError: (message: string | null) => void
}

/**
 * The path to being allowed to bid: register, subscribe, buy the booklet, accept the
 * terms, choose a deposit method, pay. The participant service enforces the order,
 * so this shows one step at a time rather than a form that can be filled out of
 * sequence and rejected.
 *
 * The two money steps are asynchronous. Pressing them asks the payment service for
 * the money and returns 202; the step completes when the gateway settles, which
 * arrives as a change in the subscription rather than as a reply. So those two show
 * a waiting state and poll, instead of pretending the bank is in the same process.
 */
export function SubscriptionSteps({
  auction,
  session,
  subscription,
  bidder,
  participant,
  onChanged,
  onError,
}: Props) {
  const [busy, setBusy] = useState(false)
  const [phone, setPhone] = useState('')
  const [email, setEmail] = useState('')

  // Asked for and not yet answered. Derived from the subscription rather than kept
  // in local state so that it survives a reload — a bidder who refreshes the page
  // mid-payment has to see the same thing, not the button again.
  const awaitingBooklet =
    subscription?.status === 'Draft' && subscription.bookletRequestedAt !== null
  const awaitingDepositPayment =
    subscription?.status === 'AwaitingDeposit' &&
    subscription.depositMethod === 'Payment' &&
    subscription.depositRequestedAt !== null
  const awaitingGateway = awaitingBooklet || awaitingDepositPayment

  // The settlement arrives on a topic, so nothing tells this page about it. Poll
  // while something is in flight and not otherwise: a portal that polls a bidder's
  // subscription for the whole auction costs the participant service a request per
  // second per person in the hall for no information.
  useEffect(() => {
    if (!awaitingGateway) return
    const t = window.setInterval(() => void onChanged(), 2000)
    return () => window.clearInterval(t)
  }, [awaitingGateway, onChanged])

  // Registration and the deposit require a second factor confirmed in the last few
  // minutes. The runner turns the service's refusal into a confirmation the user
  // can complete, rather than a 403 they can do nothing about.
  const stepUp = useStepUp(authConfig)

  const act = async (label: string, work: () => Promise<unknown>) => {
    setBusy(true)
    onError(null)
    try {
      await stepUp.run(label, work)
      await onChanged()
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // Nafath establishes who someone is; it does not supply how to reach them. The
  // deposit cannot be confirmed without contact details, because an award letter
  // has to go somewhere — the participant service refuses with "The bidder's
  // profile is incomplete", which is how this step came to be missing in the first
  // place.
  const needsProfile = bidder !== null && !bidder.profileComplete
  const status = subscription?.status ?? (bidder ? 'none' : 'unregistered')

  if (subscription?.status === 'Eligible') {
    return (
      <div className="card">
        <h2>مؤهّل للمزايدة ✓</h2>
        <p className="muted small">
          سُدّد التأمين وقُبلت الشروط. مفتاح التوقيع الخاص بك رقم{' '}
          <span className="num">{subscription.keyEpoch}</span>.
        </p>
      </div>
    )
  }

  if (subscription?.status === 'Revoked') {
    return (
      <div className="card">
        <h2>أُلغي الاشتراك</h2>
        <div className="notice error">{subscription.revocationReason ?? 'بدون سبب مسجّل'}</div>
      </div>
    )
  }

  return (
    <div className="card">
      <h2>خطوات التأهّل للمزايدة</h2>

      <ol className="steps" style={{ marginBottom: 16 }}>
        <Step done={bidder !== null} text="التسجيل بالهوية الوطنية" />
        <Step done={bidder?.profileComplete === true} text="بيانات التواصل" />
        <Step done={subscription !== null} text="الاشتراك في المزاد" />
        <Step
          done={subscription?.bookletPurchasedAt != null}
          text={`شراء كراسة الشروط — ${sar(auction.bookletPriceMinorUnits, 'ar')}`}
        />
        <Step
          done={subscription?.termsAcceptedAt != null}
          text="الموافقة على الشروط والأحكام"
        />
        <Step done={subscription?.depositMethod != null} text="اختيار طريقة التأمين" />
        <Step
          done={subscription?.depositPaidAt != null}
          text={`سداد التأمين — ${sar(auction.depositMinorUnits, 'ar')}`}
        />
      </ol>

      {/* PayTabs and SADAD need merchant accounts the contract has not produced, so
          a simulator stands behind the payment service. Said plainly rather than
          hidden: a portal that looks like it took a payment and did not is worse
          than one that admits it. */}
      <div className="notice info small">
        بوابة الدفع (مدى / سداد) لم تُربط بعد — تُنفَّذ المدفوعات عبر بوابة محاكاة.
      </div>

      {subscription?.paymentFailureReason != null && !awaitingGateway && (
        <div className="notice error small" role="alert">
          تعذّر إتمام الدفع
          {subscription.paymentFailurePurpose === 'Booklet'
            ? ' (كراسة الشروط)'
            : ' (التأمين)'}
          : <span className="ltr">{subscription.paymentFailureReason}</span>. يمكنك
          المحاولة مرة أخرى.
        </div>
      )}

      {awaitingGateway && (
        <div className="notice info small" aria-live="polite">
          بانتظار تأكيد بوابة الدفع
          {awaitingBooklet ? ' لرسوم كراسة الشروط' : ' لمبلغ التأمين'}… تُحدَّث هذه
          الصفحة تلقائياً.
        </div>
      )}

      {needsProfile && (
        <div className="grid" style={{ marginBottom: 14 }}>
          <label>
            <span>رقم الجوال</span>
            <input
              className="ltr"
              inputMode="tel"
              value={phone}
              aria-label="رقم الجوال"
              placeholder="+9665XXXXXXXX"
              onChange={(e) => setPhone(e.target.value)}
            />
          </label>
          <label>
            <span>البريد الإلكتروني</span>
            <input
              className="ltr"
              inputMode="email"
              value={email}
              aria-label="البريد الإلكتروني"
              onChange={(e) => setEmail(e.target.value)}
            />
          </label>
        </div>
      )}

      <div className="row">
        {status === 'unregistered' && (
          <button
            className="primary"
            disabled={busy}
            onClick={() =>
              // No body: identity comes from the Nafath claims in the token.
              act('register', () => participant.post('/bidders/register'))
            }
          >
            التسجيل بالهوية الوطنية
          </button>
        )}

        {needsProfile && (
          <button
            className="primary"
            disabled={busy || phone.trim() === '' || !email.includes('@')}
            onClick={() =>
              act('profile', () =>
                participant.post(`/bidders/${session.subject}/profile`, {
                  phone: phone.trim(),
                  email: email.trim(),
                }),
              )
            }
          >
            حفظ بيانات التواصل
          </button>
        )}

        {status === 'none' && !needsProfile && (
          <button
            className="primary"
            disabled={busy}
            onClick={() =>
              act('subscribe', () =>
                participant.post(`/auctions/${auction.id}/subscriptions`, {
                  bidderId: session.subject,
                }),
              )
            }
          >
            الاشتراك في المزاد
          </button>
        )}

        {subscription?.status === 'Draft' && !awaitingBooklet && (
          <button
            className="primary"
            disabled={busy}
            onClick={() =>
              act('booklet', () =>
                participant.post(
                  `/auctions/${auction.id}/subscriptions/${session.subject}/booklet`,
                ),
              )
            }
          >
            شراء كراسة الشروط
          </button>
        )}

        {subscription?.status === 'BookletPurchased' && (
          <button
            className="primary"
            disabled={busy}
            onClick={() =>
              act('terms', () =>
                participant.post(
                  `/auctions/${auction.id}/subscriptions/${session.subject}/terms`,
                ),
              )
            }
          >
            أوافق على الشروط والأحكام
          </button>
        )}

        {subscription?.status === 'TermsAccepted' && (
          <>
            <button
              className="primary"
              disabled={busy}
              onClick={() =>
                act('deposit-method', () =>
                  participant.post(
                    `/auctions/${auction.id}/subscriptions/${session.subject}/deposit-method`,
                    { method: 'Payment' },
                  ),
                )
              }
            >
              سداد التأمين إلكترونياً
            </button>
            <button
              disabled={busy}
              onClick={() =>
                act('deposit-method', () =>
                  participant.post(
                    `/auctions/${auction.id}/subscriptions/${session.subject}/deposit-method`,
                    { method: 'BankGuarantee' },
                  ),
                )
              }
            >
              رفع ضمان بنكي
            </button>
          </>
        )}

        {subscription?.status === 'AwaitingDeposit' &&
          !awaitingDepositPayment &&
          (subscription.depositMethod === 'Payment' ? (
            <button
              className="primary"
              disabled={busy}
              onClick={() =>
                act('deposit', () =>
                  participant.post(
                    `/auctions/${auction.id}/subscriptions/${session.subject}/deposit`,
                  ),
                )
              }
            >
              دفع مبلغ التأمين — {sar(auction.depositMinorUnits, 'ar')}
            </button>
          ) : (
            <button
              className="primary"
              disabled={busy}
              onClick={() =>
                act('guarantee', () =>
                  participant.post(
                    `/auctions/${auction.id}/subscriptions/${session.subject}/guarantee`,
                    {
                      documentId: crypto.randomUUID(),
                      // The guarantee must outlast the auction, which the service
                      // checks; a month past the close is the usual ask.
                      expiresAt: new Date(
                        new Date(auction.endsAt).getTime() + 30 * 864e5,
                      ).toISOString(),
                    },
                  ),
                )
              }
            >
              رفع الضمان البنكي
            </button>
          ))}

        {subscription?.status === 'AwaitingDeposit' &&
          subscription.depositMethod === 'BankGuarantee' &&
          subscription.guaranteeDocumentId !== null && (
            <span className="muted small">
              بانتظار تحقّق الإدارة من الضمان البنكي.
            </span>
          )}
      </div>
    </div>
  )
}

function Step({ done, text }: { done: boolean; text: string }) {
  return (
    <li>
      <span className={`tick ${done ? 'done' : 'todo'}`}>{done ? '✓' : '○'}</span>
      <span className={done ? '' : 'muted'}>{text}</span>
    </li>
  )
}
