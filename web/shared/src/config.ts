/**
 * Service endpoints, from the environment at build time.
 *
 * Vite inlines `import.meta.env` at build, so these are baked into the bundle. That
 * is correct for URLs and a client id — all of which are public by nature — and is
 * exactly why no secret belongs here. The Keycloak clients are public clients with
 * PKCE precisely because a browser bundle cannot keep one.
 */

function required(value: string | undefined, name: string, fallback: string): string {
  if (value && value.length > 0) return value
  if (import.meta.env.PROD) {
    // In production a missing URL means the bundle was built without its
    // environment, which should be loud at startup rather than a 404 later.
    throw new Error(`${name} is not set. The portal was built without its environment.`)
  }
  return fallback
}

export const config = {
  issuer: required(
    import.meta.env.VITE_ISSUER,
    'VITE_ISSUER',
    'http://localhost:8080/realms/eauction',
  ),
  adminApi: required(import.meta.env.VITE_ADMIN_API, 'VITE_ADMIN_API', 'http://localhost:5101'),
  participantApi: required(
    import.meta.env.VITE_PARTICIPANT_API,
    'VITE_PARTICIPANT_API',
    'http://localhost:5102',
  ),
  catcherApi: required(import.meta.env.VITE_CATCHER_API, 'VITE_CATCHER_API', 'http://localhost:5103'),
  queryApi: required(import.meta.env.VITE_QUERY_API, 'VITE_QUERY_API', 'http://localhost:5105'),
} as const
