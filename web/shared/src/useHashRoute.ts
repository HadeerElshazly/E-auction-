import { useCallback, useEffect, useState } from 'react'

/**
 * The page a portal is on, in the address: `#auction/<id>`, `#applications`.
 *
 * In the address rather than in component state, so the browser's back button
 * goes back a page, a link can be shared or reopened, and a refresh keeps the
 * person where they were — none of which held while each screen was a boolean.
 */
export function useHashRoute(fallback: string): [string[], (to: string) => void] {
  const read = () => (window.location.hash.replace(/^#\/?/, '') || fallback).split('/').filter(Boolean)
  const [parts, setParts] = useState<string[]>(read)

  useEffect(() => {
    const onChange = () => {
      setParts(read())
      window.scrollTo({ top: 0 })
    }
    window.addEventListener('hashchange', onChange)
    // Once more now that it listens: the sign-in check may have put the address
    // back (#auction/<id>) between the first render and this effect.
    setParts(read())
    return () => window.removeEventListener('hashchange', onChange)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const navigate = useCallback((to: string) => {
    const next = `#${to}`
    if (window.location.hash === next) setParts(read())
    else window.location.hash = next
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return [parts, navigate]
}
