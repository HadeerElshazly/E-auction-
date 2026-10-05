/**
 * Server-sent events read over `fetch`, not `EventSource`.
 *
 * `EventSource` cannot set request headers, so an authenticated stream would have to
 * carry its bearer token in the query string — where it lands in every access log,
 * proxy log and browser history entry along the way. A token that can award land
 * does not belong in a URL. Reading the stream from `fetch` costs reimplementing
 * reconnect, which is below, and buys an `Authorization` header.
 *
 * The wire format is the SSE one and nothing more of it than this platform uses:
 * `event:` names, `data:` payloads, `:` comments as keep-alives, and a blank line
 * ending each frame.
 */

export interface SseOptions {
  url: string
  token?: string
  signal: AbortSignal
  onEvent: (event: string, data: string) => void
  /** Called when the stream opens, and again after each successful reconnect. */
  onOpen?: () => void
  /** Called when it drops. The reader reconnects regardless; this is for the UI. */
  onError?: (error: unknown) => void
}

/**
 * Reads until the signal aborts, reconnecting with backoff.
 *
 * Reconnecting is safe to do blindly because the server sends a snapshot first: the
 * client is told the truth as of now rather than replayed from an offset, so a
 * missed delta cannot leave it permanently wrong. That is also why there is no
 * `Last-Event-ID` here — the server could not honour one.
 */
export async function readEventStream(options: SseOptions): Promise<void> {
  let attempt = 0

  while (!options.signal.aborted) {
    try {
      const headers: Record<string, string> = { Accept: 'text/event-stream' }
      if (options.token) headers.Authorization = `Bearer ${options.token}`

      const response = await fetch(options.url, {
        headers,
        signal: options.signal,
        // A stream must not be served from cache, and some proxies will try.
        cache: 'no-store',
      })

      if (!response.ok || !response.body) {
        throw new Error(`stream refused: ${response.status}`)
      }

      attempt = 0
      options.onOpen?.()
      await pump(response.body, options)

      // A clean end of body is still a disconnect: reconnect, with backoff, so a
      // server that closes immediately is not hammered.
      throw new Error('stream ended')
    } catch (error) {
      if (options.signal.aborted) return
      options.onError?.(error)

      // 1s, 2s, 4s, 8s, capped at 15s. Capped rather than unbounded because the
      // thing being watched is a live auction: a client that backs off to minutes
      // has stopped being a participant.
      const delay = Math.min(1000 * 2 ** attempt, 15_000)
      attempt++
      await sleep(delay, options.signal)
    }
  }
}

async function pump(body: ReadableStream<Uint8Array>, options: SseOptions): Promise<void> {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    while (!options.signal.aborted) {
      const { done, value } = await reader.read()
      if (done) return

      buffer += decoder.decode(value, { stream: true })

      // Frames are separated by a blank line. A chunk can split one anywhere, so
      // only complete frames are taken and the remainder stays buffered.
      let split = buffer.indexOf('\n\n')
      while (split !== -1) {
        const frame = buffer.slice(0, split)
        buffer = buffer.slice(split + 2)
        dispatch(frame, options)
        split = buffer.indexOf('\n\n')
      }
    }
  } finally {
    // Releasing the lock lets the body be cancelled by the abort signal rather
    // than leaking a half-read stream per reconnect.
    reader.releaseLock()
  }
}

function dispatch(frame: string, options: SseOptions): void {
  let event = 'message'
  const data: string[] = []

  for (const line of frame.split('\n')) {
    // A comment. Keep-alives arrive as these and carry nothing.
    if (line.startsWith(':')) continue

    const colon = line.indexOf(':')
    const field = colon === -1 ? line : line.slice(0, colon)
    // One optional space after the colon is part of the format, not the value.
    const value = colon === -1 ? '' : line.slice(colon + 1).replace(/^ /, '')

    if (field === 'event') event = value
    else if (field === 'data') data.push(value)
  }

  if (data.length > 0) options.onEvent(event, data.join('\n'))
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(done, ms)
    signal.addEventListener('abort', done, { once: true })

    function done() {
      clearTimeout(timer)
      signal.removeEventListener('abort', done)
      resolve()
    }
  })
}
