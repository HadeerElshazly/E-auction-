/**
 * The bid frame, built and signed in the browser.
 *
 * This is a second implementation of a wire format whose first implementation is
 * `src/EAuction.Core/BidFrame.cs`. That is a deliberate cost: the signing key is the
 * bidder's own, and a server that signed on their behalf would destroy the evidential
 * value of a signed bid — nobody could later tell a bid the bidder made from one the
 * platform made for them.
 *
 * The two implementations are pinned to one committed vector
 * (`__fixtures__/bid-frame-vector.json`) from both sides, because the failure mode
 * otherwise is silent: a mismatch rejects every browser bid as `InvalidSignature`
 * while neither codebase looks wrong.
 *
 *   offset  size  field
 *        0    16  auctionId        .NET Guid byte order (see below)
 *       16    16  bidderId         .NET Guid byte order
 *       32     8  amount           int64 little-endian, halalas
 *       40     8  clientTimestamp  int64 little-endian, ms since epoch
 *       48    16  clientBidId      .NET Guid byte order
 *       64     8  nonce            int64 little-endian
 *       72    32  hmac             HMAC-SHA256 over bytes [0, 72)
 *      104          end of what the client sends
 *
 * The server appends its own timestamp, pod ordinal, channel and operator id after
 * byte 104. The HMAC covers only the first 72 bytes, so appending that metadata does
 * not invalidate the bidder's signature.
 */

export const FRAME = {
  auctionId: 0,
  bidderId: 16,
  amount: 32,
  clientTimestamp: 40,
  clientBidId: 48,
  nonce: 64,
  hmac: 72,
  /** Bytes covered by the signature. */
  signedLength: 72,
  /** Total length the client sends. */
  clientLength: 104,
} as const

/**
 * Writes a GUID the way .NET's `Guid.TryWriteBytes` does: the first three groups
 * little-endian, the last eight bytes in order.
 *
 * This is NOT RFC 4122 byte order, and it is the single most likely thing to get
 * wrong here. Writing the string's bytes in order produces a frame the catcher reads
 * as a different auction and a different bidder, so it is rejected as
 * `UnknownAuction` — which looks like a configuration problem, not a codec bug.
 */
export function writeDotNetGuid(view: DataView, offset: number, guid: string): void {
  const hex = guid.replace(/-/g, '')
  if (hex.length !== 32 || /[^0-9a-fA-F]/.test(hex)) {
    throw new Error(`Not a GUID: ${guid}`)
  }

  const byte = (i: number): number => Number.parseInt(hex.slice(i * 2, i * 2 + 2), 16)

  // Groups 1-3, reversed.
  view.setUint8(offset + 0, byte(3))
  view.setUint8(offset + 1, byte(2))
  view.setUint8(offset + 2, byte(1))
  view.setUint8(offset + 3, byte(0))
  view.setUint8(offset + 4, byte(5))
  view.setUint8(offset + 5, byte(4))
  view.setUint8(offset + 6, byte(7))
  view.setUint8(offset + 7, byte(6))

  // Groups 4-5, in order.
  for (let i = 8; i < 16; i++) view.setUint8(offset + i, byte(i))
}

export interface BidFrameInput {
  auctionId: string
  bidderId: string
  /** Halalas. An integer; see money.ts for why this is never a riyal float. */
  amountMinorUnits: number
  clientBidId: string
  clientTimestampMs: number
  nonce: bigint
  /** The bidder's derived key, hex, from the participant service. */
  signingSecretHex: string
}

/** Builds the 104-byte signed frame. */
export async function buildBidFrame(input: BidFrameInput): Promise<Uint8Array<ArrayBuffer>> {
  if (!Number.isSafeInteger(input.amountMinorUnits) || input.amountMinorUnits < 0) {
    throw new Error(`Amount must be a non-negative integer of halalas, got ${input.amountMinorUnits}`)
  }

  const buffer = new ArrayBuffer(FRAME.clientLength)
  const frame = new Uint8Array(buffer)
  const view = new DataView(buffer)

  writeDotNetGuid(view, FRAME.auctionId, input.auctionId)
  writeDotNetGuid(view, FRAME.bidderId, input.bidderId)
  view.setBigInt64(FRAME.amount, BigInt(input.amountMinorUnits), true)
  view.setBigInt64(FRAME.clientTimestamp, BigInt(input.clientTimestampMs), true)
  writeDotNetGuid(view, FRAME.clientBidId, input.clientBidId)
  view.setBigInt64(FRAME.nonce, input.nonce, true)

  const key = await crypto.subtle.importKey(
    'raw',
    fromHex(input.signingSecretHex),
    { name: 'HMAC', hash: 'SHA-256' },
    false, // not extractable: the key cannot be read back out of the browser
    ['sign'],
  )

  const signature = await crypto.subtle.sign(
    'HMAC',
    key,
    frame.subarray(0, FRAME.signedLength),
  )

  frame.set(new Uint8Array(signature), FRAME.hmac)
  return frame
}

/** A fresh client bid id. Used by the catcher to reject a double-submitted bid. */
export function newClientBidId(): string {
  return crypto.randomUUID()
}

/** A nonce in int64 range, so the frame's signed bytes differ between two equal bids. */
export function newNonce(): bigint {
  const bytes = crypto.getRandomValues(new Uint8Array(8))
  // Clear the top bit: the field is a signed int64 on the server, and a negative
  // value is legal but needlessly confusing in a log.
  bytes[0] = (bytes[0] ?? 0) & 0x7f
  let value = 0n
  for (const b of bytes) value = (value << 8n) | BigInt(b)
  return value
}

export function fromHex(hex: string): Uint8Array<ArrayBuffer> {
  if (hex.length % 2 !== 0) throw new Error('Hex string has an odd length.')
  const bytes = new Uint8Array(new ArrayBuffer(hex.length / 2))
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = Number.parseInt(hex.slice(i * 2, i * 2 + 2), 16)
  }
  return bytes
}

export function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0').toUpperCase()).join('')
}
