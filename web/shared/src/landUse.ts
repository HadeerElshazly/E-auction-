/**
 * الاستخدام — the uses an approved plan zones a plot for, by the name the services
 * send, with the Arabic both portals show. One list, so a plot is «سكني تجاري» on
 * every screen alike.
 */
export const LAND_USES = [
  ['Residential', 'سكني'],
  ['Commercial', 'تجاري'],
  ['ResidentialCommercial', 'سكني تجاري'],
  ['Industrial', 'صناعي'],
  ['Agricultural', 'زراعي'],
  ['Other', 'أخرى'],
] as const

export type LandUse = (typeof LAND_USES)[number][0]

export function landUseAr(use: string | null | undefined): string {
  return LAND_USES.find(([key]) => key === use)?.[1] ?? 'غير محدد'
}

/**
 * الواجهة — which way a plot faces, as the plan states it («شمالية شرقية»). Apart
 * from the frontage's length in metres.
 */
export const FACINGS = [
  ['North', 'شمالية'],
  ['South', 'جنوبية'],
  ['East', 'شرقية'],
  ['West', 'غربية'],
  ['NorthEast', 'شمالية شرقية'],
  ['NorthWest', 'شمالية غربية'],
  ['SouthEast', 'جنوبية شرقية'],
  ['SouthWest', 'جنوبية غربية'],
] as const

export function facingAr(facing: string | null | undefined): string {
  return FACINGS.find(([key]) => key === facing)?.[1] ?? '—'
}
