import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig, type Plugin } from 'vite'

/** Dev server: `/` opens the admin entry (admin.html), the same way the built page is the default file on the Admin service. */
function adminRoot(): Plugin {
  return {
    name: 'puluj-admin-root',
    configureServer(server) {
      server.middlewares.use((req, _res, next) => {
        if (req.url === '/' || req.url === '/index.html') req.url = '/admin.html'
        next()
      })
    },
  }
}

// Second entry of the same code base: the admin panel. Served by Puluj.Admin (port 5268), talks only to /api/admin/*.
// Shares components, styles and the api/ clients with the user app (vite.config.ts).
export default defineConfig({
  // Public and admin dev servers run together; their dependency graphs must not overwrite each other.
  cacheDir: 'node_modules/.vite-admin',
  plugins: [react(), tailwindcss(), adminRoot()],
  optimizeDeps: { exclude: ['maplibre-gl'] },
  server: {
    port: 5194,
    // Playwright writes traces and screenshots under the project while tests run; watching them crashes the dev server (EBUSY on Windows).
    watch: { ignored: ['**/test-results/**', '**/e2e-report/**', '**/.*audit*results*/**', '**/.public-*/**'] },
    proxy: {
      '/api': { target: 'http://localhost:5278', changeOrigin: true },
    },
  },
  build: {
    outDir: '../src/Puluj.EntityAdmin/wwwroot',
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: { input: 'admin.html' },
  },
})
