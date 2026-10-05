import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import { contentSecurityPolicy } from '../shared/vite-csp'

export default defineConfig(({ mode }) => ({
  // The policy's connect-src is built from the same variables the bundle inlines,
  // so it permits exactly the origins this build will call and nothing else.
  plugins: [react(), contentSecurityPolicy({ env: loadEnv(mode, process.cwd(), 'VITE_') })],
  server: {
    port: 3001,
    // Fail rather than drift to another port: the port is registered as a redirect
    // URI on the Keycloak client, so a silent fallback to :3002 turns every
    // login into "Invalid parameter: redirect_uri".
    strictPort: true,
  },
  build: { outDir: 'dist', sourcemap: true },
}))
