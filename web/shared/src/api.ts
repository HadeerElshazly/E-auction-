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
  }
}

export type Api = ReturnType<typeof api>
