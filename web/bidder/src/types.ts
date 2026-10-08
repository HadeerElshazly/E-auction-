/** From src/EAuction.QueryBff/Program.cs. */

export interface AuctionSummary {
  id: string
  status: string
  nameAr: string
  nameEn: string
  channel: string
  bidderVisibility: string
  // Null when hidden from a visitor by «إعدادات العرض للزوار»; `hidden` names the
  // groups that were, so the page can say signing in shows them.
  startsAt: string | null
  endsAt: string | null
  openingPriceMinorUnits: number | null
  priceMinorUnits: number | null
  minimumNextBidMinorUnits: number | null
  depositMinorUnits: number
  /** 0 is a free booklet, always shown; a price is hidden with the fees. */
  bookletPriceMinorUnits: number | null
  plotCount: number
  totalAreaSqm: number
  /** A Public document in the document service, or null for no image. */
  coverImageDocumentId: string | null
  hidden: string[]
}

export interface Plot {
  id: string
  /** رقم القطعة on the approved plan — not a title deed number. */
  plotNumber: string
  areaSqm: number
  /** Metres. Null while the plot is listed but not yet surveyed. */
  streetWidthMeters: number | null
  frontageMeters: number | null
  /** الاستخدام, by name; see shared landUseAr. */
  landUse: string | null
  latitude: string | null
  longitude: string | null
  descriptionAr: string | null
  descriptionEn: string | null
}

export interface AuctionDetail extends Omit<AuctionSummary, 'plotCount'> {
  effectiveEndsAt: string | null
  minIncrementMinorUnits: number | null
  quietPeriodSeconds: number | null
  maxExtensions: number | null
  extensionsUsed: number | null
  plots: Plot[]
  /** Documents anyone may read — plans, photographs. Never the booklet. */
  attachments: PublicDocument[]
  /** السعي, charged to the winner on the price won. */
  brokerageFeePercent: number | null
  /** Set when an administrator withdrew it before it opened. */
  cancellationReason: string | null
  /** On a cancelled auction: whether deposits and booklet fees are returned. */
  cancellationRefunds: boolean | null
}

export interface PublicDocument {
  documentId: string
  titleAr: string
}

/** A bidder's own bid outcome, pushed to them and to nobody else. */
export interface BidVerdict {
  auctionId: string
  clientBidId: string
  accepted: boolean
  reason: string | null
  currentPriceMinorUnits: number
  minimumNextBidMinorUnits: number
  asOf: string
}

export interface LivePrice {
  auctionId: string
  status: string
  priceMinorUnits: number | null
  minimumNextBidMinorUnits: number
  /** A per-auction pseudonym, never an id or a name (D-22). */
  leaderLabel: string | null
  leaderIsYou: boolean
  /** Set only on the copy sent to the leader, so they know which of their bids won. */
  yourWinningBidId: string | null
  effectiveEndsAt: string
  extensionsUsed: number
  maxExtensions: number
  asOf: string
}

/** From src/EAuction.Participant/Program.cs. */
export interface Bidder {
  id: string
  nameAr: string
  nameEn: string
  phone: string | null
  email: string | null
  verified: boolean
  /** Nafath gives identity, not contact details; the deposit needs both. */
  profileComplete: boolean
}

export interface Subscription {
  id: string
  auctionId: string
  bidderId: string
  status: SubscriptionStatus
  /**
   * When the fee was handed to the payment service — not when it was paid.
   *
   * Status alone no longer says what is happening now that payment is
   * asynchronous: `Draft` means both "buy the booklet" and "your bank is being
   * asked", and those are different screens.
   */
  bookletRequestedAt: string | null
  bookletPurchasedAt: string | null
  termsAcceptedAt: string | null
  /** The كراسة الشروط that was accepted — the document id is its version. */
  acceptedBookletDocumentId: string | null
  /** The booklet cost nothing, so no payment was taken for it. */
  bookletFree: boolean
  /** Where the bidder stands, in the words of the requirements. */
  eligibility: 'Incomplete' | 'UnderReview' | 'Accepted' | 'Rejected'
  eligibilityReason: string | null
  /** What is left to do with the deposit once the auction is over. */
  depositSettlement:
    | 'None' | 'Held' | 'ToRefund' | 'ToRelease' | 'ToForfeit' | 'AppliedToPurchase' | 'Closed'
  depositMethod: string | null
  depositRequestedAt: string | null
  depositPaidAt: string | null
  guaranteeDocumentId: string | null
  guaranteeExpiresAt: string | null
  guaranteeVerifiedAt: string | null
  keyEpoch: number
  eligibleAt: string | null
  revocationReason: string | null
  depositResolvedAt: string | null
  depositForfeited: boolean
  /** 'Booklet' or 'Deposit', when the gateway last refused one of them. */
  paymentFailurePurpose: string | null
  paymentFailureReason: string | null
  paymentFailedAt: string | null
}

export type SubscriptionStatus =
  | 'Draft'
  | 'BookletPurchased'
  | 'TermsAccepted'
  | 'AwaitingDeposit'
  | 'Eligible'
  | 'Revoked'

export interface SigningKey {
  secretHex: string
  keyEpoch: number
}

export interface BidReceipt {
  auctionId: string
  bidderId: string
  clientBidId: string
  offset: number
  serverTimestampMs: number
  signature: string
}

/**
 * شهادة مزايدة. Read back out of the append-only log by the service, so every
 * field here is what is in the record rather than what this browser submitted.
 */
export interface BidCertificate {
  reference: string
  auctionId: string
  bidderId: string
  clientBidId: string
  offset: number
  amountMinorUnits: number
  clientTimestampMs: number
  serverTimestampMs: number
  channel: string
  enteredByUserId: string | null
  signature: string
  /** Null when no receipt was presented — only staff checking one get true/false. */
  presentedSignatureMatched: boolean | null
  issuedAt: string
}

/** The award, for its winner (participant service, from the award's snapshot). */
export interface WinnerAward {
  auctionId: string
  amountMinorUnits: number
  brokerageMinorUnits: number
  confirmedAt: string
  complianceDeadline: string
  letterAvailable: boolean
  winnerNotifiedAt: string | null
  paidMinorUnits: number
  remainingMinorUnits: number
  transferStatus: 'NotStarted' | 'InProgress' | 'Completed'
  transferCompletedAt: string | null
  settledAt: string | null
  withdrawnAt: string | null
  nextStep: 'AwaitingLetter' | 'Pay' | 'Transfer' | 'Done' | 'Withdrawn'
}
