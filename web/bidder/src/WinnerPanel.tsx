import { useState } from 'react'
import { api, config, day, sar, type Api, type Session } from '@eauction/shared'
import type { WinnerAward } from './types'

/**
 * «رسا عليك المزاد» — what the winner sees on the auction they won: the award, and
 * the one step that is theirs now. The award runs in order — the letter, paying
 * the price, the transfer at the notary — and each line says whether it is done,
 * waiting on the municipality, or waiting on them.
 *
 * Read from the participant service, which keeps the administration service's
 * snapshot of the award; the bidder portal never reaches the staff API.
 */
export function WinnerPanel({
  award,
  auctionId,
  session,
  participant,
  onError,
}: {
  award: WinnerAward
  auctionId: string
  session: Session
  participant: Api
  onError: (message: string) => void
}) {
  const [busy, setBusy] = useState(false)

  if (award.withdrawnAt) {
    return (
      <div className="notice error" role="status" data-testid="winner-withdrawn">
        <strong>سُحبت ترسية هذا المزاد منك</strong> في {day(award.withdrawnAt)}. للاستفسار تواصل
        مع الأمانة.
      </div>
    )
  }

  const downloadLetter = async () => {
    setBusy(true)
    try {
      const { documentId, grant } = await participant.get<{ documentId: string; grant: string }>(
        `/auctions/${auctionId}/award/letter-grant`,
      )
      await api({ baseUrl: config.documentsApi, session }).download(
        `/documents/${documentId}?grant=${encodeURIComponent(grant)}`,
        `خطاب-الترسية-${auctionId}.pdf`,
      )
    } catch {
      onError('تعذّر تنزيل خطاب الترسية. حاول مرة أخرى بعد قليل.')
    } finally {
      setBusy(false)
    }
  }

  const paidAll = award.remainingMinorUnits === 0
  const overdue = !paidAll && new Date(award.complianceDeadline).getTime() < Date.now()
  const transferDone = award.transferStatus === 'Completed'

  return (
    <div className="card winner-panel" data-testid="winner-panel">
      <div className="winner-head">
        <span className="winner-badge" aria-hidden="true">🏆</span>
        <div>
          <h2 style={{ margin: 0 }}>{award.nextStep === 'Done' ? 'اكتملت الترسية' : 'رسا عليك المزاد'}</h2>
          <p className="muted small" style={{ margin: '4px 0 0' }}>
            اعتمدت لجنة الترسية فوزك في {day(award.confirmedAt)}.
          </p>
        </div>
      </div>

      <div className="stat-grid" style={{ marginTop: 12 }}>
        <div className="stat highlight">
          <div className="stat-label">مبلغ الترسية</div>
          <div className="stat-value num">{sar(award.amountMinorUnits, 'ar')}</div>
        </div>
        <div className="stat">
          <div className="stat-label">المسدَّد</div>
          <div className="stat-value num">{sar(award.paidMinorUnits, 'ar')}</div>
          <div className="stat-sub">يشمل التأمين إن احتُسب من الثمن</div>
        </div>
        <div className="stat">
          <div className="stat-label">المتبقي</div>
          <div className="stat-value num" style={overdue ? { color: 'var(--danger)' } : undefined}>
            {sar(award.remainingMinorUnits, 'ar')}
          </div>
          {!paidAll && (
            <div className="stat-sub" style={overdue ? { color: 'var(--danger)' } : undefined}>
              {overdue ? 'انتهت مهلة السداد في ' : 'آخر موعد للسداد '}
              {day(award.complianceDeadline)}
            </div>
          )}
        </div>
        {award.brokerageMinorUnits > 0 && (
          <div className="stat">
            <div className="stat-label">السعي (الوساطة)</div>
            <div className="stat-value num">{sar(award.brokerageMinorUnits, 'ar')}</div>
            <div className="stat-sub">يُدفع إضافةً إلى مبلغ الترسية</div>
          </div>
        )}
      </div>

      <h3>الخطوات</h3>
      <ol className="award-steps">
        <Step state="done" title="ترسية المزاد" text={`اعتمدتها اللجنة في ${day(award.confirmedAt)}.`} />
        <Step
          state={award.letterAvailable ? 'done' : 'wait'}
          title="خطاب الترسية"
          text={
            award.letterAvailable
              ? 'صدر خطاب الترسية الموقّع. نزّله واحتفظ به — تحتاجه للسداد والإفراغ.'
              : 'تُصدره الأمانة وتوقّعه، وسيصلك إشعار حين يصبح متاحاً هنا.'
          }
        >
          {award.letterAvailable && (
            <button disabled={busy} onClick={() => void downloadLetter()}>
              تنزيل خطاب الترسية
            </button>
          )}
        </Step>
        <Step
          state={paidAll ? 'done' : award.letterAvailable ? 'you' : 'todo'}
          title="سداد مبلغ الترسية"
          text={
            paidAll
              ? 'سُدّد المبلغ كاملاً.'
              : `سدّد المتبقي (${sar(award.remainingMinorUnits, 'ar')}) للأمانة عبر سداد أو التحويل البنكي بالرجوع إلى رقم خطاب الترسية، قبل ${day(award.complianceDeadline)}. تُسجَّل كل دفعة هنا بعد أن تتحقق منها الأمانة.`
          }
        />
        <Step
          state={transferDone ? 'done' : paidAll ? (award.transferStatus === 'InProgress' ? 'wait' : 'you') : 'todo'}
          title="الإفراغ (نقل الملكية)"
          text={
            transferDone
              ? `اكتمل الإفراغ${award.transferCompletedAt ? ` في ${day(award.transferCompletedAt)}` : ''}.`
              : award.transferStatus === 'InProgress'
                ? 'الإفراغ قيد الإجراء لدى كتابة العدل.'
                : 'بعد السداد الكامل تُحدِّد الأمانة موعد الإفراغ لدى كتابة العدل.'
          }
        />
      </ol>
    </div>
  )
}

function Step({
  state,
  title,
  text,
  children,
}: {
  state: 'done' | 'you' | 'wait' | 'todo'
  title: string
  text: string
  children?: React.ReactNode
}) {
  const tag = { done: 'تم', you: 'مطلوب منك', wait: 'بانتظار الأمانة', todo: 'لاحقاً' }[state]
  return (
    <li className={`award-step ${state}`}>
      <span className="award-step-mark" aria-hidden="true">
        {state === 'done' ? '✓' : state === 'you' ? '!' : '•'}
      </span>
      <div className="grow">
        <div className="award-step-title">
          {title} <span className={`pill small ${state === 'done' ? 'live' : state === 'you' ? 'wait' : 'done'}`}>{tag}</span>
        </div>
        <div className="muted small">{text}</div>
        {children && <div style={{ marginTop: 8 }}>{children}</div>}
      </div>
    </li>
  )
}
