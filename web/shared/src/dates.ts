/**
 * Dates, in the Hijri calendar (Umm al-Qura), as the platform's users read them.
 *
 * Previously pinned to Gregorian to match the proposal's screens; the client asked
 * for Hijri throughout, which is also the calendar Saudi land and municipal
 * documents are dated in. Umm al-Qura specifically — the official Saudi calendar —
 * not the tabular "islamic" one, which can differ by a day.
 *
 * Digits stay Latin, as everywhere else on the page: a date in Arabic-Indic digits
 * beside prices and countdowns in Latin ones is how a figure gets misread. Month
 * names are written out — «28 ربيع الآخر 1448 هـ» — because a numeric Hijri date
 * (28/04/1448) is easy to mistake for a Gregorian one at a glance.
 *
 * Pinned to Riyadh time: an auction opens at 10:00 in Riyadh whatever the clock of
 * the machine showing it says. One place, because a locale string copied into
 * eight components drifts in seven of them.
 */
const LOCALE = 'ar-SA-u-ca-islamic-umalqura-nu-latn'
const TIME_ZONE = 'Asia/Riyadh'

const dateTime = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'long',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  timeZone: TIME_ZONE,
})

const dateOnly = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'long',
  day: 'numeric',
  timeZone: TIME_ZONE,
})

const timeOnly = new Intl.DateTimeFormat(LOCALE, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  timeZone: TIME_ZONE,
})

const stamp = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'long',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  timeZone: TIME_ZONE,
})

function parse(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null
  const at = new Date(value)
  return Number.isNaN(at.getTime()) ? null : at
}

/** A date and a time: 28 ربيع الآخر 1448 هـ في 10:00 ص */
export function when(value: string | number | Date | null | undefined): string {
  const at = parse(value)
  return at ? dateTime.format(at) : '—'
}

/** A date alone: 28 ربيع الآخر 1448 هـ */
export function day(value: string | number | Date | null | undefined): string {
  const at = parse(value)
  return at ? dateOnly.format(at) : '—'
}

/** A clock time alone, for the hall and the bid ladder: 10:00:03 ص */
export function clock(value: string | number | Date | null | undefined): string {
  const at = parse(value)
  return at ? timeOnly.format(at) : '—'
}

/** Date and time to the second, for records where the second matters (the audit trail). */
export function timestamp(value: string | number | Date | null | undefined): string {
  const at = parse(value)
  return at ? stamp.format(at) : '—'
}
