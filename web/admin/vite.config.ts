import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3001,
    // Fail rather than drift to another port: the port is registered as a redirect
    // URI on the Keycloak client, so a silent fallback to :3002 turns every
    // login into "Invalid parameter: redirect_uri".
    strictPort: true,
  },
  build: { outDir: 'dist', sourcemap: true },
})
