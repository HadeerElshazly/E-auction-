/**
 * OIDC authorization code flow with PKCE, against Keycloak.
 *
 * Written out rather than pulled from a library on purpose. This is the code that
 * decides who the user is and where their access token lives, it is about 150 lines,
 * and in a system where a token can commit the state to selling land it should be
 * code a reviewer can read in full.
 *
 * Both clients are public (`deploy/keycloak/eauction-realm.json`): a browser cannot
 * keep a secret, so PKCE is what stops an intercepted authorization code from being
 * redeemed by anyone but this tab.
 */

export interface AuthConfig {
  /** e.g. http://localhost:8080/realms/eauction */
  issuer: string
  clientId: string
  /** Must match a redirect URI registered on the client. */
  redirectUri: string
  scope?: string
}

export interface Session {
  accessToken: string
  /** The `sub` claim. This IS the bidder id and the signing-key input (D-18). */
  subject: string
  /** Realm roles, which Keycloak nests under realm_access.roles. */
  roles: string[]
  name: string
  nationalId?: string
  nameAr?: string
  expiresAt: number
}

interface Discovery {
  authorization_endpoint: string
  token_endpoint: string
  end_session_endpoint?: string
}

// sessionStorage, not localStorage: per-tab, and gone when the tab closes.
// The verifier is a one-time secret for a single login, so it should not outlive
// the redirect that uses it, and it must not be shared with another tab mid-flow.
const VERIFIER_KEY = 'eauction.pkce.verifier'
const STATE_KEY = 'eauction.pkce.state'
const RETURN_KEY = 'eauction.return'

let discovery: Discovery | null = null

async function discover(issuer: string): Promise<Discovery> {
  if (discovery) return discovery
  const response = await fetch(`${issuer}/.well-known/openid-configuration`)
  if (!response.ok) {
    throw new Error(
      `The identity provider at ${issuer} did not answer (${response.status}). ` +
        'Is Keycloak running with the eauction realm imported?',
    )
  }
  discovery = (await response.json()) as Discovery
  return discovery
}

function randomUrlSafe(bytes: number): string {
  const raw = crypto.getRandomValues(new Uint8Array(bytes))
  return base64Url(raw)
}

function base64Url(bytes: Uint8Array): string {
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

async function challengeFor(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))
  return base64Url(new Uint8Array(digest))
}

export interface LoginOptions {
  /**
   * `prompt=none` asks Keycloak to answer from an existing session without showing
   * anything. It comes back either with a code, or with `error=login_required`.
   * This is what makes a page refresh survivable while keeping the access token out
   * of any storage: the token is gone, but the identity provider's session is not.
   */
  silent?: boolean
}

/** Sends the browser to Keycloak. Does not return. */
export async function login(config: AuthConfig, options: LoginOptions = {}): Promise<never> {
  const { authorization_endpoint } = await discover(config.issuer)

  const verifier = randomUrlSafe(32)
  const state = randomUrlSafe(16)
  sessionStorage.setItem(VERIFIER_KEY, verifier)
  sessionStorage.setItem(STATE_KEY, state)
  sessionStorage.setItem(RETURN_KEY, location.pathname + location.search)

  const url = new URL(authorization_endpoint)
  url.searchParams.set('client_id', config.clientId)
  url.searchParams.set('redirect_uri', config.redirectUri)
  url.searchParams.set('response_type', 'code')
  url.searchParams.set('scope', config.scope ?? 'openid profile email')
  url.searchParams.set('state', state)
  url.searchParams.set('code_challenge', await challengeFor(verifier))
  url.searchParams.set('code_challenge_method', 'S256')
  if (options.silent) url.searchParams.set('prompt', 'none')

  location.assign(url.toString())
  return new Promise<never>(() => {})
}

/**
 * One exchange per authorization code, however many callers ask for it.
 *
 * An authorization code is single-use and the PKCE verifier is consumed with it, so
 * a second attempt on the same code fails twice over: Keycloak answers
 * `invalid_grant`, and this module has already cleared the verifier. Two callers is
 * not hypothetical — React StrictMode runs every effect twice in development, which
 * is exactly how this was found, and a remount would do the same in production.
 * Sharing the promise makes a repeat call return the first result instead of
 * destroying it.
 */
let exchange: { code: string; promise: Promise<Session | null> } | null = null

/**
 * Forgets the in-flight exchange and the cached discovery document.
 *
 * For tests only: each case needs a clean module, and a query-string import to
 * defeat the module cache is not something tsc can resolve.
 */
export function resetAuthStateForTests(): void {
  exchange = null
  discovery = null
}

/**
 * Completes the flow if this load is a redirect back from Keycloak.
 * Returns null when there is no code in the URL, so a normal page load is cheap.
 */
export function completeLogin(config: AuthConfig): Promise<Session | null> {
  const code = new URLSearchParams(location.search).get('code')

  if (code && exchange?.code === code) return exchange.promise

  const promise = redeem(config)
  if (code) exchange = { code, promise }
  return promise
}

async function redeem(config: AuthConfig): Promise<Session | null> {
  const params = new URLSearchParams(location.search)
  const code = params.get('code')
  const error = params.get('error')

  if (error) {
    cleanUrl()

    // Keycloak's answer to prompt=none when it has no session for this browser.
    // Expected, not a failure: it means "ask the user to sign in".
    if (error === 'login_required' || error === 'interaction_required') return null

    throw new Error(`${error}: ${params.get('error_description') ?? 'no description'}`)
  }
  if (!code) return null

  const expected = sessionStorage.getItem(STATE_KEY)
  const verifier = sessionStorage.getItem(VERIFIER_KEY)
  sessionStorage.removeItem(STATE_KEY)
  sessionStorage.removeItem(VERIFIER_KEY)

  // Without this check a third party could hand the user a link carrying their own
  // authorization code and have this tab sign in as them.
  if (!expected || params.get('state') !== expected) {
    cleanUrl()
    throw new Error('The login response did not match this tab. Start again.')
  }
  if (!verifier) {
    cleanUrl()
    throw new Error('This tab has no PKCE verifier for that code. Start again.')
  }

  const { token_endpoint } = await discover(config.issuer)
  const response = await fetch(token_endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code',
      client_id: config.clientId,
      redirect_uri: config.redirectUri,
      code,
      code_verifier: verifier,
    }),
  })

  const body = await response.text()
  if (!response.ok) throw new Error(`Token exchange failed (${response.status}): ${body}`)

  const returnTo = sessionStorage.getItem(RETURN_KEY) ?? '/'
  sessionStorage.removeItem(RETURN_KEY)
  history.replaceState({}, '', returnTo)

  return sessionFrom(JSON.parse(body).access_token as string)
}

function cleanUrl() {
  history.replaceState({}, '', location.pathname)
}

/**
 * Reads the claims this application uses. The signature is NOT verified here —
 * a browser verifying its own token proves nothing, since anything that could forge
 * the token could also patch this function. The services verify it offline against
 * the realm's JWKS, and that is the check that matters.
 */
export function sessionFrom(accessToken: string): Session {
  const [, payload] = accessToken.split('.')
  if (!payload) throw new Error('That is not a JWT.')

  const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
  const claims = JSON.parse(decodeURIComponent(escape(json))) as {
    sub: string
    name?: string
    preferred_username?: string
    national_id?: string
    name_ar?: string
    exp: number
    realm_access?: { roles?: string[] }
  }

  return {
    accessToken,
    subject: claims.sub,
    roles: claims.realm_access?.roles ?? [],
    name: claims.name ?? claims.preferred_username ?? claims.sub,
    nationalId: claims.national_id,
    nameAr: claims.name_ar,
    expiresAt: claims.exp * 1000,
  }
}

export async function logout(config: AuthConfig): Promise<void> {
  const d = await discover(config.issuer)
  if (!d.end_session_endpoint) {
    location.assign(config.redirectUri)
    return
  }
  const url = new URL(d.end_session_endpoint)
  url.searchParams.set('client_id', config.clientId)
  url.searchParams.set('post_logout_redirect_uri', config.redirectUri)
  location.assign(url.toString())
}

/** True when this load is Keycloak answering prompt=none with "no session". */
export function silentLoginWasRefused(): boolean {
  const error = new URLSearchParams(location.search).get('error')
  return error === 'login_required' || error === 'interaction_required'
}

export const Roles = {
  bidder: 'bidder',
  auctionAdmin: 'auction-admin',
  awardCommittee: 'award-committee',
  operator: 'operator',
} as const

export function has(session: Session | null, role: string): boolean {
  return session?.roles.includes(role) ?? false
}
