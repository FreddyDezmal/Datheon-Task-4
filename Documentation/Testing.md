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
| **Backend PostgreSQL** | Migrations and schema, health, checkout idempotency (replay, changed details, concurrent same-key submissions, the insert race), outcomes with failures injected before, at and after COMMIT, accounts, sign-in protection and sessions, the connection-pool cap, persistence across an app restart | A real PostgreSQL server; each test creates and drops its own database | xUnit | `[Trait("Category","PostgreSql")]` | `STOREFRONT_TEST_POSTGRES` pointing at a **disposable** server. Skipped, with the reason, when it isn't set. |
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
| Backend PostgreSQL | set `STOREFRONT_TEST_POSTGRES` (e.g. `Host=localhost;Port=55433;Username=postgres;Password=…`), then `dotnet test -c Release --filter Category=PostgreSql` (backend) |
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
- `E2E_PRODUCTION_BUILD=1`: test the production bundle (`vite build`, then `vite preview`) instead of the dev server, as a static host serves it.

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

## 2a. Continuous integration (GitHub Actions)

Two workflows live in `.github/workflows/`.

**`ci.yml` runs on every push to `main`, every pull request, and by hand ("Run workflow").** With Render's auto-deploy set to **After CI Checks Pass**, a commit reaches production only when every job below passes:

| Job | What it runs | Fails when |
|---|---|---|
| Backend build and tests | `dotnet restore`, `dotnet build -c Release`, `dotnet test -c Release` against a throwaway PostgreSQL 16 service container, then `tools/ci/check_test_results.py` | A build warning or error; a NuGet package with a known advisory (`TreatWarningsAsErrors` turns NuGet's audit into an error); any test failing **or skipped**; fewer than 416 tests found |
| Frontend typecheck, tests and build | `npm ci`, `npm audit --omit=dev`, `npm run typecheck`, `npm run test:coverage`, `npm run build` with the production API address | An advisory against the shipped packages (React, React DOM); a type error; a failing test or a coverage threshold missed; source maps or `localhost`/`127.0.0.1` in `dist/` |
| Browser tests (dev) and (production) | The full Playwright suite twice: on the Vite dev server, and on the production bundle under the site's security headers | Any failing journey or CSP violation |
| Container image builds | `docker build --pull` of `backend/`, as Render builds it; then checks the image holds `Storefront.Api.dll` and not `appsettings.Development.json` | The Dockerfile breaks; SA-15 regresses |
| Secret scan | gitleaks over the **whole** history (a secret deleted later is still exposed) | Any finding not listed in `.gitleaksignore` |

**`security.yml` runs every Monday at 07:17 (Johannesburg), on pull requests that change dependencies, and by hand.** It doesn't gate deploys, because its results change when advisories are published, not when the code does. A failed scheduled run emails the repository owner.

| Job | Fails when |
|---|---|
| Dependency advisories | `dotnet list package --vulnerable --include-transitive` lists anything; `npm audit` (development tooling included) finds a high or critical advisory |
| Container image scan | Trivy finds a high or critical vulnerability with a fix available in the built image. Usually fixed by rebuilding the image ("Clear build cache & deploy" on Render) |

**Why the skipped-test check exists.** The PostgreSQL tests skip themselves when `STOREFRONT_TEST_POSTGRES` isn't set, and `dotnet test` still reports success. *Verified*: without the variable, the suite printed `Passed! … Skipped: 16` with exit code 0. `check_test_results.py` reads the TRX files and fails the run unless every test ran and passed. Raise its minimum (`416`) when tests are added.

**Supply chain.**
- **Actions:** every third-party action is pinned to a full commit SHA, with the version in a comment.
- **Tools:** gitleaks and Trivy are downloaded from their GitHub releases and checked against pinned SHA-256 checksums, rather than run as third-party actions. Trivy 0.69.4 was a compromised release (CVE-2026-33634), so the pin is 0.75.0.
- **Permissions:** the workflows get a read-only token, and checkout doesn't keep credentials.

To update a pin, take the new tag's commit SHA from the action's releases page, or the new checksum from the tool's `checksums.txt`.

**Reviewed false positive.** The scan flags the sample `Idempotency-Key` UUID in `Storefront.Api.http`. It's an example request, not a credential, so `.gitleaksignore` lists that finding's fingerprint. A new finding of the same kind still fails.

**What was verified before the first run on GitHub (25 October 2026).** Each job's steps were run in a fresh clone of the repository with the workflows committed on top. Tooling: `actionlint` 1.7.12 with shellcheck (clean), .NET 10, Node 22, PostgreSQL 16, Chromium.

| Check | Result |
|---|---|
| Backend tests | 416 of 416 ran and passed |
| Skipped-test guard | Fails on skipped tests, on no result files, and on too few tests |
| Frontend | `npm audit` 0, `npm run typecheck` passed, 299 tests passed, thresholds met. The bundle is `index-Bk02M7jR.js`, the same file Render serves |
| Bundle guard | Fails on a `localhost` API address and on source maps |
| Browser tests | dev: 18 passed and 3 skipped (header tests that need the production build); production: 21 passed |
| Secret scan | 14 commits, no leaks. A token committed and then deleted is caught (exit 1) |

Three steps need services this workspace can't reach, so their first real run is on GitHub:
- the Docker image build and its contents check;
- `dotnet list package --vulnerable`;
- the Trivy scan.

Check the first run of both workflows in the **Actions** tab.

**First run on GitHub (25 October 2026): one real defect found.** Five jobs passed, including the three steps run there for the first time, the image build and its contents check. The production-mode browser job failed on the axe `target-size` rule. Product 14's "Add to cart" button was "partially obscured … 48px by 20.4px" by the same card's title link.

- **Cause:** the two-line clamp was on the heading, so the link inside still had all 9 lines of its 96-character title. They were hidden, but its box reached down over the button. Six products were affected at narrow widths, by up to 39px.
- **Why it slipped through until then:** axe checks only what's on screen. Locally the card was never in view at that moment of the test; on GitHub's runner (Chromium 156) it was.
- **Fix:** the clamp is now on the link itself (`catalog.css`). The catalogue is pixel-identical before and after at 320, 768 and 1440px.
- **New regression check:** the layout checks now fail when any two controls in the same layer overlap, anywhere on the page, so the test no longer depends on scroll position. A button inside a field is allowed only within the padding the field reserves for it.
- **Verified:** with the old CSS, the check reports all six cards, and axe flags product 14 once it's in view. With the fix, the full browser suite passes in both modes on Chromium 141 and 153, and axe passes with each of the 20 cards in view.

**Second run: two timing-dependent test failures, no product defect.**
- **The axe scan ran mid-animation.** axe measured the details dialog's button at `#c1ccc7` on `#4d7466` (3.17:1). That's the white-on-`#1d5442` button partway through its fade-in. `scan()` now waits for running animations and transitions to finish, as the layout check already did.
  - *Reproduced* by slowing every animation to 10% speed through the DevTools protocol: the previous test failed on the same two elements, and the new one passes.
- **The overlap check compared a field scrolled out of view with the dialog's header.** When the checkout form was scrolled 264–300px, the name field had moved above the form's visible area, behind the header, so its box overlapped the close button (23px on GitHub). Controls are now compared only within the same layer **and** the same scroll container, where their positions relative to each other can't change.
  - *Reproduced* by sweeping the form's scroll position in 4px steps: the previous check failed at 284px, and the new one passes.
  - The product-card defect is still caught: with the old CSS, the check reports all six cards.
- **The whole browser suite was then run with animations at 10% speed**, as a stand-in for a slower machine:

  | Mode | Runs | Passed |
  |---|---|---|
  | Production | 1 | All |
  | Dev | 5 | 4 |

  The first dev run had one failure, and its log wasn't kept, so which test failed isn't known. A tenfold slowdown is far beyond a real runner. If a flaky failure shows up in CI, the job's Playwright report artifact has the trace.

**Third run: all six checks green**, but the production browser job took 8m17s, of which the tests took 46.6s. `npx playwright install --with-deps chromium` spent 6m58s, almost all of it apt downloading 25.9 MB of fonts and Xvfb from a slow Ubuntu mirror (67 kB/s). The log shows every library Chromium needs was already on the runner. The job now:
- installs only the headless shell (`--only-shell`), which is what Playwright launches for headless runs with no `channel` set;
- caches it per Playwright version (`actions/cache` v6.1.0, pinned by SHA);
- skips the apt step.

A missing library would stop the browser from launching, so this can't pass silently.

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
- **Token revocation** is now tested end to end: server-side sessions (Security-Audit.md, SA-02) make sign-out, refresh-token reuse and password changes end a session, and both tokens are then refused. The browser suite checks the old refresh token gets 401 after sign-out.
- **Rate limits per IP:** login throttling, IPv6 /64 grouping and per-account sign-in protection are tested; the checkout limit is only exercised by the smoke test.
- **Database behaviour:** the SQLite suites run everywhere. The PostgreSQL suite runs where `STOREFRONT_TEST_POSTGRES` is set, which includes CI (§2a), where skipping it fails the run.
- **Unreachable branches:** `Program.cs` (83.8%) isn't covered outside Development (HSTS, HTTPS redirect, warning when CORS isn't configured), and nor are the Identity cookie redirect handlers, which the API never uses because it relies on bearer tokens. They're simple configuration; a Production-mode smoke run would cover them.
- **Frontend defensive branches:** a few, such as aborting a checkout request when the dialog closes mid-request (closing is disabled while submitting), and the double-submit ref, which is already covered by the disabled button.

**Not automated**
- **People and hardware:** screen readers, real phones, browser zoom to 400%, high-contrast mode, Safari and Firefox. The browser suite runs Chromium only. All of these are in the manual plan, which has not been carried out yet.
- **The live FakeStoreAPI:** checked only by the smoke test, never by the automated suites, which deliberately avoid the internet.
- **Load and performance:** beyond the 200-checkout smoke run.
- **Visual regressions:** no screenshot comparison. Layout tests check overflow and reachability, not appearance.

**Not proof of compliance.** The axe scan finds machine-detectable WCAG problems: missing names, contrast, ARIA misuse. Passing it doesn't make the site accessible. Sections A1–A8 of the manual plan cover what needs a person.
