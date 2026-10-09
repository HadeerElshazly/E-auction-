import { stageLabels } from '@eauction/shared'
/** Mirrors the records in src/EAuction.AuctionAdmin/Program.cs. */

export interface AuctionListItem {
  id: string
  /** رقم المزاد — sequential, assigned when the draft is first saved; what staff quote. */
  number: number
  status: AuctionStatus
  /** A change to a published auction: being made, or waiting on the committee (§6.5). */
  amendment: AmendmentStatus
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
  /** The awarded bidder (or the candidate before the committee). Staff only. */
  winnerBidderId: string | null
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
  /** رقم المزاد — sequential, assigned when the draft is first saved; what staff quote. */
  number: number
  status: AuctionStatus
  /** A change to a published auction: being made, or waiting on the committee (§6.5). */
  amendment: AmendmentStatus
  /** When the open amendment began, and when the auction (or its amendment) was last submitted. */
  amendedAt: string | null
  submittedAt: string | null
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
  /** kind: "Photo" for the gallery, "Document" for a plan or paper; null on older ones. */
  attachments: { documentId: string; titleAr: string; kind: 'Photo' | 'Document' | null }[]
  plotCount: number
  totalAreaSqm: number
  rejectionReason: string | null
  cancellationReason: string | null
  cancellationRefunded: boolean | null
  cancelledAt: string | null
  /** Why the committee refused the preliminary result, when it did. */
  resultRejectionReason: string | null
  pendingCandidateBidderId: string | null
  pendingCandidateAmountMinorUnits: number | null
  currentAward: Award | null
  /** The open award, or the settled one whose title transfer is still tracked. */
  followUpAward: Award | null
  plots: PlotView[]
}

export interface PlotView {
  id: string
  plotNumber: string
  areaSqm: number
  streetWidthMeters: number | null
  frontageMeters: number | null
  landUse: string | null
  /** الواجهة, by name ("NorthEast", …); see shared facingAr. */
  facing: string | null
  latitude: string | null
  longitude: string | null
  descriptionAr: string | null
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

/**
 * Where a change made after publication stands (§6.5). 'None' for every auction
 * whose published terms are the terms — which is every auction never published, too.
 */
export type AmendmentStatus = 'None' | 'Editing' | 'PendingReview'

/** Approved and on the catalogue, not yet open: the window in which an amendment is possible. */
export const isUpcoming = (a: { status: string }) => a.status === 'Approved' || a.status === 'Scheduled'

/** A draft, or a published auction whose amendment is still the administrator's to make. */
export const isDraft = (a: { status: string }) => a.status === 'Draft' || a.status === 'Rejected'

/**
 * Whether an administrator may change this auction's data and files now: a draft
 * freely; an upcoming one as an amendment the committee approves again, and not
 * while that approval is pending. Mirrors Auction.RequireEditable on the server.
 */
export const canEditNow = (a: { status: string; amendment: AmendmentStatus }) =>
  isDraft(a) || (isUpcoming(a) && a.amendment !== 'PendingReview')

/** What the amendment pill says, when there is one to show. */
export const amendmentLabel = (amendment: AmendmentStatus): string | null =>
  amendment === 'PendingReview' ? 'تعديل بانتظار الاعتماد' : amendment === 'Editing' ? 'تعديل لم يُرسل' : null

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
