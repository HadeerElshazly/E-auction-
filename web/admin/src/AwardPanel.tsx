import { useState } from 'react'
import { day, sar, type Api } from '@eauction/shared'
import type { Auction } from './types'

interface Props {
  auction: Auction
  client: Api
  busy: boolean
  canAct: boolean
  /**
   * The signed-in committee member's subject claim. Passed in rather than typed
   * into a form: a decision recorded under someone else's name is a decision the
   * minutes cannot be trusted on.
   */
  committeeUserId: string
  onAct: (work: () => Promise<unknown>) => Promise<void>
}

/**
 * The second workflow: approving the final result.
 *
 * None of this is automatic. The processor decides who is at the top of the ladder
 * and offers that bidder as a candidate; the committee decides whether to award to
 * them. The portal's job is to make the order the domain enforces — candidate, then
 * award, then letter, then signed letter, then notify — visible rather than
 * something a clerk discovers by getting a 409.
 */
export function AwardPanel({ auction, client, busy, canAct, committeeUserId, onAct }: Props) {
  const [reason, setReason] = useState('')
  const [forfeit, setForfeit] = useState(true)

  const before = ['Draft', 'PendingReview', 'Rejected', 'Approved', 'Scheduled', 'Live']
  if (before.includes(auction.status)) return null

  const award = auction.currentAward

  return (
    <div className="card">
      <h2>الترسية</h2>

      {!canAct && (
        <div className="notice info">
          العرض فقط — تأكيد الترسية من صلاحية لجنة الترسية.
        </div>
      )}

      {auction.status === 'PendingEligibilityReview' && (
        <p className="muted">
          أُغلق المزاد. بانتظار أن يرشّح النظام صاحب أعلى عطاء يستوفي السعر الاحتياطي.
        </p>
      )}

      {auction.status === 'Unsold' && (
        <div className="notice error">
          لم يبلغ أي عطاء السعر الاحتياطي — المزاد غير مُرسى.
        </div>
      )}

      {auction.pendingCandidateBidderId && (
        <>
          <h3>المرشّح</h3>
          <div className="grid">
            <div>
              <div className="muted small">قيمة العطاء</div>
              <div className="big-number num">
                {sar(auction.pendingCandidateAmountMinorUnits, 'ar')}
              </div>
            </div>
            <div>
              <div className="muted small">المزايد</div>
              {/* The committee is the one party that must see the real identity:
                  they sign the award letter to a named person. The masking in the
                  public view (D-22) is for everyone else. */}
              <code className="small">{auction.pendingCandidateBidderId}</code>
            </div>
          </div>

          {canAct && (
            <div className="row end">
              <button
                className="primary"
                disabled={busy}
                onClick={() =>
                  onAct(() =>
                    client.post(`/auctions/${auction.id}/award`, {
                      committeeUserId,
                    }),
                  )
                }
              >
                تأكيد الترسية
              </button>
            </div>
          )}
        </>
      )}

      {award && (
        <>
          <h3>
            الترسية المعتمدة
            {award.cascadeStep > 0 && (
              <span className="muted"> — الترتيب {award.cascadeStep + 1} بعد سحب الفوز</span>
            )}
          </h3>

          <div className="grid">
            <div>
              <div className="muted small">القيمة</div>
              <div className="num">{sar(award.amountMinorUnits, 'ar')}</div>
            </div>
            <div>
              <div className="muted small">مهلة الالتزام</div>
              <div className="num">
                {day(award.complianceDeadline)}
              </div>
            </div>
            <div>
              <div className="muted small">المزايد</div>
              <code className="small">{award.bidderId}</code>
            </div>
          </div>

          <h3>خطوات خطاب الترسية</h3>
          <ol className="steps">
            <Step done={award.letterDocumentId !== null} text="إصدار خطاب الترسية" />
            <Step done={award.signedLetterDocumentId !== null} text="توقيع الخطاب" />
            <Step done={award.winnerNotifiedAt !== null} text="إشعار المزايد الفائز" />
          </ol>

          {canAct && (
            <div className="row">
              {!award.letterDocumentId && (
                <button
                  disabled={busy}
                  onClick={() =>
                    onAct(() =>
                      client.post(`/auctions/${auction.id}/award/letter`, {
                        documentId: crypto.randomUUID(),
                      }),
                    )
                  }
                >
                  إصدار الخطاب
                </button>
              )}

              {award.letterDocumentId && !award.signedLetterDocumentId && (
                <button
                  disabled={busy}
                  onClick={() =>
                    onAct(() =>
                      client.post(`/auctions/${auction.id}/award/signed-letter`, {
                        documentId: crypto.randomUUID(),
                      }),
                    )
                  }
                >
                  رفع الخطاب الموقّع
                </button>
              )}

              {award.signedLetterDocumentId && !award.winnerNotifiedAt && (
                <button
                  className="primary"
                  disabled={busy}
                  onClick={() => onAct(() => client.post(`/auctions/${auction.id}/award/notify`))}
                >
                  إشعار الفائز
                </button>
              )}

              {award.winnerNotifiedAt && auction.status === 'Awarded' && (
                <button
                  className="primary"
                  disabled={busy}
                  onClick={() => onAct(() => client.post(`/auctions/${auction.id}/settle`))}
                >
                  تسجيل السداد
                </button>
              )}
            </div>
          )}

          {canAct && auction.status === 'Awarded' && (
            <>
              <h3>سحب الفوز</h3>
              <p className="muted small" style={{ marginTop: -4 }}>
                إذا لم يستوفِ الفائز الشروط، يُرشَّح من يليه بشرط أن يبلغ عطاؤه السعر
                الاحتياطي.
              </p>
              <div className="row">
                <input
                  placeholder="سبب سحب الفوز"
                  style={{ width: 260 }}
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                />
                <label className="row" style={{ gap: 6, margin: 0 }}>
                  <input
                    type="checkbox"
                    style={{ width: 'auto' }}
                    checked={forfeit}
                    onChange={(e) => setForfeit(e.target.checked)}
                  />
                  <span style={{ margin: 0 }}>مصادرة التأمين</span>
                </label>
                <button
                  className="danger"
                  disabled={busy || reason.trim() === ''}
                  onClick={() =>
                    onAct(async () => {
                      await client.post(`/auctions/${auction.id}/award/disqualify`, {
                        reason: reason.trim(),
                        forfeitDeposit: forfeit,
                      })
                      setReason('')
                    })
                  }
                >
                  سحب الفوز
                </button>
              </div>
            </>
          )}
        </>
      )}
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
