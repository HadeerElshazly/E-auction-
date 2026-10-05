import { config } from '@eauction/shared'

/**
 * Shared so the step-up runner and the session hook ask the same identity provider
 * for the same client. Two copies that drifted would mean a confirmation obtained
 * for one client id and checked against another.
 */
export const authConfig = {
  issuer: config.issuer,
  clientId: 'admin-web',
  redirectUri: window.location.origin + '/',
}
