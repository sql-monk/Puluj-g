import { defineConfig, devices } from '@playwright/test'

const actualBaseUrl = process.env.E13_ACTUAL_BASE_URL

/**
 * UI E2E normally runs against a fully mocked API.  The U13 acceptance runner sets E13_ACTUAL_BASE_URL instead:
 * it serves the built SPA from the real .NET API and starts no Vite development server.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: /.*\.e2e\.ts/,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1, // one WebGL map at a time against one dev server: parallel workers starve each other into timeouts
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: process.env.PLAYWRIGHT_HTML_OUTPUT_DIR ?? 'e2e-report' }]],
  use: {
    baseURL: actualBaseUrl ?? 'http://localhost:5183',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: actualBaseUrl ? undefined : [
    { command: 'npx vite --port 5183 --strictPort', url: 'http://localhost:5183', reuseExistingServer: !process.env.CI, timeout: 60_000 },
    // The admin panel (its own entry/config): the A-scenarios open it on :5184.
    { command: 'npx vite --config vite.admin.config.ts --port 5184 --strictPort', url: 'http://localhost:5184/admin.html', reuseExistingServer: !process.env.CI, timeout: 60_000 },
  ],
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 415, height: 900 } }, testMatch: /(?:Public.*|A13-admin-audit)\.e2e\.ts/ },
  ],
})
