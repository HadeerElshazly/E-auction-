import { defineConfig } from '@playwright/test'

/**
 * Browser walk-through of both portals, against the real stack.
 *
 * In the spirit of tools/smoke: nothing is mocked. A real Keycloak login form, real
 * services, real Kafka, and a bid whose frame is built and signed by the browser's
 * own Web Crypto. The point is to catch what neither the unit tests nor the API
 * smoke test can see — a CORS header that is missing, a redirect URI that does not
 * match, a claim the portal reads under the wrong name, a frame the browser builds
 * differently from the server.
 *
 * Start the stack and both dev servers first; tools/smoke/run-portals.sh does that.
 */
export default defineConfig({
  testDir: './tests',
  // One worker: the tests share one auction and drive it through its lifecycle, so
  // they are a sequence rather than a set.
  workers: 1,
  fullyParallel: false,
  // A whole auction has to open, run, close and be awarded inside this one test.
  //
  // The floor is set by the admin portal's datetime-local inputs, which have minute
  // precision: the shortest auction that can be scheduled through the UI opens a
  // minute away and runs for a minute. Add the processor's close grace, the query
  // BFF's poll interval and Keycloak logins for four actors, and four minutes of
  // wall clock is the real cost. Eight gives it room on a loaded machine without
  // hiding a genuine hang.
  timeout: 480_000,
  expect: { timeout: 20_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env.BIDDER_URL ?? 'http://localhost:3000',
    // The container has no display, and the proxy does not serve localhost.
    headless: true,
    ignoreHTTPSErrors: true,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    launchOptions: {
      // Set by the environment; named here so a failure says which browser was missing.
      executablePath: process.env.CHROMIUM_PATH || undefined,
      args: ['--no-sandbox', '--disable-dev-shm-usage'],
    },
  },
  outputDir: '/tmp/eauction-e2e',
})
