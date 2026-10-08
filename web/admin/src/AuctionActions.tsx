import { useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Icon, parseRiyals, riyals, sar, type Api } from '@eauction/shared'
import type { Auction } from './types'

type Ending = 'close' | 'cancel'

// The buttons live in the auction's side box, which is its own stacking context;
// the dialogs are drawn on the page itself so the top bar cannot sit over them.
const Dialog = ({ children }: { children: ReactNode }) => createPortal(children, document.body)

/**
 * «إنهاء المزاد» — an administrator closing a running auction before its time, for
 * one of two reasons, each on record:
 *
 *  - إغلاق مع ترسية لأعلى سعر: it closes now and the highest valid bid goes to the
 *    committee as the candidate, exactly as after a normal close; the losers'
 *    deposits come back as usual.
 *  - إغلاق للإلغاء: the sale itself is withdrawn, no award — and the administrator
 *    decides whether the bidders' paid deposits and booklet fees are refunded
 *    through the gateway. Bank guarantees are released by hand from
 *    «التسويات والإفراغ», as always.
 */
export function EndAuction({
  auction,
  client,
  busy,
  onAct,
}: {
  auction: Auction
  client: Api
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [ending, setEnding] = useState<Ending>('close')
  const [reason, setReason] = useState('')
  const [refund, setRefund] = useState(true)

  const submit = () => {
    const confirmText =
      ending === 'close'
        ? 'سيُغلق المزاد الآن وتُرفع أعلى مزايدة للجنة الترسية. متابعة؟'
        : refund
          ? 'سيُلغى المزاد نهائياً دون ترسية، ويُرد التأمين ورسم الكراسة لجميع المزايدين. لا يمكن التراجع. متابعة؟'
          : 'سيُلغى المزاد نهائياً دون ترسية ودون رد التأمين ورسم الكراسة. لا يمكن التراجع. متابعة؟'
    if (!window.confirm(confirmText)) return
    const work =
      ending === 'close'
        ? () => client.post(`/auctions/${auction.id}/close-early`, { reason: reason.trim() })
        : () => client.post(`/auctions/${auction.id}/cancel`, { reason: reason.trim(), refund })
    void onAct(work).then(() => {
      setOpen(false)
      setReason('')
    })
  }

  return (
    <>
      <button className="danger" style={{ width: '100%' }} disabled={busy} onClick={() => setOpen(true)}>
        إنهاء المزاد
      </button>

      {open && (
        <Dialog>
        <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label="إنهاء المزاد">
          <div className="modal">
            <div className="modal-head">
              <button className="icon-btn" aria-label="إغلاق النافذة" onClick={() => setOpen(false)}>
                ✕
              </button>
              <div className="grow" style={{ textAlign: 'start' }}>
                <h2>إنهاء المزاد قبل موعده</h2>
                <p>{auction.nameAr}</p>
              </div>
            </div>

            <div className="end-options" role="radiogroup" aria-label="طريقة الإنهاء">
              <label className={`end-option${ending === 'close' ? ' active' : ''}`}>
                <input type="radio" name="ending" checked={ending === 'close'} onChange={() => setEnding('close')} />
                <span>
                  <b>إغلاق مع ترسية لأعلى سعر</b>
                  <small>
                    يتوقف المزاد الآن، وتُرفع أعلى مزايدة صحيحة إلى لجنة الترسية كفائز مبدئي. يُرد تأمين بقية
                    المزايدين كالمعتاد.
                  </small>
                </span>
              </label>
              <label className={`end-option${ending === 'cancel' ? ' active' : ''}`}>
                <input type="radio" name="ending" checked={ending === 'cancel'} onChange={() => setEnding('cancel')} />
                <span>
                  <b>إغلاق للإلغاء</b>
                  <small>يُلغى البيع دون ترسية، وتحدّد أدناه رد المبالغ المدفوعة للمزايدين من عدمه.</small>
                </span>
              </label>
            </div>

            {ending === 'cancel' && (
              <div className="refund-choice">
                <label className="check-row">
                  <input type="checkbox" checked={refund} onChange={(e) => setRefund(e.target.checked)} />
                  <span>
                    <b>رد مبالغ التأمين والكراسة المدفوعة للمزايدين</b>
                    <small>
                      {refund
                        ? 'يُطلب الرد من بوابة الدفع لكل مبلغ سُدّد إلكترونياً.'
                        : 'تحتفظ الأمانة بمبالغ التأمين ورسوم الكراسة، ويُبلَّغ المزايدون بذلك.'}
                    </small>
                  </span>
                </label>
                <p className="muted small" style={{ margin: '8px 0 0' }}>
                  الضمانات البنكية لا تمر ببوابة الدفع: تُحرَّر أو تُسيَّل إدارياً من شاشة «التسويات والإفراغ».
                </p>
              </div>
            )}

            <label style={{ display: 'block', marginTop: 16 }}>
              <span>السبب {ending === 'cancel' && <span className="muted">(يظهر للمشتركين)</span>}</span>
              <textarea
                rows={3}
                aria-label="سبب الإنهاء"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                style={{ width: '100%' }}
              />
            </label>

            <div className="row" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
              <button onClick={() => setOpen(false)}>تراجع</button>
              <button className="danger" disabled={busy || reason.trim() === ''} onClick={submit}>
                {ending === 'close' ? 'إغلاق مع ترسية' : 'إغلاق للإلغاء'}
              </button>
            </div>
          </div>
        </div>
        </Dialog>
      )}
    </>
  )
}

/**
 * «إعادة الطرح بسعر مخفّض» — an auction that ended unsold, offered again as a new
 * draft at a lower opening price. The floor is the reserve; the server holds it and
 * refuses anything below, since the reserve never reaches this screen.
 */
export function Reoffer({
  auction,
  client,
  busy,
  onAct,
  onOpen,
}: {
  auction: Auction
  client: Api
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
  onOpen: (id: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [price, setPrice] = useState('')
  const minor = price.trim() === '' ? null : parseRiyals(price)
  const valid = minor != null && minor > 0 && minor < auction.openingPriceMinorUnits

  return (
    <>
      <button className="primary" style={{ width: '100%' }} disabled={busy} onClick={() => setOpen(true)}>
        إعادة الطرح بسعر مخفّض
      </button>

      {open && (
        <Dialog>
        <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label="إعادة الطرح">
          <div className="modal">
            <div className="modal-head">
              <button className="icon-btn" aria-label="إغلاق النافذة" onClick={() => setOpen(false)}>
                ✕
              </button>
              <div className="grow" style={{ textAlign: 'start' }}>
                <h2>إعادة الطرح بسعر مخفّض</h2>
                <p>{auction.nameAr}</p>
              </div>
            </div>
            <p className="muted small" style={{ margin: '0 0 16px' }}>
              يُنشأ مزاد جديد كمسودة بنفس القطع والشروط وسعر افتتاح أقل، ثم يُجدول ويُرفع للاعتماد كأي مزاد. لا يمكن
              النزول عن السعر الاحتياطي. يبقى هذا المزاد وسجله كما انتهى.
            </p>
            <div className="kv">
              <span>سعر البداية السابق</span>
              <b className="num">{sar(auction.openingPriceMinorUnits, 'ar')}</b>
            </div>
            <label style={{ display: 'block', marginTop: 12 }}>
              <span>
                سعر البداية الجديد <span className="muted">(ر.س)</span>
              </span>
              <input
                className="ltr num"
                inputMode="decimal"
                aria-label="سعر البداية الجديد"
                value={price}
                placeholder={riyals(auction.openingPriceMinorUnits, 'en')}
                onChange={(e) => setPrice(e.target.value)}
              />
            </label>
            {minor != null && !valid && (
              <p className="notice error small">يجب أن يكون السعر الجديد أقل من سعر البداية السابق.</p>
            )}
            <div className="row" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
              <button onClick={() => setOpen(false)}>تراجع</button>
              <button
                className="primary"
                disabled={busy || !valid}
                onClick={() =>
                  void onAct(async () => {
                    const next = await client.post<Auction>(`/auctions/${auction.id}/reoffer`, {
                      openingPriceMinorUnits: minor,
                    })
                    setOpen(false)
                    onOpen(next.id)
                  })
                }
              >
                إنشاء المسودة
              </button>
            </div>
          </div>
        </div>
        </Dialog>
      )}
    </>
  )
}

/**
 * «قرار الاعتماد» — the committee's decision on an auction submitted for approval,
 * in the side box where it cannot be missed: approve, or refuse with a reason the
 * preparer reads. The auction's data is on the tabs beside it.
 */
export function ReviewDecision({
  auction,
  client,
  busy,
  onAct,
  amendment = false,
}: {
  auction: Auction
  client: Api
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
  /**
   * The decision is on a change to a published auction (§6.5), not on a new one:
   * approving publishes the new terms and tells the bidders; refusing sends it
   * back to the administrator with the reason, and what was published stands.
   */
  amendment?: boolean
}) {
  const [refusing, setRefusing] = useState(false)
  const [reason, setReason] = useState('')

  return (
    <div className="review-decision" data-testid="review-decision">
      {amendment && (
        <p className="box-help" style={{ margin: 0 }}>
          تعديل على مزاد منشور: باعتماده تُنشر البيانات الجديدة ويُبلَّغ المتقدمون؛ وبرفضه يعود لمدير المزادات بالسبب
          ويبقى ما نُشر كما هو.
        </p>
      )}
      <button
        className="primary wide"
        disabled={busy}
        onClick={() => void onAct(() => client.post(`/auctions/${auction.id}/approve`))}
      >
        {amendment ? 'اعتماد التعديل' : 'اعتماد المزاد'}
      </button>
      <button className="danger wide" disabled={busy} onClick={() => setRefusing(true)}>
        رفض
      </button>

      {refusing && (
        <Dialog>
          <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={amendment ? 'رفض التعديل' : 'رفض المزاد'}>
            <div className="modal">
              <div className="modal-head">
                <button className="icon-btn" aria-label="إغلاق النافذة" onClick={() => setRefusing(false)}>
                  ✕
                </button>
                <div className="grow" style={{ textAlign: 'start' }}>
                  <h2>{amendment ? 'رفض التعديل' : 'رفض المزاد'}</h2>
                  <p>{auction.nameAr}</p>
                </div>
              </div>
              <label style={{ display: 'block' }}>
                <span>السبب — يظهر لمُعدّ المزاد ليصحّح البيانات</span>
                <textarea
                  rows={3}
                  aria-label="سبب الرفض"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  style={{ width: '100%' }}
                />
              </label>
              <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16, gap: 8 }}>
                <button onClick={() => setRefusing(false)}>تراجع</button>
                <button
                  className="danger"
                  disabled={busy || reason.trim() === ''}
                  onClick={() =>
                    void onAct(() => client.post(`/auctions/${auction.id}/reject`, { reason: reason.trim() })).then(() => {
                      setRefusing(false)
                      setReason('')
                    })
                  }
                >
                  {amendment ? 'رفض التعديل' : 'رفض المزاد'}
                </button>
              </div>
            </div>
          </div>
        </Dialog>
      )}
    </div>
  )
}

/**
 * «إرسال للاعتماد» — the preparer's last step, in the side box. The service checks
 * the auction is complete and says what is missing if it is not.
 */
export function SubmitForApproval({
  auction,
  client,
  busy,
  onAct,
  amendment = false,
}: {
  auction: Auction
  client: Api
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
  /** A change to a published auction goes to the committee the way the auction first did (§6.5). */
  amendment?: boolean
}) {
  return (
    <div className="review-decision">
      <button
        className="primary wide"
        disabled={busy}
        onClick={() => void onAct(() => client.post(`/auctions/${auction.id}/submit`))}
      >
        {amendment ? 'إرسال التعديل للاعتماد' : 'إرسال للاعتماد'}
      </button>
      <p className="box-help" style={{ margin: 0 }}>
        {amendment
          ? 'احفظ التعديلات أولاً. يُرسل التعديل إلى لجنة الترسية؛ يبقى ما نُشر للمزايدين حتى اعتماده.'
          : 'احفظ التعديلات أولاً. يُرسل المزاد إلى لجنة الترسية لاعتماده.'}
      </p>
    </div>
  )
}

/**
 * «حذف المسودة» — a draft that will not be held. Nothing was published and nobody
 * paid, so the row goes; سجل المراجعة keeps the deletion. A published auction is
 * withdrawn with «حذف المزاد» below instead, because it has bidders.
 */
export function DeleteDraft({
  auction,
  busy,
  onDelete,
}: {
  auction: Auction
  busy: boolean
  onDelete: (id: string) => Promise<void>
}) {
  return (
    <button
      className="danger wide"
      disabled={busy}
      data-testid="delete-draft"
      onClick={() => {
        if (!window.confirm(`حذف المسودة «${auction.nameAr}» نهائياً؟ لا يمكن التراجع.`)) return
        void onDelete(auction.id)
      }}
    >
      <Icon name="close" size={16} /> حذف المسودة
    </button>
  )
}

/**
 * «حذف المزاد» for an upcoming auction — published, with bidders who have paid or
 * may be about to. It leaves as a cancellation with the reason on record: the sale
 * is withdrawn from القادمة, the applicants are told, and the auction manager decides
 * here whether their deposits and booklet fees go back through the gateway. The
 * record stays, marked «أُلغي»: a published auction is not erased from the trail.
 */
export function WithdrawUpcoming({
  auction,
  client,
  busy,
  onAct,
}: {
  auction: Auction
  client: Api
  busy: boolean
  onAct: (work: () => Promise<unknown>) => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const [refund, setRefund] = useState(true)

  const submit = () => {
    const confirmText = refund
      ? 'سيُحذف المزاد من القادمة ويُلغى نهائياً، ويُبلَّغ المتقدمون، وتُرد مبالغ التأمين ورسوم الكراسة المدفوعة. لا يمكن التراجع. متابعة؟'
      : 'سيُحذف المزاد من القادمة ويُلغى نهائياً دون رد التأمين ورسوم الكراسة. لا يمكن التراجع. متابعة؟'
    if (!window.confirm(confirmText)) return
    void onAct(() => client.post(`/auctions/${auction.id}/cancel`, { reason: reason.trim(), refund })).then(() => {
      setOpen(false)
      setReason('')
    })
  }

  return (
    <>
      <button className="danger wide" disabled={busy} data-testid="withdraw-upcoming" onClick={() => setOpen(true)}>
        <Icon name="close" size={16} /> حذف المزاد
      </button>

      {open && (
        <Dialog>
          <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label="حذف المزاد">
            <div className="modal">
              <div className="modal-head">
                <button className="icon-btn" aria-label="إغلاق النافذة" onClick={() => setOpen(false)}>
                  ✕
                </button>
                <div className="grow" style={{ textAlign: 'start' }}>
                  <h2>حذف مزاد قادم</h2>
                  <p>{auction.nameAr}</p>
                </div>
              </div>

              <p className="muted small" style={{ margin: '0 0 12px' }}>
                يُسحب المزاد من المزادات القادمة ويُبلَّغ المتقدمون بذلك. يبقى سجله محفوظاً بحالة «أُلغي» ولا يُمحى من
                سجل المراجعة.
              </p>

              <div className="refund-choice">
                <label className="check-row">
                  <input type="checkbox" checked={refund} onChange={(e) => setRefund(e.target.checked)} />
                  <span>
                    <b>رد مبالغ التأمين والكراسة المدفوعة للمتقدمين</b>
                    <small>
                      {refund
                        ? 'يُطلب الرد من بوابة الدفع لكل مبلغ سُدّد إلكترونياً، بقرار مدير المزادات هنا. لا شيء يُرد إن لم يُسدَّد شيء.'
                        : 'تحتفظ الأمانة بمبالغ التأمين ورسوم الكراسة، ويُبلَّغ المتقدمون بذلك.'}
                    </small>
                  </span>
                </label>
                <p className="muted small" style={{ margin: '8px 0 0' }}>
                  الضمانات البنكية لا تمر ببوابة الدفع: تُحرَّر إدارياً من شاشة «التسويات والإفراغ».
                </p>
              </div>

              <label style={{ display: 'block', marginTop: 16 }}>
                <span>
                  السبب <span className="muted">(يظهر للمتقدمين)</span>
                </span>
                <textarea
                  rows={3}
                  aria-label="سبب حذف المزاد"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  style={{ width: '100%' }}
                />
              </label>

              <div className="row" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
                <button onClick={() => setOpen(false)}>تراجع</button>
                <button className="danger" disabled={busy || reason.trim() === ''} onClick={submit}>
                  حذف المزاد
                </button>
              </div>
            </div>
          </div>
        </Dialog>
      )}
    </>
  )
}
