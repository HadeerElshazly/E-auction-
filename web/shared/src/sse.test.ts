/**
 * The SSE reader's framing.
 *
 * Run with:
 *   node --experimental-strip-types --test web/shared/src/sse.test.ts
 *
 * Framing is where hand-written stream parsers break: a chunk boundary can fall
 * anywhere, including in the middle of a frame, in the middle of a UTF-8 character,
 * or between the two newlines that end a frame. A parser that assumes one chunk is
 * one frame works perfectly on a developer's machine and drops events in
 * production, where the price it dropped was somebody's winning bid.
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readEventStream } from './sse.ts'

/** Serves the given chunks as one streamed response, then ends. */
function serve(chunks: string[]): typeof fetch {
  return (async () => {
    const encoder = new TextEncoder()
    return new Response(
      new ReadableStream<Uint8Array>({
        start(controller) {
          for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
          controller.close()
        },
      }),
      { status: 200, headers: { 'Content-Type': 'text/event-stream' } },
    )
  }) as unknown as typeof fetch
}

/** Reads until `expected` events have arrived, or the stream ends. */
async function collect(
  chunks: string[],
  expected: number,
): Promise<Array<{ event: string; data: string }>> {
  const original = globalThis.fetch
  globalThis.fetch = serve(chunks)

  const received: Array<{ event: string; data: string }> = []
  const controller = new AbortController()

  try {
    const reading = readEventStream({
      url: 'http://localhost/stream',
      signal: controller.signal,
      onEvent: (event, data) => {
        received.push({ event, data })
        if (received.length >= expected) controller.abort()
      },
    })

    // The reader reconnects on a clean end of body, so stop it once the stream is
    // exhausted rather than letting it retry against the same canned response.
    const guard = setTimeout(() => controller.abort(), 1000)
    await reading
    clearTimeout(guard)
  } finally {
    globalThis.fetch = original
  }

  return received
}

test('a frame delivered in one chunk is parsed', async () => {
  const received = await collect(['event: price\ndata: {"a":1}\n\n'], 1)

  assert.deepEqual(received, [{ event: 'price', data: '{"a":1}' }])
})

test('a frame split across chunks is reassembled', async () => {
  // The case that matters: a 1.5 KB snapshot does not arrive in one TCP segment.
  const received = await collect(['event: snap', 'shot\nda', 'ta: {"price":12', '00}\n\n'], 1)

  assert.deepEqual(received, [{ event: 'snapshot', data: '{"price":1200}' }])
})

test('several frames in one chunk are all parsed', async () => {
  // A reconnect replays buffered verdicts back to back, so they coalesce.
  const received = await collect(
    ['event: verdict\ndata: one\n\nevent: verdict\ndata: two\n\nevent: price\ndata: three\n\n'],
    3,
  )

  assert.deepEqual(received, [
    { event: 'verdict', data: 'one' },
    { event: 'verdict', data: 'two' },
    { event: 'price', data: 'three' },
  ])
})

test('a chunk boundary between the two terminating newlines is handled', async () => {
  // The nastiest split: the frame looks complete after the first chunk.
  const received = await collect(['event: price\ndata: x\n', '\nevent: price\ndata: y\n\n'], 2)

  assert.deepEqual(received, [
    { event: 'price', data: 'x' },
    { event: 'price', data: 'y' },
  ])
})

test('keep-alive comments are ignored', async () => {
  // Sent every twenty seconds so proxies and mobile networks do not close an idle
  // stream. A parser that treated them as events would deliver empty prices.
  const received = await collect([': keep-alive\n\n', 'event: price\ndata: real\n\n'], 1)

  assert.deepEqual(received, [{ event: 'price', data: 'real' }])
})

test('an event with no name defaults to message', async () => {
  const received = await collect(['data: bare\n\n'], 1)

  assert.deepEqual(received, [{ event: 'message', data: 'bare' }])
})

test('exactly one space after the colon is stripped', async () => {
  // Part of the format, not the value. Stripping more would corrupt indented JSON;
  // stripping none would make every payload start with a space.
  const received = await collect(['event: price\ndata:  leading-space-kept\n\n'], 1)

  assert.deepEqual(received, [{ event: 'price', data: ' leading-space-kept' }])
})

test('multi-line data is joined with newlines', async () => {
  const received = await collect(['event: price\ndata: line one\ndata: line two\n\n'], 1)

  assert.deepEqual(received, [{ event: 'price', data: 'line one\nline two' }])
})

test('a multi-byte character split across chunks is not corrupted', async () => {
  // The portal is Arabic, so every payload is full of multi-byte characters and a
  // chunk boundary will land inside one. The decoder must be told to stream.
  const encoder = new TextEncoder()
  const payload = encoder.encode('event: price\ndata: مزايد\n\n')

  const original = globalThis.fetch
  const received: string[] = []
  const controller = new AbortController()

  globalThis.fetch = (async () =>
    new Response(
      new ReadableStream<Uint8Array>({
        start(controller) {
          // One byte at a time: every character is split.
          for (const byte of payload) controller.enqueue(new Uint8Array([byte]))
          controller.close()
        },
      }),
      { status: 200 },
    )) as unknown as typeof fetch

  try {
    const reading = readEventStream({
      url: 'http://localhost/stream',
      signal: controller.signal,
      onEvent: (_, data) => {
        received.push(data)
        controller.abort()
      },
    })
    const guard = setTimeout(() => controller.abort(), 1000)
    await reading
    clearTimeout(guard)
  } finally {
    globalThis.fetch = original
  }

  assert.deepEqual(received, ['مزايد'])
})

test('a refused stream is reported and retried, not thrown', async () => {
  // A 503 while the BFF is still warming is expected on a cold cluster. The reader
  // must back off and come back rather than leaving the panel dead for ever.
  const original = globalThis.fetch
  let attempts = 0
  const errors: unknown[] = []
  const controller = new AbortController()

  globalThis.fetch = (async () => {
    attempts++
    if (attempts >= 2) controller.abort()
    return new Response('warming up', { status: 503 })
  }) as unknown as typeof fetch

  try {
    await readEventStream({
      url: 'http://localhost/stream',
      signal: controller.signal,
      onEvent: () => assert.fail('no events should arrive'),
      onError: (e) => errors.push(e),
    })
  } finally {
    globalThis.fetch = original
  }

  assert.ok(attempts >= 2, `expected a retry, got ${attempts} attempt(s)`)
  assert.ok(errors.length >= 1)
  assert.match(String(errors[0]), /503/)
})

test('aborting stops the reader without reconnecting', async () => {
  const original = globalThis.fetch
  let attempts = 0
  const controller = new AbortController()

  globalThis.fetch = (async () => {
    attempts++
    return new Response(new ReadableStream<Uint8Array>({ start: (c) => c.close() }), {
      status: 200,
    })
  }) as unknown as typeof fetch

  try {
    const reading = readEventStream({
      url: 'http://localhost/stream',
      signal: controller.signal,
      onEvent: () => {},
    })
    controller.abort()
    await reading
  } finally {
    globalThis.fetch = original
  }

  assert.ok(attempts <= 1, `aborted reader kept reconnecting: ${attempts} attempts`)
})
