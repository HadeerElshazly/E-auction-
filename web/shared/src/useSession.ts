import { useCallback, useEffect, useState } from 'react'
import {
  completeLogin,
  login,
  logout,
  silentLoginWasRefused,
  type AuthConfig,
  type Session,
} from './auth'

export interface SessionState {
  session: Session | null
  loading: boolean
  error: string | null
  signIn: () => void
  signOut: () => void
}

/**
 * The access token is kept in React state and nowhere else — not localStorage, not
 * sessionStorage, not a cookie. A token in storage outlives the tab and is readable
 * by any script that gets onto the page, and in this system a bidder's token can
 * commit money while a committee member's can award land.
 *
 * The cost of that is a page refresh losing the token, which would otherwise dump
 * the user back on a login button while Keycloak still held their session. So on a
 * cold load with no token this tries `prompt=none` once: Keycloak answers from its
 * own session with a code and no interaction, or says `login_required` and the
 * button is shown. The attempt is marked in sessionStorage so a refusal cannot turn
 * into a redirect loop.
 */
const SILENT_TRIED = 'eauction.silent.tried'

export function useSession(config: AuthConfig): SessionState {
  const [session, setSession] = useState<Session | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    const run = async () => {
      try {
        const resumed = await completeLogin(config)

        if (cancelled) return

        if (resumed) {
          sessionStorage.removeItem(SILENT_TRIED)
          setSession(resumed)
          setLoading(false)
          return
        }

        // Nothing to resume. Try the identity provider's own session once.
        const refused = silentLoginWasRefused()
        const alreadyTried = sessionStorage.getItem(SILENT_TRIED) === 'yes'

        if (!refused && !alreadyTried) {
          sessionStorage.setItem(SILENT_TRIED, 'yes')
          await login(config, { silent: true })
          return // the browser is navigating away
        }

        setLoading(false)
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : String(e))
          setLoading(false)
        }
      }
    }

    void run()

    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // The realm issues 15-minute tokens and the services reject an expired one, so
  // drop the session at expiry rather than letting the user walk into a wall of 401s.
  useEffect(() => {
    if (!session) return
    const ms = session.expiresAt - Date.now()
    if (ms <= 0) {
      setSession(null)
      return
    }
    const timer = window.setTimeout(() => setSession(null), ms)
    return () => window.clearTimeout(timer)
  }, [session])

  const signIn = useCallback(() => {
    // An explicit click clears the silent marker: the user is asking to be shown
    // the form, whatever Keycloak said a moment ago.
    sessionStorage.removeItem(SILENT_TRIED)
    void login(config)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const signOut = useCallback(() => {
    // Set, not cleared: after signing out the next load must not immediately sign
    // the user back in from Keycloak's session.
    sessionStorage.setItem(SILENT_TRIED, 'yes')
    setSession(null)
    void logout(config)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return { session, loading, error, signIn, signOut }
}
