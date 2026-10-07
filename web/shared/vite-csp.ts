/**
 * The Content-Security-Policy the portals ship with.
 *
 * `bidder/src/useSigningKey.ts` holds the bidder's bid-signing key in the tab's
 * heap, and the comment on it names a strict CSP as the mitigation. This is that
 * mitigation. Without it the argument for the bidder holding their own key rests on
 * nothing: any injected script could read the key, sign a bid, or simply post the
 * key to an attacker's host, and nothing in the page would stop it.
 *
 * Two directives do the real work:
 *
 * - **`script-src 'self'`**, with no `'unsafe-inline'` and no `'unsafe-eval'`.
 *   Injected `<script>` content and inline event handlers do not run, which is the
 *   whole class of attack that reaches the key.
 * - **`connect-src`**, enumerated from the same table the portal's runtime config
 *   reads. Even a script that somehow ran could not post what it stole anywhere but
 *   the services this portal already talks to.
 *
 * `style-src` keeps `'unsafe-inline'`, deliberately. Both portals use React `style`
 * attributes throughout, which that directive governs, and the exchange — rewriting
 * them all for a marginal CSS-exfiltration gain, against the loss of the directive
 * that actually matters — is not worth making. A concession on styles is not a
 * concession on scripts.
 *
 * Delivered as a `<meta>` element because the portals are static bundles and the
 * server in front of them is not ours to configure. Two consequences, both real:
 * `frame-ancestors` and `report-uri` are **ignored** in a meta element, so
 * clickjacking protection still needs a response header from whatever serves
 * `dist/` — see `web/README.md`. And the element has to come before anything it
 * governs, so it is injected at the top of `<head>`.
 *
 * Build-only. The dev server's React Refresh preamble is an inline script, so a
 * policy strict enough to be worth shipping would stop `npm run dev` working. That
 * means the browser walk-through, which drives the dev server, does not exercise
 * this — `csp.test.ts` tests the policy and the injection directly instead.
 */
import { ENDPOINTS } from './src/endpoints.ts'

export interface CspOptions {
  /**
   * The build's environment, as Vite's `loadEnv` returns it. Origins are taken
   * from it so the policy permits exactly what the bundle was built to call.
   */
  env: Record<string, string | undefined>
}

/** The origin of a URL, or null if it is not one. */
export function originOf(url: string | undefined): string | null {
  if (!url) return null
  try {
    return new URL(url).origin
  } catch {
    return null
  }
}

/**
 * The origins this bundle may talk to: every configured endpoint, plus the page's
 * own. Deduplicated, because the four services often sit behind one gateway and a
 * policy repeating the same origin five times is harder to read than it is wrong.
 */
export function connectOrigins(env: Record<string, string | undefined>): string[] {
  const origins = new Set<string>(["'self'"])

  for (const endpoint of Object.values(ENDPOINTS)) {
    const origin = originOf(env[endpoint.env] ?? endpoint.dev)
    if (origin) origins.add(origin)
  }

  return [...origins]
}

export function buildPolicy(env: Record<string, string | undefined>): string {
  return [
    // Everything not named below falls here, so a directive forgotten in future is
    // forgotten closed rather than open.
    "default-src 'none'",

    "script-src 'self'",
    "style-src 'self' 'unsafe-inline'",

    // data: for the inline SVG favicon, which exists so there is no request to 404.
    // The document service for cover images, which are Public documents served
    // from it — that one origin, not every API the page talks to.
    `img-src ${["'self'", 'data:', originOf(env[ENDPOINTS.documentsApi.env] ?? ENDPOINTS.documentsApi.dev)]
      .filter(Boolean)
      .join(' ')}`,
    "font-src 'self'",

    `connect-src ${connectOrigins(env).join(' ')}`,

    // Nothing is embedded and nothing embeds anything. object-src in particular is
    // the classic script-src bypass.
    "object-src 'none'",
    "frame-src 'none'",

    // Without this an injected <base> can point every relative script URL at
    // another host while script-src 'self' still passes.
    "base-uri 'none'",

    // Nothing in either portal submits a form; saying so stops an injected one
    // posting a stolen key somewhere connect-src would have refused.
    "form-action 'none'",
  ].join('; ')
}

/** Vite plugin shape, structurally typed so this file needs no vite import. */
interface VitePluginLike {
  name: string
  apply: 'build'
  transformIndexHtml: {
    order: 'pre'
    handler: (html: string) => string
  }
}

export function contentSecurityPolicy(options: CspOptions): VitePluginLike {
  const policy = buildPolicy(options.env)

  return {
    name: 'eauction-csp',
    apply: 'build',
    transformIndexHtml: {
      order: 'pre',
      handler: (html: string) => injectPolicy(html, policy),
    },
  }
}

/**
 * Puts the policy first inside `<head>`.
 *
 * A meta policy governs only what the parser meets after it, so injecting it
 * anywhere later would leave whatever precedes it unprotected — and the favicon
 * link and the title are already up there.
 */
export function injectPolicy(html: string, policy: string): string {
  const meta = `<meta http-equiv="Content-Security-Policy" content="${policy}" />`
  const head = html.match(/<head[^>]*>/i)

  if (!head) {
    throw new Error('No <head> to put the Content-Security-Policy in.')
  }

  return html.replace(head[0], `${head[0]}\n    ${meta}`)
}
