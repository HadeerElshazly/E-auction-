import { useState } from 'react'
import { sar } from '@eauction/shared'
import type { AuctionListItem } from './types'
import { label } from './types'

interface Props {
  auctions: AuctionListItem[]
  canCreate: boolean
  busy: boolean
  onOpen: (id: string) => void
  onCreate: (nameAr: string, nameEn: string) => void
}

export function AuctionList({ auctions, canCreate, busy, onOpen, onCreate }: Props) {
  const [nameAr, setNameAr] = useState('')
  const [nameEn, setNameEn] = useState('')
  const [creating, setCreating] = useState(false)

  return (
    <>
      {canCreate && (
        <div className="card">
          <h2>مزاد جديد</h2>
          {creating ? (
            <>
              <div className="grid">
                <label>
                  <span>اسم المزاد (عربي)</span>
                  <input
                    value={nameAr}
                    onChange={(e) => setNameAr(e.target.value)}
                    placeholder="مخطط السعيد — المرحلة الأولى"
                    aria-label="اسم المزاد بالعربي"
                  />
                </label>
                <label>
                  <span>اسم المزاد (إنجليزي)</span>
                  <input
                    className="ltr"
                    value={nameEn}
                    onChange={(e) => setNameEn(e.target.value)}
                    placeholder="Al-Saeed plan — phase one"
                    aria-label="Auction name in English"
                  />
                </label>
              </div>
              <div className="row end">
                <button onClick={() => setCreating(false)}>إلغاء</button>
                <button
                  className="primary"
                  disabled={busy || nameAr.trim() === '' || nameEn.trim() === ''}
                  onClick={() => {
                    onCreate(nameAr.trim(), nameEn.trim())
                    setNameAr('')
                    setNameEn('')
                    setCreating(false)
                  }}
                >
                  إنشاء مسودة
                </button>
              </div>
            </>
          ) : (
            <button className="primary" onClick={() => setCreating(true)}>
              + إنشاء مزاد
            </button>
          )}
        </div>
      )}

      <div className="card">
        <h2>المزادات ({auctions.length})</h2>

        {auctions.length === 0 ? (
          <p className="muted small">لا توجد مزادات بعد.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>الحالة</th>
                <th>الاسم</th>
                <th>القطع</th>
                <th>سعر الافتتاح</th>
                <th>البداية</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {auctions.map((a) => {
                const l = label(a.status)
                return (
                  <tr key={a.id}>
                    <td>
                      <span className={`pill ${l.tone}`}>{l.ar}</span>
                    </td>
                    <td>
                      <div>{a.nameAr}</div>
                      <div className="muted small ltr">{a.nameEn}</div>
                    </td>
                    <td className="num">{a.plotCount}</td>
                    <td className="num">{sar(a.openingPriceMinorUnits, 'ar')}</td>
                    <td className="num small">
                      {a.startsAt ? new Date(a.startsAt).toLocaleString('ar-SA') : '—'}
                    </td>
                    <td>
                      <button onClick={() => onOpen(a.id)}>فتح</button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </div>
    </>
  )
}
