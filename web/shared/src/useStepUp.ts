import { useCallback } from 'react'
import { login, rememberPendingAction, type AuthConfig } from './auth'
import { ApiError } from './api'

/**
 * Turns a step-up challenge into a confirmation the user can actually complete.
 *
 * Without this a bidder who tries to pay a deposit gets a 403 and a dead end: the
 * service is asking them to confirm who they are, and nothing in the portal offers
 * them the chance. So a challenged action sends them to the identity provider with
 * the level the service named, and the label of what they were doing is remembered
 * across the redirect so they land back on it rather than on the catalogue.
 *
 * `prompt=login` matters more than it looks. Keycloak treats a level as reached for
 * the life of the session, so a user whose confirmation has gone *stale* would be
 * handed the same stale `auth_time` straight back — the gate would refuse again, the
 * portal would redirect again, and the user would be in a loop with no way out. For
 * a stale challenge the identity provider has to be told to authenticate afresh.
 */
export interface StepUpRunner {
  /**
   * Runs the action. If the service demands a confirmation, redirects to get one
   * and never returns; otherwise returns the action's result.
   */
  run: <T>(label: string, action: () => Promise<T>) => Promise<T>
}

export function useStepUp(config: AuthConfig): StepUpRunner {
  const run = useCallback(async <T,>(label: string, action: () => Promise<T>): Promise<T> => {
    try {
      return await action()
    } catch (error) {
      if (!(error instanceof ApiError) || !error.stepUp) throw error

      rememberPendingAction(label)

      // Does not return: the browser navigates to the identity provider.
      await login(config, {
        acrValues: error.stepUp.requiredAcr,
        reauthenticate: error.stepUp.stale,
      })

      throw error
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return { run }
}
