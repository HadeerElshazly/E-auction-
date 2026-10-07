import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, PageHead, api, config, useStepUp, type Session } from '@eauction/shared'
import { authConfig } from './authConfig'
import type { Bidder } from './types'

interface Props {
  session: Session
  onBack: () => void
}

/**
 * ملفي — the bidder's own record, outside any one auction.
 *
 * What Nafath supplied (name, national id) is shown and not editable: it is the
 * identity the account is bound to, and changing it is not the bidder's to do here.
 * What Nafath does not supply — how to reach them — is theirs to keep current, and
 * is what blocks participation while it is missing.
 */
export function Profile({ session, onBack }: Props) {
  const participant = useMemo(() => api({ baseUrl: config.participantApi, session }), [session])
  const stepUp = useStepUp(authConfig)

  const [bidder, setBidder] = useState<Bidder | null | undefined>(undefined)
  const [phone, setPhone] = useState('')
  const [email, setEmail] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const b = await participant.get<Bidder>(`/bidders/${session.subject}`)
      setBidder(b)
      setPhone(b.phone ?? '')
      setEmail(b.email ?? '')
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setBidder(null)
      else setMessage({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }, [participant, session.subject])

  useEffect(() => {
    void load()
  }, [load])

  const act = async (label: string, work: () => Promise<unknown>, done: string) => {
    setBusy(true)
    setMessage(null)
    try {
      await stepUp.run(label, work)
      await load()
      setMessage({ ok: true, text: done })
    } catch (e) {
      setMessage({
        ok: false,
        text:
          e instanceof ApiError && e.problems.length > 0
            ? e.problems.join(' · ')
            : e instanceof Error
              ? e.message
              : String(e),
      })
    } finally {
      setBusy(false)
    }
  }

  const changed = bidder != null && (phone.trim() !== (bidder.phone ?? '') || email.trim() !== (bidder.email ?? ''))

  return (
    <>
      <PageHead
        eyebrow="مساحة المزايد"
        title="الملف الشخصي"
        sub="بيانات المزايد وقنوات التواصل."
        action={<button onClick={onBack}>جميع المزادات</button>}
      />

      <div className="card profile-card">
        <div className="section-head">
          <h2>{bidder?.nameAr ?? session.nameAr ?? session.name}</h2>
          {bidder && (
            <span className={`pill ${bidder.profileComplete ? 'live' : 'wait'}`}>
              {bidder.profileComplete ? 'الملف مكتمل' : 'الملف غير مكتمل'}
            </span>
          )}
        </div>
        <p className="lede">
          بيانات الهوية مأخوذة من نفاذ ولا تُعدَّل من هنا. بيانات التواصل مطلوبة لاعتماد
          مشاركتك في أي مزاد، وإليها تُرسل الإشعارات وخطاب الترسية.
        </p>

        {message && (
          <div className={`notice ${message.ok ? 'ok' : 'error'}`} role="status">
            {message.text}
          </div>
        )}

        {bidder === undefined && <p className="muted">…</p>}

        {bidder === null && (
          <>
            <div className="notice info">
              لم تُسجَّل بعد كمزايد. التسجيل يربط هويتك الوطنية من نفاذ بحسابك، ويتطلب
              تأكيد هويتك.
            </div>
            <button
              className="primary"
              disabled={busy}
              onClick={() =>
                void act('register', () => participant.post('/bidders/register'), 'تم التسجيل.')
              }
            >
              التسجيل بالهوية الوطنية
            </button>
          </>
        )}

        {bidder && (
          <>
            <h3>الهوية (من نفاذ)</h3>
            <div className="stat-grid">
              <div className="stat">
                <div className="stat-label">الاسم</div>
                <div className="stat-value" style={{ fontSize: 17 }}>{bidder.nameAr}</div>
                {bidder.nameEn && <div className="stat-sub ltr">{bidder.nameEn}</div>}
              </div>
              <div className="stat">
                <div className="stat-label">رقم الهوية</div>
                <div className="stat-value num" style={{ fontSize: 17 }}>
                  {session.nationalId ?? '—'}
                </div>
              </div>
              <div className="stat">
                <div className="stat-label">التحقق عبر نفاذ</div>
                <div className="stat-value" style={{ fontSize: 17 }}>
                  {bidder.verified ? 'تم التحقق ✓' : 'لم يتم'}
                </div>
              </div>
            </div>

            <h3>بيانات التواصل</h3>
            {!bidder.profileComplete && (
              <div className="notice info small">
                أكمل رقم الجوال والبريد الإلكتروني — لا يمكن اعتماد مشاركتك في أي مزاد
                قبل اكتمالها.
              </div>
            )}
            <div className="grid">
              <label>
                <span>رقم الجوال</span>
                <input
                  className="ltr"
                  inputMode="tel"
                  value={phone}
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
                  onChange={(e) => setEmail(e.target.value)}
                />
              </label>
            </div>
            <button
              className="primary"
              disabled={busy || !changed || phone.trim() === '' || !email.includes('@')}
              onClick={() =>
                void act(
                  'profile',
                  () =>
                    participant.post(`/bidders/${session.subject}/profile`, {
                      phone: phone.trim(),
                      email: email.trim(),
                    }),
                  'حُفظت بيانات التواصل.',
                )
              }
            >
              حفظ التغييرات
            </button>
          </>
        )}
      </div>
    </>
  )
}
