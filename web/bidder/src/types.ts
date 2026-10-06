/** From src/EAuction.QueryBff/Program.cs. */

export interface AuctionSummary {
  id: string
  status: string
  nameAr: string
  nameEn: string
  channel: string
  bidderVisibility: string
  startsAt: string
  endsAt: string
  openingPriceMinorUnits: number
  priceMinorUnits: number | null
  minimumNextBidMinorUnits: number
  depositMinorUnits: number
  bookletPriceMinorUnits: number
  plotCount: number
  totalAreaSqm: number
}

export interface Plot {
  id: string
  deedNumber: string
  areaSqm: number
  latitude: string | null
  longitude: string | null
  descriptionAr: string | null
  descriptionEn: string | null
}

export interface AuctionDetail extends Omit<AuctionSummary, 'plotCount'> {
  effectiveEndsAt: string | null
  minIncrementMinorUnits: number
  quietPeriodSeconds: number | null
  maxExtensions: number
  extensionsUsed: number
  plots: Plot[]
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
