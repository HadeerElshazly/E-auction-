/**
 * Every refusal the bid path can return has Arabic to show for it.
 *
 * The portal falls back to the raw identifier — `رُفضت المزايدة: ${e.reason}` —
 * which is the right fallback and a bad thing to rely on: a bidder who presses the
 * button as the gavel falls would have been shown "AuctionClosed", in English, at
 * the single most charged moment in the product. That one was genuinely missing.
 *
 * The list below is RejectionReason from src/EAuction.Core/BidOutcome.cs. It is a
 * copy, and a copy is the honest option here — a C# enum cannot be imported into a
 * browser bundle, and the alternative is a build step that generates TypeScript
 * from C#, which is a large amount of machinery for twelve names that change about
 * once a year. What this does buy is that adding a reason on the server and
 * forgetting the portal fails here rather than in front of a citizen.
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { reasons } from './reasons.ts'

const FROM_THE_SERVER = [
  'AuctionClosed',
  'BadSignature',
  'BelowMinimumIncrement',
  'BelowOpeningPrice',
  'DuplicateBidId',
  'MalformedFrame',
  'NotEligible',
  'OutsideWindow',
  'RateLimited',
  'SelfOutbid',
  'UnknownAuction',
  // Not in the enum: the catcher returns it as a bare string when the token's
  // subject does not match the frame's bidder.
  'BidderMismatch',
]

// NotTheClerk is deliberately absent: it is the refusal for an onsite frame from
// somebody who is not the auction's clerk, and ClerkTerminal has the Arabic for it.
// A bidder cannot produce it because AuctionPage does not render the bid box on a
// hall auction at all — which is the condition this absence rests on, and was
// briefly untrue: the box was offered on a hall auction once it went Live, and the
// bidder who pressed it would have been shown the identifier.

test('every refusal a bidder can receive has Arabic', () => {
  const missing = FROM_THE_SERVER.filter((r) => !reasons[r])

  assert.deepEqual(missing, [], `no Arabic for: ${missing.join(', ')}`)
})

test('no refusal text is itself English', () => {
  for (const [code, text] of Object.entries(reasons)) {
    assert.match(text, /[؀-ۿ]/, `${code} is not Arabic: ${text}`)
  }
})
