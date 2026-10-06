/**
 * Where the portals talk to, in one place.
 *
 * Both the runtime config and the build-time Content-Security-Policy are derived
 * from this table. They have to agree exactly: a `connect-src` that is missing an
 * origin the portal calls does not fail at build, it fails in a browser, at the
 * moment a bidder presses the bid button. Two lists maintained by hand would drift
 * on the first service that moved.
 *
 * Every value here is a public URL. Nothing secret belongs in a browser bundle —
 * the Keycloak clients are public clients with PKCE for exactly that reason.
 */
export interface Endpoint {
  /** The Vite variable that supplies it, inlined at build time. */
  readonly env: string
  /** What a developer gets when the variable is not set. Never used in a build. */
  readonly dev: string
}

export const ENDPOINTS = {
  issuer: { env: 'VITE_ISSUER', dev: 'http://localhost:8080/realms/eauction' },
  adminApi: { env: 'VITE_ADMIN_API', dev: 'http://localhost:5101' },
  participantApi: { env: 'VITE_PARTICIPANT_API', dev: 'http://localhost:5102' },
  catcherApi: { env: 'VITE_CATCHER_API', dev: 'http://localhost:5103' },
  queryApi: { env: 'VITE_QUERY_API', dev: 'http://localhost:5105' },
  documentsApi: { env: 'VITE_DOCUMENTS_API', dev: 'http://localhost:5107' },
} as const satisfies Record<string, Endpoint>

export type EndpointName = keyof typeof ENDPOINTS
