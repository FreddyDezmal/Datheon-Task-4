# Storefront audit: 22 October 2026

Scope: the code in `storefront.zip` as delivered. Every result below comes from a command run during this audit. The audit environment's outbound proxy refuses nuget.org and fakestoreapi.com, so any check that depends on either is marked **Blocked**.

## 1. Executive summary

The frontend is in good shape: a clean `npm ci`, strict `tsc -b`, the production build, all Vitest tests and the browser-level checks pass. Against the FakeStoreAPI stand-in, the API behaves correctly on every endpoint, error path and CORS check, and its pricing agrees with the frontend's code to the cent on 500 random carts.

The audit found **six defects** and fixed all of them, each with a test:

1. The Swashbuckle version was outside its publisher's support matrix for .NET 10.
2. A production CORS allowlist couldn't remove the development origin.
3. A checkout without `expectedTotal` charged a total the customer never confirmed.
4. An over-limit request body returned 500 instead of 413.
5. Invalid checkouts produced error responses larger than the request, and there was no checkout body limit.
6. Under React StrictMode the "saved cart could not be read" notice was lost.

**The highest-priority remaining work can't be done here:** run `dotnet restore`, `dotnet test` and the Swagger check on a machine that can reach nuget.org. Then run `tools/smoke_test.py` against live FakeStoreAPI. Until that happens, no backend test has run under real xUnit, and Swagger is unverified.

## 2. Verification matrix

| # | Check | Command | Result | Limitation |
|---|---|---|---|---|
| 1 | Toolchain | `dotnet --list-sdks`, `--list-runtimes`; `node -v`; `npm -v` | SDK 10.0.401; runtimes Microsoft.NETCore.App and AspNetCore.App 10.0.12; Node 22.22.0; npm 10.9.4 | SDK installed from conda-forge (the dotnet.microsoft.com hosts are blocked) |
| 2 | Package restore | `dotnet restore` (in `backend/`) | **Blocked**: `error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json … status code '403'`. All alternative feeds probed (nuget.org CDN, dotnet-public on Azure DevOps, feedz.io, GitHub Packages) were also refused. | Proxy policy |
| 3 | Package versions exist | `git ls-remote --tags` on each upstream repo | Swashbuckle `v9.0.4` and `v10.3.1`, xunit `v2-2.9.3`, xunit.runner.visualstudio `3.1.0`, vstest `v17.13.0` all exist as tags | Not proof of availability on nuget.org |
| 4 | Swashbuckle compatibility | Read `Directory.Build.props` and `README.md` at tags v9.0.4 and v10.3.1 | v9.0.4 builds `net9.0;net8.0` only and is listed for ASP.NET Core 8.0/9.0; v10.x adds `net10.0`. **Defect D1.** | — |
| 5 | Build | `dotnet build` | **Blocked** by #2 | — |
| 6 | Backend tests (real xUnit) | `dotnet test` | **Blocked** by #2. 0 tests discovered or run under xUnit. | — |
| 6a | Backend test source compiles; supplementary run | SDK 10.0.401 with a stand-in for the xUnit API and runner | Compiles with 0 warnings and 0 errors; 106/106 cases pass on the stand-in. Each new test was mutation-checked (it fails when its fix is reverted). | **Not** a substitute for #6 |
| 7 | Production source compiles | SDK 10.0.401, stand-in for `AddSwaggerGen`/`UseSwagger`/`UseSwaggerUI` | 0 warnings, 0 errors (`TreatWarningsAsErrors` on) | Real Swashbuckle not compiled |
| 8 | Swagger / OpenAPI | `GET /swagger/v1/swagger.json`, `/swagger/index.html` | **Blocked** (needs the package). Confirmed Swagger isn't mapped in Production (404). | — |
| 9 | API against the mock, baseline | `python3 tools/smoke_test.py --api … --mock …` | 43 passed, **2 failed** (missing `expectedTotal` accepted; 644 KiB body accepted with a 1.2 MB error response), 1 skipped (Swagger) | — |
| 10 | API against the mock, after fixes | same | **45 passed, 0 failed**, 1 skipped (Swagger) | Mock data approximates live data |
| 11 | Upstream failures | smoke test `--mock` | 500 → 502 `UPSTREAM_UNAVAILABLE`; HTML body → 502 `UPSTREAM_MALFORMED`; 15 s stall → 504 `UPSTREAM_TIMEOUT` after 10.0 s; checkout during outage → 502; recovers once healthy | — |
| 12 | Body over Kestrel's 30 MB default | `curl --data-binary @42.9MB.json` | Before: **500 `INTERNAL_ERROR`**, logged as an error (**D4**). After: 413 `REQUEST_TOO_LARGE` | — |
| 13 | Error amplification | 660 KB body with 20,000 duplicate lines | Before: 400 with a **1,249,091-byte** response (**D5**). After: 413, 178 bytes | — |
| 14 | CORS (Development) | smoke test | Preflight from `http://localhost:5173` allowed, other origins refused; CORS header present on 400, 404 and 502 responses | — |
| 15 | CORS (Production config) | API with `ASPNETCORE_ENVIRONMENT=Production`, `Cors__AllowedOrigins__0=https://shop.example.com` | Before: **`http://localhost:5173` still allowed** (**D2**). After: only the configured origin | — |
| 16 | Client vs server pricing | `tsx` script: frontend `utils/pricing.ts` vs `POST /api/checkout` on 500 random carts | 500/500 agree on subtotal, tax, shipping and total (before and after fixes) | — |
| 17 | Frontend from clean extract | `npm ci && npx tsc -b && npm run build && npm test` | 0 vulnerabilities; tsc OK; build OK; 54/54 tests before fixes, 55/55 after | — |
| 18 | Browser, dev server (StrictMode) | headless Chromium 153 | Corrupt saved cart: **no notice** before the fix (**D6**), notice after. Repaired cart notice, 409 re-price and confirm, outage then retry (cart kept), badge counts 3 units for 1 line, empty-search state, Tab trap, Escape and focus return all pass | — |
| 19 | Browser, production build | `vite preview` + Chromium | Corrupt-cart notice shown, so D6 is development-only | — |
| 20 | Live FakeStoreAPI | — | **Not tested** (host unreachable) | Proxy policy |
| 21 | Patch reproducibility | `git apply --check` on a fresh extract of the zip | Applies cleanly and reproduces the fixed tree exactly | — |

## 3. Defects found

| ID | Severity | File | Root cause | Impact | Fix |
|---|---|---|---|---|---|
| D1 | High (build) | `backend/Storefront.Api/Storefront.Api.csproj` | Pinned `Swashbuckle.AspNetCore` 9.0.4, which ships `net8.0`/`net9.0` assets and is documented for ASP.NET Core 8.0/9.0 only | Running on .NET 10 is unsupported by the publisher; Swagger may misbehave | Pinned **10.3.1**, the line that targets `net10.0`. Its breaking changes are in the `Microsoft.OpenApi` types, which this code doesn't use (only the three parameterless calls). **Restore and compilation against it are unverified.** |
| D2 | Medium (security) | `backend/Storefront.Api/Configuration/CorsSettings.cs` | `AllowedOrigins` had the initializer `["http://localhost:5173"]`, and the configuration binder appends configured array values to an initialized array | A production allowlist still admitted `http://localhost:5173` (reproduced) | Default is now `[]`; origins come only from configuration, and `appsettings.json` keeps the dev origin. Logs a warning when empty. Test: `CorsSettingsTests` |
| D3 | Medium (integrity) | `backend/Storefront.Api/Services/CheckoutRequestValidator.cs`, `DTOs/CheckoutDtos.cs` | `expectedTotal` was optional | A client omitting it was charged the server total with no confirmation, so the stale-quote safeguard depended on the client opting in | `expectedTotal` is required (400 when missing). It is still never used as a price. Tests: `Expected_total_is_required`, `Negative_expected_total_is_rejected`; existing tests updated to send the correct total |
| D4 | Medium (reliability) | `backend/Storefront.Api/Middleware/GlobalExceptionHandler.cs` | `BadHttpRequestException` (over-limit or truncated body) fell through to the catch-all | Client errors returned 500 and were logged as server failures | Mapped to its own status: 413 `REQUEST_TOO_LARGE`, otherwise 4xx `BAD_REQUEST`. Tests in `GlobalExceptionHandlerTests` |
| D5 | Medium (DoS) | `backend/Storefront.Api/Controllers/CheckoutController.cs`, `Services/CheckoutRequestValidator.cs` | No body limit below Kestrel's 30 MB default, and one validation error per line even past the 50-line maximum | 660 KB in, 1.25 MB out; large bodies fully deserialised | `[RequestSizeLimit(64 KiB)]` on checkout (a maximal valid order is about 3 KiB); past the line limit only the count error is reported. Test: `Over_the_line_limit_only_the_count_error_is_reported` |
| D6 | Low (development only) | `frontend/src/utils/cartStorage.ts` | `loadCart` deleted the unreadable key inside `useReducer`'s initializer. StrictMode runs initializers twice, so the second run saw empty storage and overwrote the "discarded" status | In development, the "saved cart could not be read" notice never showed. Production was unaffected (verified) | `loadCart` is now read-only; the existing save effect removes the key. Regression test runs `App` under `<StrictMode>` (fails before the fix) |

Also corrected: the README claimed backend "Met" results based on the stand-in runner. Section 9 now reports Blocked, Passed and Not tested status for each check.

## 4. Security and correctness review

**Already in place (verified):**

- **Server-side repricing.** The server prices from its own catalogue and ignores client `unitPrice` or `price` fields, checked with a `unitPrice: 0.01` request.
- **Stale totals.** A stale total returns 409 with a full server quote. The UI re-prices the cart and asks the shopper to confirm.
- **Validation.**
  - Product IDs must be positive and unique.
  - Quantities must be integers from 1 to 99.
  - At most 50 lines per order.
  - Name must be 2 to 100 characters, and the email must be well formed.
  - Upstream products with an invalid id, title, price or rating are rejected.
  - Unknown products return 422 with their IDs.
- **Money arithmetic.** The server uses `decimal` with half-away-from-zero rounding; the client uses integer cents. Tax is rounded once on the merchandise subtotal, and the free-shipping threshold is inclusive and based on the pre-tax subtotal. Client and server agreed on all 500 random carts.
- **Error responses.** Errors are ProblemDetails with a stable `code`; 500s never include exception detail.
- **Cart handling.** The cart is cleared only after a 200 confirmation and is kept on every failure path (verified in the browser).
- **Caching.** Upstream failures are never cached. After expiry, an outage fails closed (502), so stale prices are never served.
- **Configuration.** Options are validated at startup, and there are no secrets in the repository.

**Remaining risks (not fixed; need further work):**

1. **Duplicate orders on retry.** There's no idempotency key, so a lost response followed by **Try again** places a second order. That's harmless while checkout is simulated; add an `Idempotency-Key` before real orders.
2. **Stuck 409 loop.** If `/api/store/config` fails at page load and the server uses non-default rules, every checkout returns 409 until reload. A fix would refetch the config on a 409.
3. **Catalogue cache stampede.** Concurrent cold-cache requests each call FakeStoreAPI; there's no single-flight.
4. **Production hardening.** There's no rate limiting, HTTPS redirection, HSTS, upstream retry or circuit breaker, and `AllowedHosts` is `*`.
5. **Concurrency.** Checkout is stateless with no inventory, so there's no race on stock. Prices are authoritative as of the last catalogue fetch, up to 5 minutes old by default.

## 5. Changes made

All changes are in `Documentation/Audits/storefront-audit.patch` (apply from the project root with `git apply`); it applies cleanly to the original zip. The pre-fix versions of every modified file are kept in `Documentation/Audits/originals/`.

| File | Change | Reason |
|---|---|---|
| `backend/Storefront.Api/Storefront.Api.csproj` | Swashbuckle 9.0.4 → 10.3.1 | D1 |
| `backend/Storefront.Api/Configuration/CorsSettings.cs` | Default `[]` | D2 |
| `backend/Storefront.Api/Program.cs` | Warn when no CORS origins are configured | D2 |
| `backend/Storefront.Api/Services/CheckoutRequestValidator.cs` | Require `expectedTotal`; skip per-line errors past the limit | D3, D5 |
| `backend/Storefront.Api/DTOs/CheckoutDtos.cs` | Doc comments for the required `expectedTotal` | D3 |
| `backend/Storefront.Api/Middleware/GlobalExceptionHandler.cs` | Map `BadHttpRequestException` to 413/4xx | D4 |
| `backend/Storefront.Api/Controllers/CheckoutController.cs` | `[RequestSizeLimit(64 KiB)]`, 413 documented | D5 |
| `backend/Storefront.Api.Tests/Configuration/CorsSettingsTests.cs` (new) | 2 tests | D2 |
| `backend/Storefront.Api.Tests/Checkout/CheckoutRequestValidatorTests.cs` | `ValidRequest` sends the correct total; 3 new tests | D3, D5 |
| `backend/Storefront.Api.Tests/Checkout/CheckoutServiceTests.cs` | Sends the correct total for a multi-line cart | D3 |
| `backend/Storefront.Api.Tests/Checkout/GlobalExceptionHandlerTests.cs` | 2 new tests | D4 |
| `frontend/src/utils/cartStorage.ts` | `loadCart` is read-only | D6 |
| `frontend/src/utils/cartStorage.test.ts` | Asserts purity and repeatability instead of removal | D6 (removal is now asserted end to end in `App.test`) |
| `frontend/src/App.test.tsx` | Corrupt-cart test runs with and without `<StrictMode>` and checks the key is removed | D6 |
| `tools/smoke_test.py` (new) | Reusable end-to-end API verification, standard library only | Reproducibility |
| `README.md` | Swagger is Development-only; `expectedTotal` required; 413; CORS in production; mock modes; full test commands; smoke test; honest verification section; risks | Accuracy |
| `Documentation/Audits/AUDIT.md` (new) | This report | — |

No dependencies were added. No test was weakened. One assertion moved: removal of a corrupt key is now asserted after mount in `App.test` rather than inside `loadCart`.

## 6. Reproduction

```bash
# Backend (needs nuget.org)
cd storefront/backend
dotnet restore && dotnet build -c Release && dotnet test -c Release --no-build
cd Storefront.Api && dotnet run --launch-profile http   # API :5080, Swagger http://localhost:5080/swagger

# Frontend
cd storefront/frontend
npm ci && npm run typecheck && npm test && npm run build
npm run dev                                             # http://localhost:5173

# Offline mock and failure modes
python3 tools/fakestore_mock.py 9100
FakeStore__BaseUrl=http://localhost:9100/ FakeStore__CatalogueCacheSeconds=0 dotnet run --launch-profile http
python3 tools/smoke_test.py --api http://localhost:5080 --mock http://localhost:9100

# Live FakeStoreAPI
python3 tools/smoke_test.py --api http://localhost:5080
```

Expected after restore: `dotnet test` discovers 106 test cases. That count comes from the stand-in run and should be confirmed. The smoke test should report 0 failed, with Swagger checked rather than skipped.

## 7. Remaining limitations

**Environmental blockers (not code defects):**

- No NuGet access, so restore, build, `dotnet test`, Swagger and the Swashbuckle 10.3.1 upgrade are unverified.
- No access to fakestoreapi.com, so live integration is untested.

**Code-level limitations:** see the remaining risks in section 4 (idempotency, config fallback 409 loop, cache stampede, production hardening).

## 8. Final acceptance review

| Criterion | Status |
|---|---|
| Real `dotnet restore` | **Blocked** |
| Real `dotnet test` (xUnit) | **Blocked** |
| Swagger / OpenAPI available | **Blocked** |
| Live FakeStoreAPI integration | **Not tested** |
| API endpoints against the mock | **Passed** |
| Malformed, timeout and HTTP-failure upstreams | **Passed** (mock) |
| Missing products, invalid quantities, price mismatch | **Passed** |
| CORS for the documented origin (including error responses and production config) | **Passed** (after D2 fix) |
| Server never trusts client prices or totals | **Passed** |
| Stale quote returns 409 and requires renewed confirmation | **Passed** (after D3 fix, required on the server) |
| Failed checkout keeps the cart; success clears it only after confirmation | **Passed** |
| Client and server calculations consistent | **Passed** (500 carts) |
| Strict TypeScript, production build, Vitest | **Passed** (55/55) |
| Cart persists across refresh; badge counts units | **Passed** |
| Quantity and removal behaviour consistent | **Passed** |
| Drawer focus, Escape, keyboard | **Passed** |
| Loading, empty, error, retry and confirmation states | **Passed** |
| README accurate and reproducible | **Passed** for the frontend from a clean extract; the backend steps are blocked at restore here |
