/**
 * Service endpoints, from the environment at build time.
 *
 * Vite inlines `import.meta.env` at build, so these are baked into the bundle. That
 * is correct for URLs and a client id — all of which are public by nature — and is
 * exactly why no secret belongs here. The Keycloak clients are public clients with
 * PKCE precisely because a browser bundle cannot keep one.
 *
 * The names and the development fallbacks come from `endpoints.ts`, which the
 * build-time Content-Security-Policy reads as well: the policy has to permit
 * exactly the origins this file produces, and a second hand-maintained list would
 * drift the first time a service moved.
 */
import { ENDPOINTS, type EndpointName } from './endpoints'

function required(value: string | undefined, name: string, fallback: string): string {
  if (value && value.length > 0) return value
  if (import.meta.env.PROD) {
    // In production a missing URL means the bundle was built without its
    // environment, which should be loud at startup rather than a 404 later.
    throw new Error(`${name} is not set. The portal was built without its environment.`)
  }
  return fallback
}

function resolve(name: EndpointName): string {
  const endpoint = ENDPOINTS[name]
  // Indexed rather than destructured: Vite replaces `import.meta.env.VITE_X` by
  // textual substitution at build time, so the whole expression has to be written
  // out. A lookup on a variable key would be undefined in the built bundle.
  return required(ENV[endpoint.env], endpoint.env, endpoint.dev)
}

const ENV: Record<string, string | undefined> = {
  VITE_ISSUER: import.meta.env.VITE_ISSUER,
  VITE_ADMIN_API: import.meta.env.VITE_ADMIN_API,
  VITE_PARTICIPANT_API: import.meta.env.VITE_PARTICIPANT_API,
  VITE_CATCHER_API: import.meta.env.VITE_CATCHER_API,
  VITE_QUERY_API: import.meta.env.VITE_QUERY_API,
  VITE_DOCUMENTS_API: import.meta.env.VITE_DOCUMENTS_API,
}

export const config = {
  issuer: resolve('issuer'),
  adminApi: resolve('adminApi'),
  participantApi: resolve('participantApi'),
  catcherApi: resolve('catcherApi'),
  queryApi: resolve('queryApi'),
  documentsApi: resolve('documentsApi'),
} as const
