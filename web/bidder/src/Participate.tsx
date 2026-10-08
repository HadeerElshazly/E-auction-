import { useEffect, useState } from 'react'
import { ApiError, Icon, api, config, sar, useStepUp, type Api, type Session } from '@eauction/shared'
import { authConfig } from './authConfig'
import type { AuctionDetail, Bidder, Subscription } from './types'

/**
 * «اشترك في المزاد» — the prototype's dialog: 01 الملف الشخصي ← 02 الشروط ← 03 التأمين,
 * then «أصبحت جاهزاً للمشاركة».
 *
 * Driven by the participant service's state of this bidder's application, not by a
 * local wizard: closed half way, or waiting on the payment gateway, it opens again
 * on the step the application is actually at. The two payments settle through the
 * gateway and arrive as a change in the subscription, so those steps wait and poll.
 */
export function ParticipateDialog({
  auction,
  session,
  bidder,
  subscription,
  participant,
  onChanged,
  onClose,
}: {
  auction: AuctionDetail
  session: Session
  bidder: Bidder | null
  subscription: Subscription | null
  participant: Api
  onChanged: () => Promise<void>
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [phone, setPhone] = useState('')
  const [email, setEmail] = useState('')
  const [agreed, setAgreed] = useState(false)
  const [method, setMethod] = useState<'Payment' | 'BankGuarantee'>(
    subscription?.depositMethod === 'BankGuarantee' ? 'BankGuarantee' : 'Payment',
  )
  const [file, setFile] = useState<File | null>(null)
  const stepUp = useStepUp(authConfig)
  const documents = api({ baseUrl: config.documentsApi, session })
  const base = `/auctions/${auction.id}/subscriptions/${session.subject}`

  const s = subscription?.status
  const awaitingBooklet = s === 'Draft' && subscription?.bookletRequestedAt != null
  const awaitingDeposit =
    s === 'AwaitingDeposit' && subscription?.depositMethod === 'Payment' && subscription?.depositRequestedAt != null
  useEffect(() => {
    if (!awaitingBooklet && !awaitingDeposit) return
    const t = window.setInterval(() => void onChanged(), 2000)
    return () => window.clearInterval(t)
  }, [awaitingBooklet, awaitingDeposit, onChanged])

  const run = async (label: string, work: () => Promise<unknown>) => {
    setBusy(true)
    setProblem(null)
    try {
      await stepUp.run(label, work)
      await onChanged()
    } catch (e) {
      setProblem(
        e instanceof ApiError && e.problems.length > 0 ? e.problems.join(' ') : e instanceof Error ? e.message : String(e),
      )
    } finally {
      setBusy(false)
    }
  }

  // Where the application stands, as the three steps of the prototype.
  const profileDone = bidder !== null && bidder.profileComplete
  const termsDone = subscription?.termsAcceptedAt != null
  const step: 1 | 2 | 3 | 'done' | 'review' =
    s === 'Eligible'
      ? 'done'
      : subscription?.guaranteeDocumentId != null && subscription.eligibility === 'UnderReview'
        ? 'review'
        : !profileDone
          ? 1
          : !termsDone
            ? 2
            : 3
  const free = auction.bookletPriceMinorUnits === 0

  // «متابعة إلى التأمين»: whatever is left of subscribe → booklet → terms. The booklet
  // is free or already paid by the time this is pressed; a paid one is bought first.
  const continueToDeposit = () =>
    run('terms', async () => {
      if (!subscription) await participant.post(`/auctions/${auction.id}/subscriptions`, { bidderId: session.subject })
      const fresh = await participant.get<Subscription>(base)
      if (fresh.status === 'Draft') {
        await participant.post(`${base}/booklet`)
        if (!free) return // the gateway settles it; the dialog waits
        for (let i = 0; i < 20; i++) {
          const now = await participant.get<Subscription>(base)
          if (now.status === 'BookletPurchased') break
          await new Promise((r) => setTimeout(r, 500))
        }
      }
      await participant.post(`${base}/terms`)
    })

  const payDeposit = () =>
    run('deposit', async () => {
      if (subscription?.depositMethod !== 'Payment') await participant.post(`${base}/deposit-method`, { method: 'Payment' })
      await participant.post(`${base}/deposit`)
    })

  const sendGuarantee = () =>
    run('guarantee', async () => {
      if (subscription?.depositMethod !== 'BankGuarantee')
        await participant.post(`${base}/deposit-method`, { method: 'BankGuarantee' })
      if (!file) throw new Error('اختر ملف الضمان البنكي.')
      const uploaded = await documents.upload<{ id: string }>('/documents', file, { access: 'Private' })
      await participant.post(`${base}/guarantee`, {
        documentId: uploaded.id,
        // The guarantee must outlast the auction — a month past the close.
        expiresAt: new Date(new Date(auction.endsAt ?? Date.now()).getTime() + 30 * 864e5).toISOString(),
      })
    })

  const title =
    step === 'done' ? 'أصبحت جاهزاً للمشاركة' : step === 'review' ? 'أُرسل الضمان للمراجعة' : step === 3 ? 'تأمين المشاركة' : step === 1 ? 'إكمال الملف الشخصي' : `المشاركة في ${auction.nameAr}`

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={title}>
      <div className="modal narrow participate">
        <div className="modal-head">
          <div className="grow">
            <h2>{title}</h2>
          </div>
          <button className="icon-btn" aria-label="إغلاق" onClick={onClose}>
            <Icon name="close" size={18} />
          </button>
        </div>

        {typeof step === 'number' && (
          <div className="join-steps">
            {['الملف الشخصي', 'الشروط', 'التأمين'].map((label, i) => (
              <span key={label} className={`join-step${i + 1 < step ? ' done' : i + 1 === step ? ' active' : ''}`}>
                <span className="num">0{i + 1}</span> {label}
              </span>
            ))}
          </div>
        )}

        {problem && <div className="notice error small">{problem}</div>}

        {step === 1 &&
          (bidder === null ? (
            <>
              <div className="confirmation">
                <div className="success-icon"><Icon name="shield" size={28} /></div>
                <p>التسجيل بالهوية الوطنية من نفاذ — الاسم ورقم الهوية يصلان من نفاذ ولا يُكتبان.</p>
              </div>
              <button className="primary wide" disabled={busy} onClick={() => void run('register', () => participant.post('/bidders/register'))}>
                التسجيل والمتابعة
              </button>
            </>
          ) : (
            <>
              <p className="muted small">بيانات التواصل لإرسال الإشعارات وخطاب الترسية.</p>
              <div className="form-row-2">
                <label>
                  <span>رقم الجوال</span>
                  <input className="ltr" inputMode="tel" value={phone} placeholder="+9665XXXXXXXX" onChange={(e) => setPhone(e.target.value)} aria-label="رقم الجوال" />
                </label>
                <label>
                  <span>البريد الإلكتروني</span>
                  <input className="ltr" inputMode="email" value={email} onChange={(e) => setEmail(e.target.value)} aria-label="البريد الإلكتروني" />
                </label>
              </div>
              <button
                className="primary wide"
                style={{ marginTop: 16 }}
                disabled={busy || phone.trim() === '' || !email.includes('@')}
                onClick={() =>
                  void run('profile', () => participant.post(`/bidders/${session.subject}/profile`, { phone: phone.trim(), email: email.trim() }))
                }
              >
                حفظ الملف والمتابعة
              </button>
            </>
          ))}

        {step === 2 && (
          <>
            <div className="kv"><span>مساحة القطعة</span><b><span className="num">{auction.totalAreaSqm}</span> م²</b></div>
            <div className="kv"><span>قيمة التأمين</span><b className="num">{sar(auction.depositMinorUnits, 'ar')}</b></div>
            <div className="kv"><span>رسوم الكراسة</span><b>{free ? 'مجانية' : sar(auction.bookletPriceMinorUnits, 'ar')}</b></div>
            <div className="kv"><span>السعي (الوساطة)</span><b className="num">{auction.brokerageFeePercent ?? 0}% من سعر الترسية</b></div>
            <div className="document-row">
              <span className="doc-icon"><Icon name="file" /></span>
              <div className="grow">
                <strong>كراسة الشروط</strong>
                <small>
                  {subscription?.bookletPurchasedAt != null
                    ? 'متاحة للتنزيل'
                    : free
                      ? 'مجانية — تُتاح بعد المتابعة'
                      : 'تُتاح بعد شرائها'}
                </small>
              </div>
              {subscription?.bookletPurchasedAt != null ? (
                <BookletButton auction={auction} session={session} participant={participant} onError={setProblem} />
              ) : (
                !free && (
                  <button
                    className="small"
                    disabled={busy || awaitingBooklet}
                    onClick={() =>
                      void run('booklet', async () => {
                        if (!subscription) await participant.post(`/auctions/${auction.id}/subscriptions`, { bidderId: session.subject })
                        await participant.post(`${base}/booklet`)
                      })
                    }
                  >
                    {awaitingBooklet ? 'بانتظار تأكيد الدفع…' : `شراء — ${sar(auction.bookletPriceMinorUnits, 'ar')}`}
                  </button>
                )
              )}
            </div>
            <label className="check-row" style={{ margin: '16px 0' }}>
              <input type="checkbox" checked={agreed} onChange={(e) => setAgreed(e.target.checked)} />
              <span>اطلعت على كراسة الشروط وأوافق عليها.</span>
            </label>
            <button
              className="primary wide"
              disabled={busy || !agreed || (!free && subscription?.bookletPurchasedAt == null)}
              onClick={() => void continueToDeposit()}
            >
              متابعة إلى التأمين
            </button>
          </>
        )}

        {step === 3 && (
          <>
            <div className="confirmation">
              <small className="muted">تأمين مزاد {auction.nameAr}</small>
              <strong className="num">{sar(auction.depositMinorUnits, 'ar')}</strong>
            </div>
            <div className="choice">
              <button className={method === 'Payment' ? 'selected' : ''} onClick={() => setMethod('Payment')}>
                <Icon name="wallet" size={18} /> دفع إلكتروني
              </button>
              <button className={method === 'BankGuarantee' ? 'selected' : ''} onClick={() => setMethod('BankGuarantee')}>
                <Icon name="file" size={18} /> ضمان بنكي
              </button>
            </div>
            {subscription?.eligibility === 'Rejected' && (
              <div className="notice error small">رُفض الضمان السابق: {subscription.eligibilityReason}. يمكنك رفع ضمان آخر.</div>
            )}
            {subscription?.paymentFailureReason && !awaitingDeposit && (
              <div className="notice error small">لم يكتمل الدفع: <span className="ltr">{subscription.paymentFailureReason}</span>. حاول مرة أخرى.</div>
            )}
            {method === 'Payment' ? (
              <>
                <div className="notice info small">يُنفَّذ الدفع عبر بوابة الدفع (محاكاة في هذه البيئة).</div>
                <button className="primary wide" disabled={busy || awaitingDeposit} onClick={() => void payDeposit()}>
                  {awaitingDeposit ? 'بانتظار تأكيد بوابة الدفع…' : `دفع التأمين — ${sar(auction.depositMinorUnits, 'ar')}`}
                </button>
              </>
            ) : (
              <>
                <label style={{ display: 'block' }}>
                  <span className="strong">ملف الضمان البنكي</span>
                  <input
                    type="file"
                    accept="application/pdf,image/*"
                    aria-label="ملف الضمان البنكي"
                    onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                    style={{ width: '100%', marginTop: 8 }}
                  />
                  <small className="muted">PDF أو صورة. تراجع إدارة المزاد الضمان قبل اعتماد المشاركة.</small>
                </label>
                <button className="primary wide" style={{ marginTop: 14 }} disabled={busy || !file} onClick={() => void sendGuarantee()}>
                  إرسال الضمان للمراجعة
                </button>
              </>
            )}
          </>
        )}

        {step === 'done' && (
          <>
            <div className="confirmation">
              <div className="success-icon"><Icon name="check" size={28} /></div>
              <p>تم تأكيد التأمين والأهلية. يمكنك المزايدة عند فتح المزاد.</p>
            </div>
            <button className="primary wide" onClick={onClose}>
              الانتقال إلى المزاد
            </button>
          </>
        )}

        {step === 'review' && (
          <>
            <div className="confirmation">
              <div className="success-icon"><Icon name="clock" size={28} /></div>
              <p>تراجع إدارة المزاد ضمانك البنكي، وتصلك النتيجة في الإشعارات.</p>
            </div>
            <button className="primary wide" onClick={onClose}>
              متابعة حالة المشاركة
            </button>
          </>
        )}
      </div>
    </div>
  )
}

/**
 * كراسة الشروط for a bidder who has it. Restricted in the document service, so it is
 * fetched with a grant the participant service gives only to this bidder.
 */
export function BookletButton({
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
    try {
      const { documentId, grant } = await participant.get<{ documentId: string; grant: string }>(
        `/auctions/${auction.id}/subscriptions/${session.subject}/booklet-grant`,
      )
      await documents.download(`/documents/${documentId}?grant=${encodeURIComponent(grant)}`, `كراسة-الشروط-${auction.id}.pdf`)
    } catch (e) {
      onError(
        e instanceof ApiError && e.reason === 'BookletNotPurchased'
          ? 'لم يكتمل شراء كراسة الشروط بعد.'
          : 'تعذّر تنزيل كراسة الشروط. حاول مرة أخرى بعد قليل.',
      )
    } finally {
      setBusy(false)
    }
  }
  return (
    <button className="small" disabled={busy} onClick={() => void open()}>
      <Icon name="download" size={16} /> تنزيل
    </button>
  )
}
