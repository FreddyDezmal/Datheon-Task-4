# Storefront — Final Frontend Deployment to Render

The Storefront backend is already deployed to Render and connected to PostgreSQL. The reviewed database migration has been applied, production health checks work, rate-limit configuration has been corrected, checkout idempotency has been verified, and backend deployment smoke tests passed (57 passed, 0 failed, 2 expected skips).

The **frontend is the only remaining deployment task**.

Frontend stack: React 19, TypeScript, Vite. Backend: ASP.NET Core .NET 10. Repository is already on GitHub.

## Objective

Prepare, deploy, and verify the frontend as a Render Static Site, connected securely to the existing deployed API.

## Phase 1 — Audit before changing code

1. Inspect the existing frontend project, `package.json`, Vite configuration, API client, routing, authentication/session handling, cart persistence, and frontend tests.
2. Inspect the existing deployment documentation and current backend configuration.
3. Identify the exact production API URL from the existing configuration or documented deployment details. Do not invent a URL.
4. Check how `VITE_API_BASE_URL` is consumed and whether any API calls bypass the centralized API client.
5. Check for hardcoded localhost URLs, insecure HTTP URLs, environment assumptions, build-time secrets, and routes that may fail on refresh.
6. Verify that the frontend build output is `dist`, or document any justified difference.

## Phase 2 — Prepare the frontend

1. Configure the frontend to use `VITE_API_BASE_URL` for the deployed API without breaking local development.
2. Ensure the API base URL is read correctly at build time. Do not put secrets in Vite environment variables because frontend variables are exposed to browser users.
3. Verify production API error handling, loading states, authentication, cart behavior, and checkout's existing idempotency and uncertain-outcome recovery logic.
4. Ensure the application displays useful errors if the API is temporarily unavailable and does not claim a checkout failed when its outcome is uncertain.
5. Configure client-side routing fallback for a Render Static Site, so refreshing a valid nested route does not return a 404. Use Render's appropriate rewrite configuration rather than adding unnecessary server infrastructure.
6. Check accessibility, responsive layouts, browser console errors, and API CORS compatibility.
7. Do not weaken existing tests, remove features, or rewrite working architecture without a clear reason.

## Phase 3 — Render configuration

Prepare the exact Render Static Site configuration for this repository.

- Root directory: the actual frontend directory.
- Build command: use the lockfile and existing package manager; typically `npm ci && npm run build`.
- Publish directory: `dist`, if confirmed by the Vite configuration.
- Environment variable: `VITE_API_BASE_URL` set to the verified HTTPS URL of the deployed API.
- Configure the necessary SPA rewrite to serve `index.html` for client-side routes.
- Document the exact backend CORS setting that must contain the frontend's HTTPS origin: `Cors__AllowedOrigins__0`.
- Do not use a wildcard CORS origin with credentials.
- Do not change production settings or trigger deployment without authorization.

If the API URL or any required Render value cannot be established from repository evidence, ask me for it instead of guessing.

## Phase 4 — Verify

1. Run frontend unit and component tests, type checking, linting if configured, production build, and existing coverage thresholds.
2. Run existing Playwright/browser tests where available.
3. Report any test failures and distinguish pre-existing issues from changes introduced by this work.
4. Provide a concise manual verification checklist for the deployed site:
   - Home page loads over HTTPS.
   - Refreshing nested routes works.
   - Browser requests go to the deployed API, not localhost.
   - CORS permits the actual frontend origin and rejects unapproved origins.
   - Sign-in, session persistence, and sign-out work.
   - Product browsing and cart persistence work.
   - Successful checkout creates one order.
   - Retrying an uncertain checkout does not create a duplicate order.
   - API outage and recovery states are handled correctly.
   - No unexpected browser console errors or exposed secrets appear.

## Phase 5 — Documentation and final report

Update the existing deployment documentation with the actual frontend build settings, environment variable names (never their secret values), routing rewrite, CORS configuration, deployment steps, and verification results.

Report:
- Files changed.
- Commands and tests run, with exact results.
- Render settings I need to enter manually.
- Any remaining blockers.
- A safe order for deploying the static site, setting its exact origin in backend CORS, and verifying the live application.

Do not commit, push, change production environment settings, or deploy without my explicit approval. Make the smallest safe changes and preserve the existing working backend.