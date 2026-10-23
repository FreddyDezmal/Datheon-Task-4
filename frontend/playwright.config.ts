import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';
import { PORTS } from './e2e/support/ports';

/*
 * Browser journeys against the real stack, started fresh for every run:
 *   - tools/fakestore_mock.py on :9101  (stand-in for FakeStoreAPI, so tests never need the internet)
 *   - the ASP.NET Core API on :5081      (Development, its own throwaway SQLite file under e2e/.data)
 *   - the Vite dev server on :5174       (pointed at that API)
 * Separate ports from `npm run dev` / `dotnet run` (5173 / 5080), so your own servers and database
 * are never touched. See Documentation/Testing.md.
 */

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..');
const dataDir = path.join(here, 'e2e', '.data');
const databasePath = path.join(dataDir, 'e2e.db');

// Start every run with an empty database. The config is also loaded by each worker; only the main
// process (no TEST_WORKER_INDEX) resets it, before the servers start.
if (!process.env.TEST_WORKER_INDEX) {
  fs.rmSync(dataDir, { recursive: true, force: true });
  fs.mkdirSync(dataDir, { recursive: true });
}

const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');

export default defineConfig({
  testDir: './e2e',
  outputDir: './e2e/.results',
  // The journeys share one API and database, and some switch the mock into failure modes,
  // so they run one at a time. The whole suite takes about a minute.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }], ['junit', { outputFile: 'e2e/.results/junit.xml' }]],
  use: {
    baseURL: `http://localhost:${PORTS.app}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 860 },
    // Set PW_CHROMIUM_PATH to use an already-installed Chromium instead of `npx playwright install`.
    ...(process.env.PW_CHROMIUM_PATH ? { launchOptions: { executablePath: process.env.PW_CHROMIUM_PATH } } : {}),
  },
  webServer: [
    {
      name: 'FakeStore mock',
      command: `${python} tools/fakestore_mock.py ${PORTS.mock}`,
      cwd: repo,
      url: `http://127.0.0.1:${PORTS.mock}/products`, // the mock listens on IPv4 only
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      name: 'API',
      // Override with E2E_API_COMMAND, e.g. to add --no-restore when packages come from an offline feed.
      command: process.env.E2E_API_COMMAND ?? 'dotnet run --project backend/Storefront.Api --no-launch-profile',
      cwd: repo,
      url: `http://localhost:${PORTS.api}/health`,
      reuseExistingServer: false,
      timeout: 240_000, // the first run compiles the API
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: `http://localhost:${PORTS.api}`,
        ConnectionStrings__Storefront: `Data Source=${databasePath}`,
        FakeStore__BaseUrl: `http://127.0.0.1:${PORTS.mock}/`,
        FakeStore__CatalogueCacheSeconds: '0', // so switching the mock into an outage takes effect at once
        Cors__AllowedOrigins__0: `http://localhost:${PORTS.app}`,
        RateLimiting__AccountPermitLimit: '1000',
        RateLimiting__CheckoutPermitLimit: '1000',
      },
    },
    {
      name: 'Frontend',
      command: `npx vite --port ${PORTS.app} --strictPort`,
      cwd: here,
      url: `http://localhost:${PORTS.app}`,
      reuseExistingServer: false,
      timeout: 60_000,
      env: { VITE_API_BASE_URL: `http://localhost:${PORTS.api}` },
    },
  ],
});
