# Testing and quality assurance

How Storefront is tested:
- what each kind of test proves
- how to run each suite and produce coverage reports
- what the numbers were on 23 October 2026
- what still isn't tested

The step-by-step human checklist is in [`Testing/ManualTestPlan.md`](Testing/ManualTestPlan.md).

---

## 1. Kinds of tests

| Category | What it proves | Runs against | Tool | Location | Needs |
|---|---|---|---|---|---|
| **Frontend unit** | Pure functions, services, hooks and context logic: money, validation, parsers, the API client, sessions, idempotency keys, checkout outcome classification | Fake `fetch` and fake timers | Vitest | `frontend/src/{utils,services,context,hooks}/**/*.test.ts(x)` | Nothing |
| **Frontend component and workflow** | What a shopper sees and does, from one component up to whole flows (sign in, checkout, order history) in the full app | jsdom plus the fake API in `src/test/fixtures.ts` (real response shapes, no network) | Vitest + Testing Library | `frontend/src/components/**/*.test.tsx`, `src/App.test.tsx` | Nothing |
| **Backend unit** | Pricing, validation, category rules, the FakeStore client, error-to-ProblemDetails mapping | In-memory objects; a stub HTTP handler | xUnit | `[Trait("Category","Unit")]` | Nothing |
| **Backend database** | Checkout and order queries against a real SQLite database built by the real migrations: constraints, transactions, unique indexes, idempotency races | SQLite in memory or a temp file | xUnit | `[Trait("Category","Database")]` | Nothing |
| **Backend API** | The whole ASP.NET Core pipeline: routing, binding, authentication (including real token expiry with a test clock), authorization, rate limits, ProblemDetails, CORS, security headers | `WebApplicationFactory` with its own temp SQLite file and an in-memory catalogue | xUnit | `[Trait("Category","Api")]` | Nothing |
| **Browser (end-to-end)** | Shopper journeys, layout at five widths, an axe accessibility scan, and keyboard paths, in real Chromium against the real API and database | Real API on :5081 (fresh SQLite file), Vite on :5174, `tools/fakestore_mock.py` on :9101 | Playwright + `@axe-core/playwright` | `frontend/e2e/*.spec.ts` | .NET SDK, Python 3, Playwright's Chromium |
| **API smoke test** | Every endpoint of a running API, 200 repriced checkouts, outage modes | A running API (live FakeStoreAPI or the mock) | Python | `tools/smoke_test.py` | A running API |
| **Manual** | Real phones, screen readers, zoom, high contrast, slow networks | People | Checklist | `Documentation/Testing/ManualTestPlan.md` | A tester |

**Mocked versus real.**
- The Vitest suites never talk to the backend. Their fake API returns response shapes copied from the running API, so they verify the frontend's handling, not the backend's behaviour.
- The backend API tests and the browser tests use the real application and a real (throwaway) database.
- Only FakeStoreAPI is replaced, so no automated test needs the internet. The smoke test can point at the live FakeStoreAPI.

**SQLite and production.** SQLite is the only database provider the app has. Tests use the same provider and migrations as the app, so constraint and transaction behaviour is the real thing. If a server database (SQL Server or PostgreSQL) is added later, the Database and Api categories should also run against it. SQLite differs in locking (one writer at a time), case sensitivity and decimal storage (the app stores money as TEXT and compares with `CAST`).

**Test data.** Tests make up their own data, such as `*@example.com` and `e2e-*@example.test` accounts. Every backend test and every browser run creates a new temporary database and deletes it afterwards (browser runs use `frontend/e2e/.data/`, which is git-ignored). No test touches `App_Data/storefront.db`.

---

## 2. Commands

Run these from the repository root in PowerShell or bash. The first restore needs internet access (NuGet and npm).

### Everything
```powershell
cd backend;  dotnet restore; dotnet tool restore; dotnet build -c Release; dotnet test -c Release; cd ..
cd frontend; npm ci; npm run typecheck; npm run build; npm test; npx playwright install chromium; npm run test:e2e; cd ..
```

### By category
| Suite | Command (directory) |
|---|---|
| Frontend unit | `npm run test:unit` (frontend) |
| Frontend component and workflow | `npm run test:component` (frontend) |
| All Vitest tests | `npm test` (frontend) |
| Backend unit | `dotnet test -c Release --filter Category=Unit` (backend) |
| Backend database | `dotnet test -c Release --filter Category=Database` (backend) |
| Backend API | `dotnet test -c Release --filter Category=Api` (backend) |
| All xUnit tests | `dotnet test -c Release` (backend) |
| Browser journeys | `npm run test:e2e` (frontend). The first time, run `npx playwright install chromium`. The suite starts its own mock, API and Vite servers and stops them afterwards. |
| One browser test | `npx playwright test -g "interrupted checkout"` (frontend) |
| Browser report | `npm run test:e2e:report` (frontend; opens `playwright-report/index.html`) |
| API smoke test | See README §4 (`python tools/smoke_test.py --api http://localhost:5080`) |

The browser suite uses its own ports (5174, 5081, 9101), so it can run while `npm run dev` and `dotnet run` are using 5173 and 5080. Optional settings:
- `PYTHON`: the Python command (default `python` on Windows, `python3` elsewhere).
- `PW_CHROMIUM_PATH`: use a Chromium that's already installed.
- `E2E_API_COMMAND`: how to start the API (for example, add `--no-restore` when packages come from an offline feed).

### Coverage reports

**Frontend** (Vitest, v8 provider):
```powershell
cd frontend
npm run test:coverage            # also enforces the thresholds below
start coverage/index.html        # macOS: open coverage/index.html
```
- **Output:** `coverage/index.html` (browsable), `coverage/lcov.info` (for CI or editor plugins), `coverage/coverage-summary.json`.

**Backend** (Microsoft code coverage collector, part of `Microsoft.NET.Test.Sdk`; HTML from ReportGenerator, a local dotnet tool):
```powershell
cd backend
Remove-Item -Recurse -Force TestResults -ErrorAction SilentlyContinue   # bash: rm -rf TestResults
dotnet test -c Release --settings coverage.runsettings --results-directory TestResults
dotnet tool run reportgenerator "-reports:TestResults/**/*.cobertura.xml" "-targetdir:TestResults/coverage-report" "-reporttypes:Html;TextSummary;MarkdownSummaryGithub" "-title:Storefront API"
start TestResults/coverage-report/index.html
```
- **Output:** `TestResults/coverage-report/index.html` (by namespace, class and file, with line and branch highlighting), `Summary.txt`, `SummaryGithub.md`, and the raw `*.cobertura.xml`.
- **Delete `TestResults` first:** otherwise ReportGenerator merges the old runs too.

All report folders are git-ignored.

---

## 3a. Availability safeguards (23 October 2026, live availability pass)

Run in the same Linux workspace. These results show the safeguards behave as designed; they are not evidence of uptime.

| Suite | Before | After | Passed | Failed | Skipped |
|---|---|---|---|---|---|
| Backend xUnit | 190 | **255** (unit 132, database 28, API 95) | 255 | 0 | 0 |
| Frontend Vitest | 230 | **260 in 23 files** (unit 181, component 79) | 260 | 0 | 0 |
| Browser (Playwright) | 16 | **18** (outage journey rewritten, offline journey added, 320 px plus axe check of outage and offline states) | 18 | 0 | 0 |
| API smoke (mock) | 59 | **70** (health, correlation IDs, stale browsing, readiness Degraded and recovery) | 70 | 0 | 0 |

| Coverage | After |
|---|---|
| Backend lines / branches / methods | **96.1%** (1257/1308) / **88.7%** (515/580) / **96.7%** (504/521) |
| Frontend lines / statements / branches / functions | **96.3%** (1044/1084) / **94.31%** (1212/1285) / **90.06%** (879/976) / **94.24%** (344/365) |

- **Timeout (found on the owner's Windows machine).** The two-checkout reload test took 5.09 s there against Vitest's 5 s default (the whole run took 135 s, against about 50 s here), so it was timed out rather than failing an assertion. The component project now allows 20 s per test; unit tests keep 5 s.
- **Culture (found on the owner's Windows machine).** 3 `CorrelationIdTests` cases failed there because a test helper wrote `expectedTotal` with the machine's decimal separator (`1,00`), which is invalid JSON. The API was not at fault: it runs with `InvariantGlobalization`. The helper now formats with the invariant culture. Reproduced and re-verified under de-DE: backend 255/255 (3 failures before the fix) and Vitest 260/260.
- **Changed assertions (second review).** The old test "uses a new idempotency key when the order changes between attempts" changed the details after a *network failure* and expected a new key, which is the behaviour that could place a second order. It is now two tests: after a definite failure (503 `DATABASE_UNAVAILABLE`) a changed order gets a new key; after an uncertain one the original key is kept. New tests also cover the key surviving a reload and being released only on confirmation or 422. Disabling the pinning made those 3 tests fail. The registration-outage test now also covers 503 and a lost response.
- **Changed assertions.** One existing frontend assertion matched the old network-error wording ("Could not reach the store"), which this pass replaced. It now matches the new wording and is just as specific. Two older journeys assumed an outage blanks the catalogue; they now assert the saved copy and notice, and that checkout still fails safely.
- **Mutation checks:**
  - Disabling the reconnect reload and the offline-checkout guard made 3 tests fail; restored, all pass.
  - Restoring the old single-step save made 4 `CheckoutOutcomeTests` cases fail. In those, a committed order was reported as 503 `DATABASE_UNAVAILABLE` / `DATABASE_BUSY`.
  - Turning off the error-response log line made 8 `CorrelationIdTests` fail; letting it log a second time after the exception handler made 5 fail.
- **Follow-up review** (owner's three checks before committing): checkout's write was split so a failure is definite only when COMMIT was never sent; errors answered without an exception now get one log line; there are new tests for losing the database while running and for liveness during pending migrations.
- **Coverage gaps:** `CorrelationIdMiddleware.CorrelationScope` is 37.5% covered, because tests log as text and only `ToString` runs. Its structured form was checked by hand: a Production-mode run wrote JSON lines with a `CorrelationId` field. `DatabaseHealthCheck` is 75% covered; its catch block isn't reached, because EF's `CanConnectAsync` returns false for an unopenable file instead of throwing.
- **Owner's Windows machine:** `dotnet test -c Release`, `npm run test:coverage` and `npm run test:e2e` all passed (23 October 2026, reported by the owner) after the two test-only fixes above.

---

## 3. Results on 23 October 2026 (testing and QA pass)

Measured in a Linux workspace: .NET SDK 10.0.401, Node 22.22.0, Chromium 141 (headless) with Playwright 1.64.0. The same commands (backend tests, `npm ci`, `npm test`, `npm run test:coverage` and `npm run test:e2e`) were then run on the owner's Windows machine on 23 October 2026 and all passed.

### Test counts
| Suite | Before (baseline) | After | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|---|---|
| Frontend Vitest | 98 tests in 8 files | **230 tests in 19 files** (unit 164, component 66) | 230 | 0 | 0 | about 40 s (about 50 s with coverage) |
| Backend xUnit | 161 | **190** (unit 104, database 28, API 58) | 190 | 0 | 0 | about 11 s of tests, 15 s with startup |
| Browser (Playwright) | 0 in the repo | **16** (8 journeys, 5 viewport widths, axe scan, keyboard, reduced motion) | 16 | 0 | 0 | about 1.5 min, including building and starting the API |
| API smoke (mock) | 59 | 59 (unchanged) | 59 | 0 | 0 | about 30 s |

The frontend baseline took about 30 s with Vitest's default reporter.

### Coverage
| | Before | After | Denominator |
|---|---|---|---|
| Frontend lines | 92.55% | **96.82% (975)** | 1007 lines |
| Frontend statements | 88.59% | **94.59% (1137)** | 1201–1202 statements |
| Frontend branches | 81.65% | **90.24% (823)** | 910–912 branches |
| Frontend functions | 90.96% | **94.46% (324)** | 343 functions |
| Backend lines | 95.9% (1026) | **96.3% (1030)** | 1069 coverable lines, 44 files |
| Backend branches | 87.2% (426) | **88.1% (430)** | 488 branches |
| Backend methods | 98.0% | **98.0%** | 469 methods |

- **Same settings before and after:** the "before" figures were measured on the last commit (`717b91b`) with the exclusions below.
- **Denominators:** the frontend counts changed by one or two when error mapping and outcome classification moved into their own modules.

**Thresholds** (frontend, in `vite.config.ts`): statements 93%, branches 88%, functions 92%, lines 95%. They sit about two points under the measured figures, so a real drop fails `npm run test:coverage` but a one-line change doesn't. The backend has no threshold yet: the Microsoft collector can't enforce one without an extra package. Check `Summary.txt` instead and keep line coverage at 95% or above.

### Exclusions
| Excluded | Why |
|---|---|
| Frontend `*.test.*`, `src/test/**` | The tests and the fake API themselves |
| Frontend `src/main.tsx` | Three lines that mount the app into `index.html`; run by every browser test |
| Frontend `src/types/**`, `vite-env.d.ts` | Type declarations only: no runtime code |
| Backend `Storefront.Api.Tests.dll` | The tests themselves |
| Backend `Data/Migrations/**` | Generated by `dotnet ef`; checked instead by `Migrations_match_the_model` |
| Backend `[GeneratedCode]`, `obj/**` | Source-generated code (for example the idempotency-key regex) |
| **Not** excluded: `[CompilerGenerated]` | Async methods and lambdas compile into generated classes; excluding them would silently drop most of the checkout code |

The raw Microsoft collector report says 93.8% for the backend, because it also counts the test project and the migrations. That figure isn't used.

### Mutation checks (do the tests catch real bugs?)
Each safeguard was broken on purpose, the suite was run, and the change was put back.

- **Frontend: 21 of 21 caught.** They include: session refresh sharing, renewing and retrying after a 401, ending a rejected session, ignoring a refresh that finishes after sign-out, refreshing before expiry, never falling back to a guest order, the bearer header, reusing the idempotency key on a retry, classifying uncertain and definite outcomes, reading the replay header, rotating the key after it is reused, Retry-After, timeouts, keeping tokens out of storage, rejecting incomplete token responses, cleaning up the product URL, mapping auth errors, the focus trap, and discarding stale catalogue responses. The double-submit ref is the one safeguard no test can break on its own, because the disabled button already stops the second submit.
- **Backend:** each of the following made its tests fail:
  - checkout accepting an invalid token as a guest
  - the "lost the insert race, return the winner's order" branch
  - the `[Produces]` content-type override on error responses
  - (from earlier rounds) ownership filters, idempotency lookup, `[Authorize]`, security headers
- **Browser:** making checkout create a new idempotency key on every attempt broke the interrupted-checkout journey, because the retry created a second order and no "not placed a second time" note appeared.

---

## 4. Defects found by this round of testing

| Found by | Defect | Fix |
|---|---|---|
| Backend API test (content type) | Errors returned from controllers (400 bad request body, 404 order not found, 401 on checkout) were labelled `application/json` instead of `application/problem+json`: every controller had `[Produces("application/json")]`, which overrides the problem content type. Errors from authentication and the global exception handler were already correct, so the API was inconsistent. | Removed `[Produces]` from the five controllers. Successful responses are still `application/json`. Tests now pin down both. |
| Coverage report | The "two identical checkouts race to insert" recovery path in `CheckoutService` (return the winner's order) had never run under test. The existing 8-way parallel test usually finishes before reaching it, so it passed either way. | A test interceptor (`BeforeFirstSave`) commits the competing order between the service's check and its insert, so the race happens every time. Added a second test for the same key with different items (rejected, not merged), and a third for collisions that keep happening (gives up instead of looping). |
| Browser layout test at 320 px | The sign-in and registration form was wider than a 320 px screen: the password field's built-in minimum width plus the room for **Show** forced the column to 334 px, and the dialog scrolled sideways. The earlier screenshot audit never opened sign-in at that width. | `.field input { width: 100%; min-width: 0 }`. |
| Unit test (fake fetch) | Not an app defect. The first fake `fetch` didn't reject an already-cancelled signal the way browsers do, so a test hung. The fake now behaves like the real `fetch`. | Test double fixed |

---

## 5. What is not covered, and why

**Automated, but weak or indirect**
- **Real token revocation:** Identity bearer tokens can't be revoked, so "sign-out invalidates the token" isn't testable. Tests check that the browser forgets the tokens.
- **Rate limits per IP:** one test (login is throttled) runs with a low limit; the checkout limit is only exercised by the smoke test.
- **SQLite only:** locking and concurrency behaviour may differ on a server database (§1).
- **Unreachable branches:** `Program.cs` (83.8%) isn't covered outside Development (HSTS, HTTPS redirect, warning when CORS isn't configured), and nor are the Identity cookie redirect handlers, which the API never uses because it relies on bearer tokens. They're simple configuration; a Production-mode smoke run would cover them.
- **Frontend defensive branches:** a few, such as aborting a checkout request when the dialog closes mid-request (closing is disabled while submitting), and the double-submit ref, which is already covered by the disabled button.

**Not automated**
- **People and hardware:** screen readers, real phones, browser zoom to 400%, high-contrast mode, Safari and Firefox. The browser suite runs Chromium only. All of these are in the manual plan, which has not been carried out yet.
- **The live FakeStoreAPI:** checked only by the smoke test, never by the automated suites, which deliberately avoid the internet.
- **Load and performance:** beyond the 200-checkout smoke run.
- **Visual regressions:** no screenshot comparison. Layout tests check overflow and reachability, not appearance.

**Not proof of compliance.** The axe scan finds machine-detectable WCAG problems: missing names, contrast, ARIA misuse. Passing it doesn't make the site accessible. Sections A1–A8 of the manual plan cover what needs a person.
