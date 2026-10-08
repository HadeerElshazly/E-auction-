import type { Session } from './auth'

/**
 * One HTTP helper for all four services.
 *
 * It exists mainly so error handling is in one place: these APIs answer with a
 * `{ problems: [...] }` or `{ reason: "..." }` body that a user can actually be
 * shown, and `fetch` discards it unless someone remembers to read it. A portal that
 * says "something went wrong" when the service said "BelowOpeningPrice" is a portal
 * whose users cannot tell a mistake from an outage.
 */
/** Raised on the window when a signed-in request comes back 401. */
export const SESSION_EXPIRED = 'eauction:session-expired'

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly reason: string | null,
    readonly problems: string[],
    message: string,
    /** Present when the service answered with a step-up challenge. */
    readonly stepUp?: StepUpChallenge,
  ) {
    super(message)
    this.name = 'ApiError'
  }

  /** True when the server rejected the request on its merits, not by failing. */
  get isRejection(): boolean {
    return this.status >= 400 && this.status < 500
  }

  /**
   * True when confirming identity again and retrying would work.
   *
   * The distinction a bare 403 cannot make: "you are the wrong person", where
   * retrying is pointless, versus "confirm it is you", where retrying is the whole
   * remedy.
   */
  get needsStepUp(): boolean {
    return this.stepUp !== undefined
  }
}

/** What the service says to ask the identity provider for. */
export interface StepUpChallenge {
  /** Whether a confirmation is missing altogether, or simply too old. */
  stale: boolean
  /** Values for `acr_values`, so the portal does not hard-code a level. */
  requiredAcr: string[]
  maxAgeSeconds: number
}

export interface ApiOptions {
  baseUrl: string
  session?: Session | null
}

async function send<T>(
  { baseUrl, session }: ApiOptions,
  method: string,
  path: string,
  body?: unknown,
): Promise<T> {
  const headers: Record<string, string> = {}
  if (session) headers.Authorization = `Bearer ${session.accessToken}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  let response: Response
  try {
    response = await fetch(baseUrl.replace(/\/$/, '') + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch (cause) {
    // fetch rejects for a blocked CORS preflight exactly as it does for an
    // unreachable host, so name both rather than guess.
    throw new ApiError(
      0,
      'Unreachable',
      [],
      `Could not reach ${baseUrl}. The service may be down, or its ` +
        `Cors__AllowedOrigins may not include ${location.origin}.`,
    )
  }

  const text = await response.text()
  const parsed = text ? safeJson(text) : null

  if (!response.ok) {
    // A signed-in request refused for its token: the session is over, whatever the
    // portal thought. Said once, here, so every screen ends up at the sign-in
    // button rather than at an error with no way forward (useSession listens).
    if (response.status === 401 && session) window.dispatchEvent(new Event(SESSION_EXPIRED))

    const reason = typeof parsed?.reason === 'string' ? parsed.reason : null
    const problems = Array.isArray(parsed?.problems) ? (parsed.problems as string[]) : []
    throw new ApiError(
      response.status,
      reason,
      problems,
      problems[0] ?? reason ?? `${method} ${path} failed (${response.status})`,
      stepUpFrom(response.status, parsed),
    )
  }

  return parsed as T
}

function stepUpFrom(status: number, body: Record<string, unknown> | null): StepUpChallenge | undefined {
  if (status !== 403 || body === null) return undefined

  const reason = body.reason
  if (reason !== 'StepUpRequired' && reason !== 'StepUpStale') return undefined

  return {
    stale: reason === 'StepUpStale',
    requiredAcr: Array.isArray(body.requiredAcr) ? (body.requiredAcr as string[]) : ['high'],
    maxAgeSeconds: typeof body.maxAgeSeconds === 'number' ? body.maxAgeSeconds : 300,
  }
}

function safeJson(text: string): Record<string, unknown> | null {
  try {
    return JSON.parse(text) as Record<string, unknown>
  } catch {
    // A developer exception page, an HTML error from a proxy, a plain string.
    return null
  }
}

export function api(options: ApiOptions) {
  return {
    get: <T>(path: string) => send<T>(options, 'GET', path),
    post: <T>(path: string, body?: unknown) => send<T>(options, 'POST', path, body),
    put: <T>(path: string, body: unknown) => send<T>(options, 'PUT', path, body),
    del: <T>(path: string) => send<T>(options, 'DELETE', path),

    /**
     * A file, as a multipart form.
     *
     * Its own method rather than a `body` that happens to be a FormData, because
     * `send` sets Content-Type to application/json and a multipart request must
     * not have one set at all — the browser writes it, boundary included, and a
     * hand-written header would name a boundary the body does not use.
     */
    upload: async <T>(path: string, file: File, fields: Record<string, string> = {}): Promise<T> => {
      const form = new FormData()
      form.append('file', file)
      for (const [name, value] of Object.entries(fields)) form.append(name, value)

      const headers: Record<string, string> = {}
      if (options.session) headers.Authorization = `Bearer ${options.session.accessToken}`

      let response: Response
      try {
        response = await fetch(options.baseUrl.replace(/\/$/, '') + path, {
          method: 'POST',
          headers,
          body: form,
        })
      } catch {
        throw new ApiError(
          0,
          'Unreachable',
          [],
          `Could not reach ${options.baseUrl}. The service may be down, or its ` +
            `Cors__AllowedOrigins may not include ${location.origin}.`,
        )
      }

      const text = await response.text()
      const parsed = text ? safeJson(text) : null

      if (!response.ok) {
        const reason = typeof parsed?.reason === 'string' ? parsed.reason : null
        const problems = Array.isArray(parsed?.problems) ? (parsed.problems as string[]) : []
        throw new ApiError(
          response.status,
          reason,
          problems,
          problems[0] ?? reason ?? `Upload failed (${response.status})`,
          stepUpFrom(response.status, parsed),
        )
      }

      return parsed as T
    },

    /** The bid path: a fixed-width binary frame, not JSON. */
    postFrame: async <T>(path: string, frame: Uint8Array<ArrayBuffer>): Promise<T> => {
      const headers: Record<string, string> = { 'Content-Type': 'application/octet-stream' }
      if (options.session) headers.Authorization = `Bearer ${options.session.accessToken}`

      const response = await fetch(options.baseUrl.replace(/\/$/, '') + path, {
        method: 'POST',
        headers,
        body: frame,
      })

      const text = await response.text()
      const parsed = text ? safeJson(text) : null

      if (!response.ok) {
        const reason = typeof parsed?.reason === 'string' ? parsed.reason : null
        throw new ApiError(response.status, reason, [], reason ?? `Bid rejected (${response.status})`)
      }
      return parsed as T
    },

    /**
     * Downloads a file the service protects with a bearer token.
     *
     * An `<a download>` or a `window.open` cannot carry an Authorization header, so
     * a CSV behind a role policy has to be fetched and handed to the browser as a
     * blob. The alternative — a signed one-time download URL — is a second auth
     * mechanism on an endpoint that already has one.
     *
     * The filename comes from Content-Disposition when the service sets one, which
     * التقارير do: the second thing anybody asks of a downloaded report is which
     * day it was run on.
     */
    download: async (path: string, fallbackName: string): Promise<void> => {
      const headers: Record<string, string> = {}
      if (options.session) headers.Authorization = `Bearer ${options.session.accessToken}`

      const response = await fetch(options.baseUrl.replace(/\/$/, '') + path, { headers })

      if (!response.ok) {
        const text = await response.text()
        const parsed = text ? safeJson(text) : null
        const reason = typeof parsed?.reason === 'string' ? parsed.reason : null
        throw new ApiError(
          response.status,
          reason,
          Array.isArray(parsed?.problems) ? (parsed.problems as string[]) : [],
          reason ?? `Download failed (${response.status})`,
          stepUpFrom(response.status, parsed),
        )
      }

      const url = URL.createObjectURL(await response.blob())
      try {
        const link = document.createElement('a')
        link.href = url
        link.download = fileNameFrom(response.headers.get('content-disposition')) ?? fallbackName
        document.body.appendChild(link)
        link.click()
        link.remove()
      } finally {
        // Released on the next tick rather than immediately: revoking it in the
        // same turn as the click races the navigation the click starts, and the
        // download arrives empty often enough to look intermittent.
        window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
      }
    },
  }
}

/**
 * The filename out of a Content-Disposition header.
 *
 * `filename*` first, because that is the one that survives a non-ASCII name — and
 * these reports are named in English but the rule costs one line and the next
 * report might not be.
 */
function fileNameFrom(header: string | null): string | null {
  if (!header) return null

  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(header)?.[1]
  if (encoded) {
    try {
      return decodeURIComponent(encoded)
    } catch {
      // A malformed header is not worth failing a download over.
    }
  }

  return /filename="?([^";]+)"?/i.exec(header)?.[1] ?? null
}

export type Api = ReturnType<typeof api>
