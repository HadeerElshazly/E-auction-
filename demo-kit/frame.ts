// Builds a signed bid frame with the bidder portal's own code and prints it as JSON.
// usage: node --experimental-strip-types frame.ts <auctionId> <bidderId> <amountHalalas> <secretHex>
import { buildBidFrame, newClientBidId, newNonce, toHex } from '../web/shared/src/bidFrame.ts'

const [auctionId, bidderId, amount, secret] = process.argv.slice(2)
const clientBidId = newClientBidId()
const frame = await buildBidFrame({
  auctionId, bidderId, amountMinorUnits: Number(amount), clientBidId,
  clientTimestampMs: Date.now(), nonce: newNonce(), signingSecretHex: secret,
})
console.log(JSON.stringify({ clientBidId, hex: toHex(frame) }))
