/**
 * What each stage of an auction is called, for both portals.
 *
 * One table, because two drifted: the administrators' screen said «مجدول» where the
 * citizen's said «قادم», and «مُسدَّد» where nothing was said at all — the same
 * auction, in the same stage, described differently depending on who looked. The
 * administrators see more stages (drafts, review) but never a different word for a
 * stage both see.
 */
export type Tone = 'live' | 'wait' | 'done' | 'bad'

export const stageLabels: Record<string, { ar: string; tone: Tone }> = {
  // Before publication — administrators only.
  Draft: { ar: 'مسودة', tone: 'done' },
  PendingReview: { ar: 'بانتظار الاعتماد', tone: 'wait' },
  Rejected: { ar: 'مرفوض', tone: 'bad' },
  Approved: { ar: 'معتمد', tone: 'wait' },

  // Both portals.
  Scheduled: { ar: 'قادم', tone: 'wait' },
  Live: { ar: 'جارٍ الآن', tone: 'live' },
  // Three names in the services for one moment: bidding has ended and the
  // candidate is being worked out. One word for all three.
  Closing: { ar: 'أُغلق', tone: 'done' },
  Closed: { ar: 'أُغلق', tone: 'done' },
  PendingEligibilityReview: { ar: 'أُغلق', tone: 'done' },
  PendingAward: { ar: 'بانتظار الترسية', tone: 'wait' },
  Awarded: { ar: 'تمت الترسية', tone: 'live' },
  WinnerDisqualified: { ar: 'سُحب الفوز', tone: 'bad' },
  Unsold: { ar: 'لم يُبع', tone: 'bad' },
  Settled: { ar: 'تم البيع', tone: 'done' },
  Cancelled: { ar: 'أُلغي', tone: 'bad' },
}

export function stageLabel(status: string): { ar: string; tone: Tone } {
  return stageLabels[status] ?? { ar: status, tone: 'done' }
}

/** Stages in which no bid can be placed any more. */
export const finishedStages = [
  'Closing', 'Closed', 'PendingEligibilityReview', 'PendingAward', 'Awarded',
  'WinnerDisqualified', 'Unsold', 'Settled', 'Cancelled',
]
