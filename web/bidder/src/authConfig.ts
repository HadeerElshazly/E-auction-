import { config } from '@eauction/shared'

/**
 * Shared so the step-up runner and the session hook ask the same identity provider
 * for the same client. Two copies that drifted would mean a confirmation obtained
 * for one client id and checked against another.
 */
export const authConfig = {
  issuer: config.issuer,
  clientId: 'bidder-web',
  redirectUri: window.location.origin + '/',
  // national_id and name_ar ride on the realm's default scopes; see
  // deploy/keycloak/README.md.
  scope: 'openid profile email',
}
