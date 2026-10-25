/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import { apiBaseUrlProblem } from './src/config/apiBaseUrl.ts';
import { originOf, securityHeaders } from './src/config/securityHeaders.ts';

export default defineConfig(({ command, mode }) => {
  // `vite build` stops on an unusable VITE_API_BASE_URL (src/config/apiBaseUrl.ts). On Render (which
  // sets RENDER during builds) it must also be present; a local build may leave it unset (same origin).
  const env = loadEnv(mode, process.cwd(), 'VITE_');
  if (command === 'build') {
    const problem = apiBaseUrlProblem(env.VITE_API_BASE_URL, { required: Boolean(process.env.RENDER) });
    if (problem) throw new Error(problem);
  }

  // `vite preview` serves the production bundle with the same headers the Render Static Site sets, so
  // the browser tests check the bundle under that Content-Security-Policy. CSP_IMAGE_ORIGINS (not a
  // VITE_ variable, so it never reaches the bundle) lists the image hosts; FakeStoreAPI by default.
  const previewHeaders = securityHeaders({
    api: originOf(env.VITE_API_BASE_URL) ?? "'self'",
    images: (process.env.CSP_IMAGE_ORIGINS ?? 'https://fakestoreapi.com').split(/\s+/).filter(Boolean),
  });

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true, // the backend's CORS policy allows exactly this origin
    },
    preview: {
      headers: previewHeaders,
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
      css: false,
      restoreMocks: true,
      unstubGlobals: true,
      // Two categories, runnable separately with `--project unit` or `--project component`:
      //  - unit: functions, services, hooks and context logic in isolation
      //  - component: components and whole shopper workflows rendered in jsdom, with fetch replaced
      //    by the fake API in src/test/fixtures.ts (real response shapes, no network)
      // Browser journeys against the real API live in e2e/ and run with Playwright, not Vitest.
      projects: [
        {
          extends: true,
          test: { name: 'unit', include: ['src/{utils,services,context,hooks,config}/**/*.test.{ts,tsx}'] },
        },
        {
          extends: true,
          test: {
            name: 'component',
            include: ['src/components/**/*.test.tsx', 'src/App.test.tsx'],
            // Whole workflows with simulated typing: about 1–5 s each here, and roughly 2–3× slower on
            // some Windows machines (with coverage on). The 5 s default timed a two-checkout test out
            // there. A timeout is not an assertion: no test relies on it to detect loops (they use settle()).
            testTimeout: 20_000,
          },
        },
      ],
      coverage: {
        provider: 'v8',
        include: ['src/**/*.{ts,tsx}'],
        exclude: [
          'src/**/*.test.{ts,tsx}', // the tests themselves
          'src/test/**', // test setup and the fake API
          'src/main.tsx', // three lines that mount <App /> into index.html; exercised by the browser tests
          'src/vite-env.d.ts', // type declarations only
          'src/types/**', // type declarations only (no runtime code)
        ],
        reporter: ['text', 'text-summary', 'html', 'lcov', 'json-summary'],
        reportsDirectory: './coverage',
        // Set a little below the measured baseline (see Documentation/Testing.md): they catch a real
        // drop without failing the build over a one-line change. Raise them as coverage improves.
        thresholds: {
          statements: 93,
          branches: 88,
          functions: 92,
          lines: 95,
        },
      },
    },
  };
});
