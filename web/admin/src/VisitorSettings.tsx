import { useEffect, useMemo, useState } from 'react'
import { Icon, PageHead, api, config, timestamp, type Session } from '@eauction/shared'

interface Field {
  key: string
  labelAr: string
  hintAr: string
  isPublic: boolean
  publicByDefault: boolean
}

interface Settings {
  fields: Field[]
  alwaysPublic: string[]
  updatedAt: string | null
}

/**
 * «إعدادات العرض للزوار» — what a visitor who has not signed in sees of an auction.
 *
 * Some things a visitor always sees and are listed as fixed; each other group is
 * either shown to visitors or kept for those who sign in. The query service applies
 * the choice to what it answers, so a hidden figure is not sent to a visitor at all.
 */
export function VisitorSettings({ session }: { session: Session }) {
  const client = useMemo(() => api({ baseUrl: config.adminApi, session }), [session])
  const [saved, setSaved] = useState<Settings | null>(null)
  const [choice, setChoice] = useState<Record<string, boolean>>({})
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  const take = (s: Settings) => {
    setSaved(s)
    setChoice(Object.fromEntries(s.fields.map((f) => [f.key, f.isPublic])))
  }

  useEffect(() => {
    client
      .get<Settings>('/settings/public-visibility')
      .then(take)
      .catch(() => setMessage({ ok: false, text: 'تعذّر تحميل الإعدادات.' }))
  }, [client])

  if (!saved) return <p className="muted">…</p>

  const changed = saved.fields.some((f) => choice[f.key] !== f.isPublic)
  const save = async () => {
    setBusy(true)
    setMessage(null)
    try {
      take(await client.put<Settings>('/settings/public-visibility', { public: choice }))
      setMessage({ ok: true, text: 'حُفظت الإعدادات. تُطبَّق على الزوار خلال ثوانٍ.' })
    } catch {
      setMessage({ ok: false, text: 'تعذّر حفظ الإعدادات.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHead
        eyebrow="الإعدادات"
        title="إعدادات العرض للزوار"
        sub="ما يراه الزائر من المزاد قبل تسجيل الدخول، وما يظهر بعد الدخول فقط."
      />

      {message && <div className={`notice ${message.ok ? 'ok' : 'error'}`}>{message.text}</div>}

      <div className="split visitor-settings">
        <section className="card">
          <h2>بيانات يحدّد ظهورها المسؤول</h2>
          <div className="visibility-list">
            {saved.fields.map((f) => {
              const shown = choice[f.key] ?? f.isPublic
              return (
                <div key={f.key} className="visibility-row" data-testid={`visibility-${f.key}`}>
                  <div className="grow">
                    <strong>{f.labelAr}</strong>
                    <small>{f.hintAr}</small>
                  </div>
                  <div className="segmented" role="radiogroup" aria-label={f.labelAr}>
                    <button
                      role="radio"
                      aria-checked={shown}
                      className={shown ? 'active' : ''}
                      onClick={() => setChoice({ ...choice, [f.key]: true })}
                    >
                      ظاهر للزوار
                    </button>
                    <button
                      role="radio"
                      aria-checked={!shown}
                      className={!shown ? 'active' : ''}
                      onClick={() => setChoice({ ...choice, [f.key]: false })}
                    >
                      <Icon name="lock" size={14} /> بعد تسجيل الدخول
                    </button>
                  </div>
                </div>
              )
            })}
          </div>
          <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16, gap: 8 }}>
            <button
              disabled={busy}
              onClick={() =>
                setChoice(Object.fromEntries(saved.fields.map((f) => [f.key, f.publicByDefault])))
              }
            >
              الإعدادات الافتراضية
            </button>
            <button className="primary" disabled={busy || !changed} onClick={() => void save()}>
              حفظ
            </button>
          </div>
        </section>

        <aside>
          <div className="card">
            <h3 style={{ marginTop: 0 }}>يظهر للزائر دائماً</h3>
            <ul className="check-list">
              {saved.alwaysPublic.map((t) => (
                <li key={t}>
                  <Icon name="check" size={16} /> {t}
                </li>
              ))}
            </ul>
            <p className="box-help">
              <Icon name="shield" size={16} /> يُطبَّق الإعداد في الخادم: ما يُخفى لا يُرسل للزائر أصلاً. كل تغيير
              يُسجَّل في سجل المراجعة.
            </p>
            {saved.updatedAt && <p className="box-help">آخر تعديل: {timestamp(saved.updatedAt)}</p>}
          </div>
        </aside>
      </div>
    </>
  )
}
