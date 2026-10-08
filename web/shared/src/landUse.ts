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
