/**
 * Money is carried as an integer count of halalas — 1 SAR = 100 halalas — in every
 * API field whose name ends in MinorUnits. Nothing here ever holds a riyal amount
 * in a float: 0.1 + 0.2 is not 0.3 in binary floating point, and an auction that
 * sells land for 1,200,000.00 SAR must not be able to produce 1,199,999.99.
 */

export const HALALAS_PER_RIYAL = 100

const formatter = new Intl.NumberFormat('en-US', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

const arabicFormatter = new Intl.NumberFormat('ar-SA', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** 120000000 -> "1,200,000.00" */
export function riyals(minorUnits: number | null | undefined, locale: 'ar' | 'en' = 'en'): string {
  if (minorUnits === null || minorUnits === undefined) return '—'
  const whole = Math.trunc(minorUnits / HALALAS_PER_RIYAL)
  const fraction = Math.abs(minorUnits % HALALAS_PER_RIYAL)
  const value = whole + (minorUnits < 0 ? -fraction : fraction) / HALALAS_PER_RIYAL
  return (locale === 'ar' ? arabicFormatter : formatter).format(value)
}

/** 120000000 -> "1,200,000.00 SAR" */
export function sar(minorUnits: number | null | undefined, locale: 'ar' | 'en' = 'en'): string {
  if (minorUnits === null || minorUnits === undefined) return '—'
  return locale === 'ar' ? `${riyals(minorUnits, 'ar')} ر.س` : `${riyals(minorUnits)} SAR`
}

/**
 * Parses what someone typed into halalas, or null if it is not a number.
 *
 * Deliberately not `Math.round(parseFloat(x) * 100)`: for "1234567.89" that route
 * goes through a float big enough to land on 123456788 instead of 123456789, and a
 * bid one halala under the increment is rejected. Splitting on the decimal point
 * keeps both halves in integer range.
 */
export function parseRiyals(text: string): number | null {
  const cleaned = text.replace(/[\s,٬]/g, '').replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
  if (!/^-?\d+(\.\d{0,2})?$/.test(cleaned)) return null

  const negative = cleaned.startsWith('-')
  const [whole = '0', fraction = ''] = cleaned.replace('-', '').split('.')
  const halalas = Number(whole) * HALALAS_PER_RIYAL + Number(fraction.padEnd(2, '0'))
  if (!Number.isSafeInteger(halalas)) return null

  return negative ? -halalas : halalas
}

/** "3 days 4 hours" / "00:42" as the end approaches. */
export function untilText(target: string | Date, now: Date = new Date()): string {
  const ms = new Date(target).getTime() - now.getTime()
  if (ms <= 0) return 'ended'

  const seconds = Math.floor(ms / 1000)
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = seconds % 60

  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`
  return `${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
}
