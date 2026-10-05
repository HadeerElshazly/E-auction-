/** From src/EAuction.QueryBff/Program.cs. */

export interface AuctionSummary {
  id: string
  status: string
  nameAr: string
  nameEn: string
  channel: string
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

export interface LivePrice {
  auctionId: string
  status: string
  priceMinorUnits: number | null
  minimumNextBidMinorUnits: number
  /** A per-auction pseudonym, never an id or a name (D-22). */
  leaderAlias: string | null
  leaderIsYou: boolean
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
  bookletPurchasedAt: string | null
  termsAcceptedAt: string | null
  depositMethod: string | null
  depositPaidAt: string | null
  guaranteeDocumentId: string | null
  guaranteeExpiresAt: string | null
  guaranteeVerifiedAt: string | null
  keyEpoch: number
  eligibleAt: string | null
  revocationReason: string | null
  depositResolvedAt: string | null
  depositForfeited: boolean
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
