import { useEffect, useState } from 'react'
import { sar, type Api } from '@eauction/shared'
import type { BidCertificate } from './types'

/**
 * شهادة مزايدة — the printable proof that one bid was accepted and recorded.
 *
 * Every figure on it is fetched from the service rather than taken from what this
 * page already has in memory. That is the whole point: the browser holds what it
 * submitted, and a document built from that would prove only that the browser
 * remembered something. The service reads the bid back out of the append-only log
 * and reports what is actually in the record.
 *
 * Printing is the delivery mechanism, because there is no document service yet. The
 * page prints to PDF through the browser, and the print stylesheet hides everything
 * around it.
 */
interface Props {
  client: Api
  auctionId: string
  auctionNameAr: string
  offset: number
  onClose: () => void
}

export function Certificate({ client, auctionId, auctionNameAr, offset, onClose }: Props) {
  const [certificate, setCertificate] = useState<BidCertificate | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    client
      .get<BidCertificate>(`/auctions/${auctionId}/bids/${offset}/certificate`)
      .then((c) => {
        if (!cancelled) setCertificate(c)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e))
      })

    return () => {
      cancelled = true
    }
  }, [client, auctionId, offset])

  return (
    <div className="certificate-backdrop no-print-backdrop">
      <div className="card certificate">
        <div className="row no-print" style={{ justifyContent: 'space-between' }}>
          <button onClick={onClose}>إغلاق</button>
          <button className="primary" disabled={!certificate} onClick={() => window.print()}>
            طباعة
          </button>
        </div>

        {error && <div className="notice error">{error}</div>}
        {!certificate && !error && <p className="muted">جارٍ إصدار الشهادة…</p>}

        {certificate && (
          <>
            <h2>شهادة مزايدة</h2>
            <p className="muted small">
              تُثبت هذه الشهادة أن المزايدة أدناه سُجّلت في سجل المزايدات غير القابل
              للتعديل. البيانات مقروءة من السجل نفسه وقت الإصدار.
            </p>

            <div className="grid">
              <Row k="الرقم المرجعي" v={certificate.reference} ltr />
              <Row k="المزاد" v={auctionNameAr} />
              <Row k="المبلغ" v={sar(certificate.amountMinorUnits, 'ar')} />
              <Row
                k="وقت القبول"
                v={new Date(certificate.serverTimestampMs).toLocaleString('ar-SA')}
              />
              <Row k="الترتيب في السجل" v={String(certificate.offset)} ltr />
              <Row k="القناة" v={certificate.channel === 'Onsite' ? 'حضوري' : 'إلكتروني'} />
              {certificate.enteredByUserId && (
                // An onsite bid claims something weaker than an online one, and the
                // certificate says so rather than quietly reading the same.
                <Row k="أُدخلت بواسطة" v={certificate.enteredByUserId} ltr />
              )}
              <Row k="رقم المزايدة لديك" v={certificate.clientBidId} ltr />
              <Row k="تاريخ الإصدار" v={new Date(certificate.issuedAt).toLocaleString('ar-SA')} />
            </div>

            <h3>بصمة التحقق</h3>
            <p className="ltr num small" style={{ wordBreak: 'break-all' }}>
              {certificate.signature}
            </p>
            <p className="muted small">
              هذه البصمة تطابق الإيصال الذي استلمته لحظة المزايدة. يمكن للجهة المختصة
              التحقق منها بالرقم المرجعي أعلاه.
            </p>
          </>
        )}
      </div>
    </div>
  )
}

function Row({ k, v, ltr = false }: { k: string; v: string; ltr?: boolean }) {
  return (
    <div>
      <div className="muted small">{k}</div>
      <div className={ltr ? 'ltr num' : ''}>{v}</div>
    </div>
  )
}
