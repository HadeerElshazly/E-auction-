/**
 * Dates, as the proposal shows them.
 *
 * `toLocaleString('ar-SA')` does not do what it looks like it does: the default
 * calendar for that locale is Umm al-Qura, so a date renders as ١٤٤٨/٤/٢٥ rather
 * than 26/08/2026, in Arabic-Indic digits. Both are correct Arabic and only one is
 * the one on the screens this platform was specified from — and an auction that
 * closes "٢٥ شوال" beside a contract dated in Gregorian is a support call.
 *
 * So the calendar is pinned to Gregorian and the numbering to Latin, while the
 * month and meridiem words stay Arabic. One place, because a locale string copied
 * into eight components drifts in seven of them.
 */
const LOCALE = 'ar-SA-u-ca-gregory-nu-latn'

const dateTime = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
})

const dateOnly = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
})

const timeOnly = new Intl.DateTimeFormat(LOCALE, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

/** A date and a time: 26/08/2026, 12:56 م */
export function when(value: string | number | Date | null | undefined): string {
  if (value === null || value === undefined || value === '') return '—'
  const at = new Date(value)
  return Number.isNaN(at.getTime()) ? '—' : dateTime.format(at)
}

/** A date alone: 26/08/2026 */
export function day(value: string | number | Date | null | undefined): string {
  if (value === null || value === undefined || value === '') return '—'
  const at = new Date(value)
  return Number.isNaN(at.getTime()) ? '—' : dateOnly.format(at)
}

/** A clock time alone, for the hall and the bid ladder: 12:56:03 م */
export function clock(value: string | number | Date | null | undefined): string {
  if (value === null || value === undefined || value === '') return '—'
  const at = new Date(value)
  return Number.isNaN(at.getTime()) ? '—' : timeOnly.format(at)
}
