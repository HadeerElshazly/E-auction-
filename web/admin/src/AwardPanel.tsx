import { useState } from 'react'
import { day, sar, type Api, type Session } from '@eauction/shared'
import type { Auction } from './types'
import { BidderName } from './winners'

interface Props {
  session: Session
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
export function AwardPanel({ session, auction, client, busy, canAct, committeeUserId, onAct }: Props) {
  const [reason, setReason] = useState('')
  const [forfeit, setForfeit] = useState(true)
  const [resultReason, setResultReason] = useState('')

  const before = ['Draft', 'PendingReview', 'Rejected', 'Approved', 'Scheduled', 'Live', 'Cancelled']
  if (before.includes(auction.status)) return null

  // Once settled there is no open award; the settled one is still the result.
  const award = auction.currentAward ?? auction.followUpAward
  const awaitingCommittee = auction.status === 'PendingAward' || auction.status === 'WinnerDisqualified'

  return (
    <div className="card">
      <h2>الترسية</h2>

      {/* Say what has happened, to everyone; the viewer's own permission only
          matters while a decision is still the committee's to take. */}
      {award && (
        <div className="notice ok" data-testid="award-confirmed">
          اعتمدت لجنة الترسية الترسية في {day(award.confirmedAt)}
          {auction.status === 'Settled' ? ' — واعتُمدت التسوية.' : '.'}
        </div>
      )}
      {!canAct && awaitingCommittee && (
        <div className="notice info">
          بانتظار قرار لجنة الترسية — التأكيد من صلاحيتها، وهذه الصفحة للعرض فقط.
        </div>
      )}

      {auction.status === 'PendingEligibilityReview' && (
        <p className="muted">
          أُغلق المزاد. بانتظار أن يرشّح النظام صاحب أعلى عطاء يستوفي السعر الاحتياطي.
        </p>
      )}

      {auction.status === 'Unsold' && (
        <div className="notice error">
          {auction.resultRejectionReason
            ? <>رفضت لجنة الترسية النتيجة المبدئية — السبب: {auction.resultRejectionReason}</>
            : 'لم يبلغ أي عطاء السعر الاحتياطي — المزاد غير مُرسى.'}
        </div>
      )}

      {/* A defaulting winner goes to manual review, not down the ladder by itself
          (الخاصية 08 and the scope decisions): the next bidder is shown as a
          suggestion, and the committee decides. */}
      {auction.status === 'WinnerDisqualified' && (
        <div className="notice info">
          سُحب الفوز من الفائز — بانتظار قرار لجنة الترسية.{' '}
          {auction.pendingCandidateBidderId
            ? 'المزايد التالي المؤهل مبيَّن أدناه للمراجعة؛ لا تُحال الترسية إليه إلا بقرار اللجنة.'
            : 'لا يوجد مزايد تالٍ يستوفي السعر الاحتياطي حتى الآن.'}
        </div>
      )}

      {auction.status === 'WinnerDisqualified' && canAct && (
        <div className="row end" style={{ marginBottom: 12 }}>
          {auction.pendingCandidateBidderId && (
            <button
              className="primary"
              disabled={busy}
              onClick={() => onAct(() => client.post(`/auctions/${auction.id}/next-bidder`))}
            >
              إحالة الترسية للمزايد التالي
            </button>
          )}
          <button
            className="danger"
            disabled={busy}
            onClick={() => {
              if (!window.confirm('إنهاء المزاد دون ترسية؟ يُرد التأمين لغير المستبعدين.')) return
              void onAct(() => client.post(`/auctions/${auction.id}/unsold`))
            }}
          >
            إنهاء دون ترسية
          </button>
        </div>
      )}

      {auction.pendingCandidateBidderId && (
        <>
          <h3>{auction.status === 'WinnerDisqualified' ? 'المزايد التالي (مقترح)' : 'المرشّح'}</h3>
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
              <BidderName session={session} id={auction.pendingCandidateBidderId} />
            </div>
          </div>

          {/* Confirmable only once it is up for award — after a disqualification
              that takes the committee's referral first. */}
          {canAct && auction.status === 'PendingAward' && (
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

          {/* Refusing the preliminary result (الخاصية 08): with a reason, and not
              passed down the ladder — the requirements rule out awarding the next
              bidder automatically. */}
          {canAct && auction.status === 'PendingAward' && (
            <div className="row end" style={{ marginTop: 10 }}>
              <input
                placeholder="سبب رفض النتيجة المبدئية"
                aria-label="سبب رفض النتيجة"
                style={{ flex: '1 1 260px' }}
                value={resultReason}
                onChange={(e) => setResultReason(e.target.value)}
              />
              <button
                className="danger"
                disabled={busy || resultReason.trim() === ''}
                onClick={() => {
                  if (!window.confirm('رفض النتيجة يجعل المزاد غير مُرسى ولا ينتقل للمزايد التالي. متابعة؟')) return
                  void onAct(() =>
                    client.post(`/auctions/${auction.id}/result/reject`, {
                      reason: resultReason.trim(),
                    }),
                  )
                }}
              >
                رفض النتيجة
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
              <div>
                {day(award.complianceDeadline)}
              </div>
            </div>
            <div>
              <div className="muted small">المزايد</div>
              <BidderName session={session} id={award.bidderId} />
            </div>
          </div>

          <h3>خطوات خطاب الترسية</h3>
          <ol className="steps">
            <Step done={award.letterDocumentId !== null} text="إصدار خطاب الترسية" />
            <Step done={award.signedLetterDocumentId !== null} text="توقيع الخطاب" />
            <Step done={award.winnerNotifiedAt !== null} text="إشعار المزايد الفائز" />
            <Step
              done={award.remainingMinorUnits === 0}
              text={
                award.remainingMinorUnits === 0
                  ? 'سداد مبلغ الترسية'
                  : `سداد مبلغ الترسية — المتبقي ${sar(award.remainingMinorUnits, 'ar')}`
              }
            />
            <Step done={auction.status === 'Settled'} text="اعتماد التسوية" />
          </ol>

          {canAct && auction.status === 'Awarded' && (
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
                <>
                  {/* Settling releases every other bidder's deposit, so it waits
                      for the price to be receipted in full on متابعة الترسية. */}
                  <button
                    className="primary"
                    disabled={busy || award.remainingMinorUnits > 0}
                    onClick={() => onAct(() => client.post(`/auctions/${auction.id}/settle`))}
                  >
                    اعتماد التسوية
                  </button>
                  {award.remainingMinorUnits > 0 && (
                    <span className="muted small">
                      المتبقي {sar(award.remainingMinorUnits, 'ar')} — يُسجَّل السداد من صفحة
                      «متابعة الترسية».
                    </span>
                  )}
                </>
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
