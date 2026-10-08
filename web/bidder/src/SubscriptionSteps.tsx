import { useEffect, useRef, useState } from 'react'
import { ApiError, api, config, sar, useStepUp, when, type Api, type Session } from '@eauction/shared'
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

  // The document service, for the bank guarantee the bidder uploads and the
  // booklet they read. Its own client because it is its own origin — which is
  // also why it is in the endpoint table that drives the Content-Security-Policy.
  const documents = api({ baseUrl: config.documentsApi, session })
  const guaranteeInput = useRef<HTMLInputElement>(null)

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

  const freeBooklet = auction.bookletPriceMinorUnits === 0

  if (subscription?.status === 'Eligible') {
    return (
      <div className="card">
        <div className="section-head">
          <h2>مؤهّل للمزايدة</h2>
          <EligibilityPill subscription={subscription} />
        </div>
        <p className="muted small">
          سُدّد التأمين وقُبلت الشروط. مفتاح التوقيع الخاص بك رقم{' '}
          <span className="num">{subscription.keyEpoch}</span>.
        </p>
        <TermsRecord subscription={subscription} />
        <Booklet
          auction={auction}
          session={session}
          participant={participant}
          onError={onError}
        />
      </div>
    )
  }

  if (subscription?.status === 'Revoked') {
    return (
      <div className="card">
        <div className="section-head">
          <h2>طلب المشاركة</h2>
          <EligibilityPill subscription={subscription} />
        </div>
        <div className="notice error">
          <strong>سبب الرفض: </strong>
          {subscription.eligibilityReason ?? 'بدون سبب مسجّل'}
        </div>
      </div>
    )
  }

  return (
    <div className="card">
      <div className="section-head">
        <h2>خطوات التأهّل للمزايدة</h2>
        {subscription && <EligibilityPill subscription={subscription} />}
      </div>

      {subscription?.eligibility === 'Rejected' && (
        <div className="notice error small" role="alert">
          <strong>رُفض الضمان البنكي: </strong>
          {subscription.eligibilityReason}. يمكنك رفع ضمان آخر أدناه.
        </div>
      )}

      {subscription?.eligibility === 'UnderReview' && (
        <div className="notice info small" aria-live="polite">
          {subscription.depositMethod === 'BankGuarantee'
            ? 'طلبك قيد المراجعة — تتحقّق إدارة المزاد من الضمان البنكي، وتظهر النتيجة في هذه الصفحة.'
            : 'طلبك قيد المراجعة — بانتظار تأكيد سداد التأمين.'}
        </div>
      )}

      <StepBar
        stages={[
          { label: 'الهوية والملف', done: bidder !== null && bidder.profileComplete === true },
          {
            label: 'الكراسة والشروط',
            done: subscription?.bookletPurchasedAt != null && subscription?.termsAcceptedAt != null,
          },
          { label: 'التأمين', done: subscription?.depositPaidAt != null },
        ]}
      />

      <ol className="steps" style={{ marginBottom: 16 }}>
        <Step done={bidder !== null} text="التسجيل بالهوية الوطنية" />
        <Step done={bidder?.profileComplete === true} text="بيانات التواصل" />
        <Step done={subscription !== null} text="الاشتراك في المزاد" />
        <Step
          done={subscription?.bookletPurchasedAt != null}
          text={
            freeBooklet
              ? 'الحصول على كراسة الشروط — مجاناً'
              : `شراء كراسة الشروط — ${sar(auction.bookletPriceMinorUnits, 'ar')}`
          }
        />
        <Step
          done={subscription?.termsAcceptedAt != null}
          text={
            subscription?.termsAcceptedAt
              ? `الموافقة على الشروط والأحكام — ${when(subscription.termsAcceptedAt)}`
              : 'الموافقة على الشروط والأحكام'
          }
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

      {subscription?.bookletPurchasedAt != null && (
        <Booklet
          auction={auction}
          session={session}
          participant={participant}
          onError={onError}
        />
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
            {freeBooklet ? 'الحصول على كراسة الشروط (مجاناً)' : 'شراء كراسة الشروط'}
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
            <>
              <input
                ref={guaranteeInput}
                type="file"
                accept="application/pdf,image/*"
                // Distinct from the button's text: an input[type=file] has role
                // `button` too, so the same name on both makes
                // getByRole('button', { name }) ambiguous.
                aria-label="ملف الضمان البنكي"
                // Hidden rather than display:none, so it stays focusable and a
                // test can set files on it.
                style={{ position: 'absolute', width: 1, height: 1, opacity: 0 }}
                onChange={(e) => {
                  const file = e.target.files?.[0]
                  if (!file) return

                  void act('guarantee', async () => {
                    // Private, not Restricted: this is the bidder's own document
                    // and an administrator has to be able to look at it to verify
                    // it. Uploaded first, so the id the participant service
                    // records is one that resolves.
                    const uploaded = await documents.upload<{ id: string }>(
                      '/documents',
                      file,
                      { access: 'Private' },
                    )

                    return participant.post(
                      `/auctions/${auction.id}/subscriptions/${session.subject}/guarantee`,
                      {
                        documentId: uploaded.id,
                        // The guarantee must outlast the auction, which the
                        // service checks; a month past the close is the usual ask.
                        expiresAt: new Date(
                          new Date(auction.endsAt!).getTime() + 30 * 864e5,
                        ).toISOString(),
                      },
                    )
                  })
                }}
              />
              <button
                className="primary"
                disabled={busy}
                onClick={() => guaranteeInput.current?.click()}
              >
                {subscription.guaranteeDocumentId !== null
                  ? 'استبدال الضمان البنكي'
                  : 'رفع الضمان البنكي'}
              </button>
            </>
          ))}
      </div>
    </div>
  )
}

/**
 * The link to كراسة الشروط, for a bidder who has paid for it.
 *
 * The document is Restricted in the document service, so this cannot be a plain
 * link: it asks the participant service for a grant — which that service gives
 * only to a bidder whose booklet fee has settled — and then opens the document
 * with it. The grant is good for five minutes and names this bidder, so the URL
 * it produces is no use to anyone it is forwarded to.
 */
function Booklet({
  auction,
  session,
  participant,
  onError,
}: {
  auction: AuctionDetail
  session: Session
  participant: Api
  onError: (message: string | null) => void
}) {
  const [busy, setBusy] = useState(false)
  const documents = api({ baseUrl: config.documentsApi, session })

  const open = async () => {
    setBusy(true)
    onError(null)
    let granted = false
    try {
      const { documentId, grant } = await participant.get<{
        documentId: string
        grant: string
      }>(`/auctions/${auction.id}/subscriptions/${session.subject}/booklet-grant`)
      granted = true

      // Fetched with the token, not opened in a new tab. The grant is bound to this
      // bidder, so the document service checks it against the caller's subject —
      // and a tab opened with window.open carries no Authorization header, so it
      // arrived anonymous and got a 404 every time. Holding the booklet in memory
      // as a blob for a moment is the price of the grant meaning what it says.
      await documents.download(
        `/documents/${documentId}?grant=${encodeURIComponent(grant)}`,
        `كراسة-الشروط-${auction.id}.pdf`,
      )
    } catch (e) {
      onError(granted ? 'تعذّر تنزيل كراسة الشروط. حاول مرة أخرى بعد قليل.' : bookletProblem(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="row" style={{ marginTop: 10 }}>
      <button disabled={busy} onClick={() => void open()}>
        تنزيل كراسة الشروط
      </button>
    </div>
  )
}

/**
 * What a bidder is told when the booklet will not open. Not the raw "GET … failed
 * (404)": that is a URL and a status code on the one screen with no staff on it.
 *
 * For the grant request only: it answers 404 when the auction has no booklet
 * attached and 409 when this bidder has not paid for it. A failure after the grant
 * — the download itself — is reported by the caller as worth retrying.
 */
function bookletProblem(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.reason === 'BookletNotPurchased') return 'لم يكتمل شراء كراسة الشروط بعد.'
    if (e.status === 404) return 'لم تُرفق كراسة الشروط بهذا المزاد بعد. تواصل مع إدارة المزاد.'
  }
  return 'تعذّر تنزيل كراسة الشروط. حاول مرة أخرى بعد قليل.'
}

const eligibilityAr: Record<Subscription['eligibility'], { ar: string; tone: string }> = {
  Incomplete: { ar: 'قيد الاستكمال', tone: 'done' },
  UnderReview: { ar: 'قيد المراجعة', tone: 'wait' },
  Accepted: { ar: 'مقبول', tone: 'live' },
  Rejected: { ar: 'مرفوض', tone: 'bad' },
}

function EligibilityPill({ subscription }: { subscription: Subscription }) {
  const s = eligibilityAr[subscription.eligibility] ?? eligibilityAr.Incomplete
  return <span className={`pill ${s.tone}`}>الأهلية: {s.ar}</span>
}

/**
 * What the bidder agreed to and when — the record the requirements ask the system
 * to keep, shown back to the person it binds.
 */
function TermsRecord({ subscription }: { subscription: Subscription }) {
  if (!subscription.termsAcceptedAt) return null
  return (
    <p className="muted small">
      وافقتَ على الشروط والأحكام في {when(subscription.termsAcceptedAt)}
      {subscription.acceptedBookletDocumentId && (
        <>
          {' '}— نسخة الكراسة{' '}
          <span className="mono">{subscription.acceptedBookletDocumentId.slice(0, 8)}</span>
        </>
      )}
      .
    </p>
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

/**
 * The three stages of qualifying, as a bar: done, the one in hand, and what comes
 * after. The detailed checklist below says exactly which step is next.
 */
function StepBar({ stages }: { stages: Array<{ label: string; done: boolean }> }) {
  const current = stages.findIndex((st) => !st.done)
  return (
    <div className="stage-bar" aria-label="مراحل التأهّل">
      {stages.map((st, i) => (
        <span
          key={st.label}
          className={`stage-bar-step${st.done ? ' done' : i === current ? ' active' : ''}`}
          aria-current={i === current ? 'step' : undefined}
        >
          <b className="num">{String(i + 1).padStart(2, '0')}</b> {st.label}
        </span>
      ))}
    </div>
  )
}
