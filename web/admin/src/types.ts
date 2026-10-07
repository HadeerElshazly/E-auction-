import { stageLabels } from '@eauction/shared'
/** Mirrors the records in src/EAuction.AuctionAdmin/Program.cs. */

export interface AuctionListItem {
  id: string
  status: AuctionStatus
  nameAr: string
  nameEn: string
  channel: string
  bidderVisibility: string
  clerkUserId: string | null
  startsAt: string | null
  endsAt: string | null
  openingPriceMinorUnits: number
  depositMinorUnits: number
  plotCount: number
  createdAt: string
  bookletPriceMinorUnits: number
  /** A Public document — the same cover the citizen's catalogue shows. */
  coverImageDocumentId: string | null
  totalAreaSqm: number
}

export interface Award {
  id: string
  bidderId: string
  amountMinorUnits: number
  cascadeStep: number
  confirmedAt: string
  complianceDeadline: string
  letterDocumentId: string | null
  signedLetterDocumentId: string | null
  winnerNotifiedAt: string | null
  /** Receipted so far, and what is still owed — recorded on متابعة الترسية. */
  paidMinorUnits: number
  remainingMinorUnits: number
  overdue: boolean
}

export interface Auction {
  id: string
  status: AuctionStatus
  nameAr: string
  nameEn: string
  channel: string
  bidderVisibility: string
  clerkUserId: string | null
  phase: string | null
  startsAt: string | null
  endsAt: string | null
  openingPriceMinorUnits: number
  minIncrementMinorUnits: number
  depositMinorUnits: number
  brokerageFeePercent: number
  bookletPriceMinorUnits: number
  quietPeriodSeconds: number | null
  maxExtensions: number
  bookletDocumentId: string | null
  coverImageDocumentId: string | null
  /** Public documents for the catalogue — plans, photographs. Never the booklet. */
  attachments: { documentId: string; titleAr: string }[]
  plotCount: number
  totalAreaSqm: number
  rejectionReason: string | null
  cancellationReason: string | null
  cancelledAt: string | null
  /** Why the committee refused the preliminary result, when it did. */
  resultRejectionReason: string | null
  pendingCandidateBidderId: string | null
  pendingCandidateAmountMinorUnits: number | null
  currentAward: Award | null
}

export type AuctionStatus =
  | 'Draft'
  | 'PendingReview'
  | 'Rejected'
  | 'Approved'
  | 'Scheduled'
  | 'Live'
  | 'PendingEligibilityReview'
  | 'PendingAward'
  | 'Awarded'
  | 'WinnerDisqualified'
  | 'Unsold'
  | 'Settled'
  | 'Cancelled'

/** The Arabic label and the visual weight each status gets. */
// One vocabulary for both portals: see shared/src/stages.ts.
export const statusLabels = stageLabels

export function label(status: string): { ar: string; tone: 'live' | 'wait' | 'done' | 'bad' } {
  return statusLabels[status] ?? { ar: status, tone: 'done' }
}

/**
 * One eligible bidder as the clerk's terminal lists them (§29). A paddle number
 * because that is what the room holds up; a name because the clerk confirms it out
 * loud before entering a bid on somebody's behalf.
 */
export interface RosterEntry {
  bidderId: string
  nameAr: string
  paddleNumber: number
}
