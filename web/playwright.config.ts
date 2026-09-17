import { defineConfig, devices } from '@playwright/test'

/**
 * UI E2E (plan P12 D1, §16.1): the map app against a fully mocked API (web/e2e/fixtures/api.ts) — no .NET, no database.
 * `npm run e2e` starts the Vite dev server itself. Desktop and mobile projects; screenshots are DOM-only (popup/legend).
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
    baseURL: 'http://localhost:5183',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: [
    { command: 'npx vite --port 5183 --strictPort', url: 'http://localhost:5183', reuseExistingServer: !process.env.CI, timeout: 60_000 },
    // The admin panel (its own entry/config): the A-scenarios open it on :5184.
    { command: 'npx vite --config vite.admin.config.ts --port 5184 --strictPort', url: 'http://localhost:5184/admin.html', reuseExistingServer: !process.env.CI, timeout: 60_000 },
  ],
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, testMatch: /E(?:0[169]|10).*\.e2e\.ts/ },
  ],
})
