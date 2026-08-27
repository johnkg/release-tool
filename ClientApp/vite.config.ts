import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Same-origin in production (the SPA is served from wwwroot), so the dev
      // server has to stand in for that. Target must match the API's
      // launchSettings applicationUrl.
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: false,
      },
    },
  },
  build: {
    // Published into the API's wwwroot by the BuildSpa target in Phase 8.
    outDir: 'dist',
    emptyOutDir: true,
  },
})
