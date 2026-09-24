/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'

// The user app (map). Dev server proxies API + SignalR to the .NET Api (see src/Puluj.Api/Properties/launchSettings.json).
// The admin panel is a second entry with its own config: vite.admin.config.ts → Puluj.Admin.
export default defineConfig({
  // Public and admin dev servers run together; their dependency graphs must not overwrite each other.
  cacheDir: 'node_modules/.vite-public',
  plugins: [react(), tailwindcss()],
  // maplibre-gl v6 loads its worker via new URL('./maplibre-gl-worker.mjs', import.meta.url);
  // pre-bundling would strip that file, so the package is served as-is.
  optimizeDeps: { exclude: ['maplibre-gl'] },
  server: {
    port: 5193,
    // Playwright writes traces and screenshots under the project while tests run; watching them crashes the dev server (EBUSY on Windows).
    watch: { ignored: ['**/test-results/**', '**/e2e-report/**', '**/.*audit*results*/**', '**/.public-*/**'] },
    proxy: {
      '/api': { target: process.env.PULUJ_PUBLIC_API_PROXY ?? 'http://localhost:5277', changeOrigin: true },
      '/hubs': { target: 'http://localhost:5277', changeOrigin: true, ws: true },
    },
  },
  // Built straight into the API's static folder, so `dotnet run` serves the SPA without extra copying.
  build: { outDir: '../src/Puluj.EntityApi/wwwroot', emptyOutDir: true, sourcemap: true },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
})
