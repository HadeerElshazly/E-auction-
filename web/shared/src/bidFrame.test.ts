/**
 * The TypeScript side of the cross-language frame contract.
 *
 * Run with node's own test runner, so the portals need no test framework:
 *   node --experimental-strip-types --test web/shared/src/bidFrame.test.ts
 *
 * The C# side of the same vector is tests/EAuction.Tests/FrameVectorTests.cs.
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readFileSync } from 'node:fs'
import { buildBidFrame, newNonce, toHex, writeDotNetGuid } from './bidFrame.ts'

const vector = JSON.parse(
  readFileSync(new URL('./__fixtures__/bid-frame-vector.json', import.meta.url), 'utf8'),
) as {
  auctionId: string
  bidderId: string
  clientBidId: string
  amountMinorUnits: number
  clientTimestampMs: number
  nonce: string
  signingSecretHex: string
  frameHex: string
}

test('the browser builds the same frame .NET does', async () => {
  const frame = await buildBidFrame({
    auctionId: vector.auctionId,
    bidderId: vector.bidderId,
    amountMinorUnits: vector.amountMinorUnits,
    clientBidId: vector.clientBidId,
    clientTimestampMs: vector.clientTimestampMs,
    nonce: BigInt(vector.nonce),
    signingSecretHex: vector.signingSecretHex,
  })

  assert.equal(frame.length, 104)
  assert.equal(toHex(frame), vector.frameHex)
})

test('a GUID is written in .NET byte order, not RFC 4122 order', () => {
  // The exact mistake the shared vector exists to catch, stated on its own so a
  // failure here says what is wrong rather than just "the frame differs".
  const view = new DataView(new ArrayBuffer(16))
  writeDotNetGuid(view, 0, '0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9')

  assert.equal(
    toHex(new Uint8Array(view.buffer)),
    '3D2C1B0A5F4E71608293A4B5C6D7E8F9',
    'The first three groups are little-endian; the last eight bytes are in order.',
  )
})

test('a different amount produces a different signature', async () => {
  // Guards against an HMAC computed over the wrong slice: if it covered only the
  // ids, two bids at different prices would sign identically and a replay could
  // change the amount.
  const base = {
    auctionId: vector.auctionId,
    bidderId: vector.bidderId,
    clientBidId: vector.clientBidId,
    clientTimestampMs: vector.clientTimestampMs,
    nonce: BigInt(vector.nonce),
    signingSecretHex: vector.signingSecretHex,
  }

  const cheap = await buildBidFrame({ ...base, amountMinorUnits: 100_000_000 })
  const dear = await buildBidFrame({ ...base, amountMinorUnits: 120_000_000 })

  assert.notEqual(toHex(cheap.subarray(72)), toHex(dear.subarray(72)))
})

test('the signature covers only the first 72 bytes', async () => {
  // The catcher appends its own timestamp and pod ordinal after byte 104. If the
  // HMAC covered more than 72 bytes, every accepted bid would fail verification
  // the moment the server stamped it.
  const frame = await buildBidFrame({
    auctionId: vector.auctionId,
    bidderId: vector.bidderId,
    amountMinorUnits: vector.amountMinorUnits,
    clientBidId: vector.clientBidId,
    clientTimestampMs: vector.clientTimestampMs,
    nonce: BigInt(vector.nonce),
    signingSecretHex: vector.signingSecretHex,
  })

  assert.equal(toHex(frame.subarray(72)), vector.frameHex.slice(144))
})

test('a non-integer amount is refused rather than silently truncated', async () => {
  // A riyal value that slipped through unmultiplied, or a float, must not become a
  // bid for an amount nobody chose.
  await assert.rejects(
    () =>
      buildBidFrame({
        auctionId: vector.auctionId,
        bidderId: vector.bidderId,
        amountMinorUnits: 1_200_000.5,
        clientBidId: vector.clientBidId,
        clientTimestampMs: vector.clientTimestampMs,
        nonce: 1n,
        signingSecretHex: vector.signingSecretHex,
      }),
    /non-negative integer of halalas/,
  )
})

test('nonces fit in a signed int64 and differ', () => {
  const seen = new Set<string>()
  for (let i = 0; i < 200; i++) {
    const n = newNonce()
    assert.ok(n >= 0n && n <= 0x7fffffffffffffffn, `${n} is out of int64 range`)
    seen.add(n.toString())
  }
  assert.ok(seen.size > 190, `expected distinct nonces, got ${seen.size} of 200`)
})
