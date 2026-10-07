/**
 * The countdown a bidder reads.
 *
 * This is the one string in the product that sits on a public page with no staff
 * anywhere near it, and it spent its life saying "ended", "3d 4h" and "2h 05m" —
 * developer shorthand nobody caught, because the screens anyone looked at were the
 * Arabic ones that happen not to call it.
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { untilText } from './money.ts'

const now = new Date('2026-08-26T12:00:00Z')
const at = (ms: number) => untilText(new Date(now.getTime() + ms), now)

test('an auction that has closed says so in Arabic', () => {
  assert.equal(at(0), 'انتهى')
  assert.equal(at(-1), 'انتهى')
  assert.equal(at(-86_400_000), 'انتهى')
})

test('days and hours while it is still far off', () => {
  assert.equal(at(3 * 86_400_000 + 4 * 3_600_000), '3 يوم 4 ساعة')
})

test('hours and minutes once the days are gone', () => {
  assert.equal(at(2 * 3_600_000 + 5 * 60_000), '2 ساعة 05 دقيقة')
})

test('a clock in the last hour, which is when it is watched', () => {
  assert.equal(at(5 * 60_000 + 3_000), '05:03')
  assert.equal(at(59_000), '00:59')
  assert.equal(at(1_000), '00:01')
})

test('never emits a Latin letter', () => {
  // The actual regression. Any English word here reaches a citizen on the one page
  // that is not behind a staff login.
  const samples = [
    -1, 0, 1_000, 59_000, 60_000, 3_599_000, 3_600_000,
    86_399_000, 86_400_000, 10 * 86_400_000,
  ]

  for (const ms of samples) {
    assert.doesNotMatch(at(ms), /[A-Za-z]/, `at ${ms}ms`)
  }
})
