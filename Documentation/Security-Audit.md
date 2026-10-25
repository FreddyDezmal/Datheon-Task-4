# Storefront security audit

**Date:** 24 October 2026. **Commit audited:** `b897c34` on `main`, with no uncommitted changes apart from the untracked prompt file. **Mode:** read-only assessment. The only file this audit wrote to the repository is this report. Nothing was changed in production.

---

## 1. Executive summary

The Storefront is a demo shop with no payments. A React static site on Render calls an ASP.NET Core 10 API on Render, which stores accounts and orders in PostgreSQL.

**The core controls hold up under direct testing:**
- **Checkout integrity.** The server re-prices every order, ignores client-supplied prices and user IDs, and enforces every limit tested. Under stress on a disposable PostgreSQL database, idempotency held completely:
  - 20 rounds of 100 simultaneous identical requests: one order each round;
  - one key racing with two different orders, and a guest racing an account: one order, the losers got 422;
  - 200 parallel orders: all distinct;
  - the API process killed abruptly during 8 bursts (5 of them mid-flight), then every key retried: no duplicates, no partial orders, and every committed order recovered by replay.
- **Order access.** Every order query is bound to the caller's token. A second account, a guest and an anonymous caller could not read or replay another customer's order.
- **Secrets.** Git history (11 commits), the production bundle and the image contents hold no secrets.
- **CORS** allows exactly one origin.
- **Errors and logs** leak nothing sensitive.

**No Critical or High findings.** There are 4 Medium findings, mostly in the account system that ASP.NET Core Identity provides, plus one availability issue:

| ID | Severity | Finding | Status |
|---|---|---|---|
| SA-01 | Medium | A stolen access token can switch on two-factor authentication and lock the owner out permanently (unused Identity endpoints are exposed) | Confirmed locally |
| SA-02 | Medium | Refresh tokens can't be revoked: reusable for 14 days, sign-out is client-side only, and access tokens survive a password change | Confirmed locally |
| SA-03 | Medium | Per-client rate limits key on the full IP address, so a client with an IPv6 /64 block gets a fresh limit per address | Confirmed locally; production not tested |
| SA-04 | Medium | The database connection pool (default 100) can exhaust the database's limit, and the public readiness endpoint hits the database on every call | Confirmed locally ("too many clients"); production setting unknown |
| SA-05 to SA-14 | Low | Account enumeration; targeted lockout; no email verification or recovery; an unused cookie sign-in mode; missing `Cache-Control: no-store`; large bodies on account endpoints; unverifiable runtime patch level; static-site headers; database privilege and TLS settings; upstream response trust | See section 6 |
| SA-15 to SA-20 | Informational | Image contents, log noise and missing security alerting, data retention and backups, no CI or deploy gate, HTTP redirect left to Render, deployment details in docs | See section 6 |

This doesn't make the application "secure". Several controls depend on Render dashboard settings that this audit couldn't see (section 11), and nothing was actively tested against production (section 14).

---

## 2. Scope and exclusions

**In scope:**
- `backend/` (API, EF Core model and migrations, Dockerfile, `.dockerignore`, settings);
- `frontend/` (source, build configuration, lockfile, production bundle);
- `tools/`;
- `Documentation/`;
- the full git history of the local clone.

**Excluded, or not possible from here:**
- Active testing of `https://datheon-task-4.onrender.com` or `https://datheon-task-4-1.onrender.com`. Both the workspace and the shell on the owner's computer are blocked from reaching Render by their network allowlist. No authorization for active tests was requested.
- Render and GitHub dashboard settings.
- The deployed container image itself.
- `dotnet list package --vulnerable`, because nuget.org is blocked here.
- Container image scanning (no Docker here).
- OWASP ZAP. No authorized target was reachable.

**Excluded by the safety rules:** denial-of-service testing, high-volume fuzzing, credential stuffing, and anything that touches production data. The one load-like test (section 13, T11) ran against the local copy only, and was bounded at 600 requests.

---

## 3. Architecture and threat model

```
Browser ── HTTPS ──> Render Static Site (React bundle, /* -> /index.html rewrite)
   │
   └─ HTTPS, CORS, Authorization: Bearer ──> Render edge (Cloudflare) ──> API container (ASP.NET Core 10, non-root)
                                                                             ├─> PostgreSQL (Render Free): accounts, orders, encrypted Data Protection keys
                                                                             └─> FakeStoreAPI (HTTPS): product catalogue, cached 5 min
```

- **Identity:** ASP.NET Core Identity via `MapIdentityApi`. It issues opaque bearer and refresh tokens, protected by Data Protection keys. The keys are stored in PostgreSQL, encrypted with a certificate. The frontend keeps the refresh token in `sessionStorage` and the access token in memory.
- **Checkout:** guests and accounts can both check out. Prices are always recalculated server-side, and the client's `expectedTotal` must match. Retries are protected by the `Idempotency-Key` header plus a unique index. No payment is taken and no stock is reserved, so payment and inventory controls are **not applicable**.
- **Assets:** account credentials and sessions; customers' names and email addresses on orders; order history; the Data Protection keys and certificate; the database credentials; the Render and GitHub accounts.

| Threat | Main controls | Residual risk |
|---|---|---|
| Account takeover | PBKDF2 hashing (Identity v3); 8-character complexity policy; lockout after 5 failures; per-IP limit of 10 a minute | SA-01, SA-02, SA-03, SA-05 |
| Unauthorized access to orders | User-scoped queries; 404 for other customers' orders; idempotency owner check | None found |
| Token theft or replay | No HTML-injection sinks in the frontend; access tokens kept in memory, refresh token in `sessionStorage`; 1-hour access tokens | SA-02, SA-12 (no CSP on the site) |
| Checkout duplication or manipulation | Server re-pricing, input limits, idempotency with a unique index, explicit transaction, "uncertain" outcome handling | None found |
| Endpoint abuse | Per-IP fixed-window limits on account endpoints (10 a minute) and checkout (30 a minute); body limit on checkout | SA-03, SA-04, SA-06, SA-10 |
| Database compromise or loss | Credentials only in Render settings; no raw SQL; encrypted key ring | SA-13, SA-17 (Free plan: no backups) |
| Exposed secrets and build credentials | Nothing committed; `.dockerignore`; startup refuses without the certificate | SA-18 (no CI or branch protection evidence) |
| Cross-origin attacks and injection | Exact-origin CORS without credentials; JSON-only endpoints; React escaping; API sends a CSP | SA-08, SA-12 |
| Upstream compromise or outage | Validation of upstream products; timeouts and retries; stale copy capped at 60 minutes; prices from upstream only | SA-14 |
| Unsafe deployment or recovery | Migrations never automatic in production; documented procedures | SA-11, SA-17, SA-18 |

---

## 4. Methodology and tools

1. **Baseline:**
   - checked the branch, working tree and history on the owner's machine;
   - compared checksums of all 224 tracked files with the audit copy (identical apart from one earlier audit artifact);
   - listed the entry points, routes, middleware, models, migrations, manifests and lockfiles, the Dockerfile and `.dockerignore`.
   - There are no CI workflows and no Render configuration file in the repository.
2. **Manual code review** of every controller, service, middleware, security setup file, EF configuration and migration, plus the frontend's API client, session, storage and rendering code.
3. **Dynamic testing** against a local copy that mirrors production:
   - the published Release build in `Production`, on PostgreSQL 16.15, with a freshly generated Data Protection certificate;
   - the same CORS origin, `AllowedHosts`, `CF-Connecting-IP` forwarding and default rate limits as Render;
   - the FakeStore mock as the upstream.
   - Probes were Python scripts using synthetic accounts. Each request carried its own `CF-Connecting-IP`, so the per-client limits behaved as they do live.
4. **Stress and crash tests:** concurrent checkout scenarios, and `kill -9` of the API mid-burst followed by full retries, with database invariant checks afterwards.
5. **Existing suites:** backend xUnit with PostgreSQL, the frontend typecheck and Vitest. The Playwright results come from earlier the same day on this tree.
6. **Secret scanning** over `git log -p --all` on the owner's machine, with nine rules. Only masked snippets were printed. Sensitive file extensions in history and tracked files were checked too.
7. **Supply chain:**
   - `npm audit`, for all dependencies and runtime only;
   - review of the NuGet package versions against published advisories (GitHub's advisory API is blocked here, so web sources);
   - inspection of the published output.

No SAST tool beyond the compiler is configured. `TreatWarningsAsErrors` is on, with nullable analysis and the default .NET analyzers. There is no linter for the frontend.

---

## 5. Coverage matrix

| Domain | Inspected | Tests and evidence | Result | Gaps |
|---|---|---|---|---|
| Password storage and policy | `SecuritySetup.cs` 42–50 | Weak password rejected with 4 rule errors (T1) | OK: Identity v3 PBKDF2, lockout enabled | None |
| Enumeration | Identity endpoints | Registration error codes, login timing, lockout message (T1) | SA-05 | — |
| Brute force and rate-limit keys | `SecuritySetup.cs` 136–150, `ForwardedHeadersSetup.cs` | IPv4 limit (T3); IPv6 /64 rotation; forged `X-Forwarded-For` ignored | SA-03 | Production IPv6 behaviour |
| Tokens (lifetime, refresh, revocation) | Identity bearer defaults, `SessionContext.tsx` 66–74 and 171 | Refresh reused, password change, sign-out (T1) | SA-02 | — |
| Unused Identity endpoints | `Program.cs` 163 | 2FA enabled with a token only (T2); forgotPassword, confirmEmail | SA-01, SA-07 | — |
| Cookie and CSRF | Default scheme | Cookie sign-in, preflight, `text/plain` (T6) | SA-08 (CSRF blocked) | — |
| Object-level authorization | `OrderQueryService.cs` 27, 55; `CheckoutService.cs` 155 | Second user, guest, anonymous, forged token (T1) | OK | — |
| Route-level authorization | All controllers | Route table (5.1) | OK | — |
| Input validation and mass assignment | `CheckoutRequestValidator.cs`, DTOs, `ProductQueryParameters.cs` | 13 malformed checkouts, nested JSON, 70 KB, form body (T4) | OK | — |
| SQL injection | Whole backend | No raw SQL; injection-looking inputs (T4) | OK | — |
| XSS | Frontend `src/` | No raw-HTML sinks; `<img src>` only; script stored and returned as text (T4) | OK | No browser test with a hostile upstream |
| CORS | `Program.cs` CORS policy | Seven origins (T5) | OK | Live check by the owner earlier today (14) |
| Security headers | `SecurityHeadersMiddleware.cs`; static site | API headers (T5); HSTS with a real host (T5b) | API OK; site SA-12 | Static-site headers not visible |
| Errors and leakage | `GlobalExceptionHandler.cs` | Problem bodies, 404s, Swagger 404 in Production (T4, T5) | OK | — |
| Request size and resource exhaustion | `CheckoutController.cs`, Kestrel defaults, health checks | 5 MB register body; readiness burst (T4, T11) | SA-04, SA-10 | — |
| Health endpoints | `HealthChecks.cs` | Status words only (T5) | OK; SA-04 | — |
| Checkout logic and idempotency | `CheckoutService.cs`, migrations | T7–T10 and 20 PostgreSQL tests | OK | — |
| PostgreSQL data protection | Entities, configurations, migrations, `DatabaseSetup.cs` | Invariants after crashes; logs redact parameters | OK; SA-13, SA-17 | Production role and TLS mode |
| Frontend storage and bundle | `sessionStorage.ts`, `cartStorage.ts`, `pendingCheckout.ts`; bundle scan | `npm audit`; bundle matches the deployed hash | Documented trade-off; SA-12 | — |
| Logging and privacy | Middleware, `appsettings.json` | Scan of 4,128 local log lines (T12) | OK; SA-16 | No production log review |
| Secrets in history | 11 commits | Scan (T13) | OK | — |
| Docker image | `Dockerfile`, `.dockerignore`, publish output | Publish inspection (T14) | SA-11, SA-15 | Image not built or scanned here |
| Dependencies | `package-lock.json`, `.csproj` files | `npm audit` 0; advisory review | OK; SA-11 | `dotnet list package --vulnerable` not run |
| CI and branch protection | Repository | No workflows | SA-18 | Dashboard only |

### 5.1 Route table

| Method | Route | Authentication | Authorization | Validation | Rate limit | Tests | Findings |
|---|---|---|---|---|---|---|---|
| GET | `/api/products` | None | Public | Search ≤100 characters, category slug, sort allowlist, price ranges | None (served from cache) | Unit and API | — |
| GET | `/api/products/{id:int}` | None | Public | Integer route value | None | API | — |
| GET | `/api/categories` | None | Public | — | None | API | — |
| GET | `/api/store/config` | None | Public | — | None | API | — |
| POST | `/api/checkout` | Optional bearer (an invalid one gets 401) | Order owner comes from the token only | Full business validation; 64 KB body; JSON only | 30 a minute per IP | Unit, API, PostgreSQL, browser; T4, T7–T10 | SA-03 |
| GET | `/api/orders` | Bearer required (cookie refused) | Own orders only, up to 100 | — | None | API, T1 | SA-09 |
| GET | `/api/orders/{reference}` | Bearer required | Own order only; others get 404 | Length cap | None | API, T1 | SA-09 |
| POST | `/api/account/register` | None | — | Identity rules | 10 a minute per IP | API, T1 | SA-05, SA-07, SA-10 |
| POST | `/api/account/login` | None | — | Identity | 10 a minute per IP | API, T1 | SA-03, SA-05, SA-06, SA-08 (`?useCookies`) |
| POST | `/api/account/refresh` | Refresh token | Security stamp checked | — | 10 a minute per IP | API, T1 | SA-02 |
| GET | `/api/account/confirmEmail` | Code | — | — | 10 a minute per IP | T1 | SA-07 (unusable without email) |
| POST | `/api/account/resendConfirmationEmail`, `/forgotPassword` | None | — | Always 200 | 10 a minute per IP | T1 | SA-07 (no email is sent) |
| POST | `/api/account/resetPassword` | Reset code | — | — | 10 a minute per IP | — | SA-07 (unreachable without email) |
| GET, POST | `/api/account/manage/info` | Bearer **or cookie** | Self | Old password needed to change the password | 10 a minute per IP | T1, T6 | SA-01, SA-08 |
| POST | `/api/account/manage/2fa` | Bearer **or cookie** | Self | TOTP code | 10 a minute per IP | T2 | **SA-01** |
| GET | `/health`, `/health/live` | None | Public | — | None | API | — |
| GET | `/health/ready` | None | Public | — | None | API, T11 | SA-04 |
| GET | `/swagger/*` | — | Development only (404 in Production) | — | — | T5 | — |

The frontend uses only register, login, refresh, checkout, orders and the public catalogue endpoints. Every row from `confirmEmail` down to `manage/2fa` is reachable but unused.

---

## 6. Findings, by severity

### SA-01: A stolen access token can enable 2FA and lock the owner out permanently

- **Severity:** Medium. **Confidence:** high. **Status:** confirmed, locally (T2).
- **Component:** `backend/Storefront.Api/Program.cs` line 163 (`MapIdentityApi<StorefrontUser>()` maps every Identity endpoint); `frontend` (no 2FA prompt at sign-in).
- **Evidence:**
  - Using only a victim's access token, `POST /api/account/manage/2fa` returned the shared key and recovery codes. A second call with a code computed from that key enabled 2FA (`isTwoFactorEnabled: true`).
  - The owner's correct password then got `401 RequiresTwoFactor`, and their refresh token got `401`.
  - The shop's UI can't send a 2FA code, the attacker holds the recovery codes, and password reset is impossible without email (SA-07). The owner has no way back in.
- **Attack scenario:** an attacker obtains an access token for one hour, through XSS (SA-12), a shared or compromised device, or a leaked log. They enable 2FA, and the victim loses the account and its order history for good.
- **Impact:** permanent account denial of service. It isn't a takeover: the attacker still lacks the password.
- **Preconditions and exposure:** a valid access token. The endpoint is live in production.
- **Fix:** expose only the endpoints the app uses: register, login and refresh. For example, add a small endpoint filter or middleware that returns 404 for `/api/account/manage/*`, `/confirmEmail`, `/resendConfirmationEmail`, `/forgotPassword` and `/resetPassword`, or replace `MapIdentityApi` with the three endpoints written against `SignInManager`. Re-enable 2FA only together with UI support and a recovery path.
- **Regression test:** with a valid token, `POST /api/account/manage/2fa` returns 404 and the account can still sign in with its password.

### SA-02: Refresh tokens can't be revoked; sign-out doesn't end the session on the server

- **Severity:** Medium. **Confidence:** high. **Status:** confirmed, locally (T1).
- **Component:** Identity bearer-token defaults (access token 1 hour, refresh token 14 days, not stored server-side); `frontend/src/context/SessionContext.tsx` lines 66–74 and 171 (sign-out only clears the tab's storage); `frontend/src/utils/sessionStorage.ts` line 50.
- **Evidence:**
  - The same refresh token was accepted twice (`200`, `200`), so it isn't rotated, and reuse isn't detected.
  - There is no server-side sign-out.
  - After a password change, the old refresh token was rejected (`401`, because the security stamp changed), but the old access token still worked (`200`) until it expired.
- **Attack scenario:** a refresh token copied once, by XSS or from an unlocked device, gives 14 days of access. Signing out on the real device doesn't stop it. The only server-side kill switch is a password change, which the UI doesn't offer (it exists only as the unused `manage/info` endpoint).
- **Impact:** prolonged token replay. The account can read the victim's order history (names, emails, items) and place orders as them.
- **Preconditions:** theft of the refresh token.
- **Fix, in increasing effort:**
  1. Shorten the refresh lifetime (`BearerTokenOptions.RefreshTokenExpiration`, for example to 1 day).
  2. Add a sign-out endpoint that calls `UserManager.UpdateSecurityStampAsync`, and have the UI call it. That invalidates every refresh token, though access tokens still last up to an hour.
  3. Store refresh tokens server-side with rotation and reuse detection.
- **Regression test:** after sign-out, the previous refresh token returns 401.

### SA-03: Rate limits can be bypassed with IPv6 address rotation

- **Severity:** Medium. **Confidence:** high locally, medium for production. **Status:** confirmed, locally (T3). Production not tested.
- **Component:** `backend/Storefront.Api/Security/SecuritySetup.cs` lines 142–143 (the partition key is the full `RemoteIpAddress`).
- **Evidence:** 11 sign-in attempts from one IPv4 address: the 11th got `429`. 11 attempts from 11 addresses in one IPv6 /64 block: all 11 got `401`, none limited. A forged `X-Forwarded-For` was correctly ignored (`429`).
- **Attack scenario:** anyone with an ordinary IPv6 connection controls a /64 block, which is about 1.8 × 10¹⁹ addresses. Rotating through it gets past the 10-a-minute account limit and the 30-a-minute checkout limit.
- **Impact:** the per-IP limit isn't a meaningful barrier against IPv6 clients.
  - Password guessing against a single account is still capped by lockout (5 tries per 5 minutes), but that makes SA-06 worse.
  - Spraying one password across many accounts, and order spam (guest checkout), aren't capped at all.
- **Preconditions:** IPv6 access to Render's edge, and Render passing the IPv6 address in `CF-Connecting-IP`. Both are likely, but unverified.
- **Fix:** partition IPv6 clients by their /64 prefix and IPv4 clients by address. Add a per-account limit on login (keyed on the normalised email), independent of the client address.
- **Regression test:** 11 logins from different addresses in one /64 block, and the 11th gets `429`.

### SA-04: Database connections can be exhausted through public endpoints

- **Severity:** Medium (availability). **Confidence:** high locally. **Status:** confirmed, locally (T11). Production pool size unknown.
- **Component:** Npgsql's default `Maximum Pool Size` of 100; the connection string has no override in the repository. `backend/Storefront.Api/Health/HealthChecks.cs` lines 70 and 75: `/health/ready` is public, unlimited, and makes two database round trips per call.
- **Evidence:**
  - 600 unauthenticated `/health/ready` calls, 300 at a time, ran up to 60 concurrent database connections.
  - Afterwards the API's pool kept 82 idle connections open.
  - A separate admin `psql` session was refused with `FATAL: sorry, too many clients already` (local `max_connections` = 100).
- **Why it applies on Render:** Render databases with under 8 GB of RAM allow 100 connections, and its guide says to plan on about 90 usable ([Render](https://render.com/tutorials/postgres-on-render/connection-limits-and-pooling)). The default pool can take all of them by itself.
- **Attack scenario:** a modest burst of readiness or signed-in order-list requests fills the database's connections. Checkouts then fail with `DATABASE_UNAVAILABLE`, and the health check may fail. During a deploy, the new instance can't connect while the old one holds the connections, so the deploy fails. Admin access fails too.
- **Impact:** a denial of service that needs no authentication.
- **Fix:**
  - Set `Maximum Pool Size` in the connection string, for example `;Maximum Pool Size=20`, and leave headroom for a second instance during deploys and for admin sessions. This was already listed as blocker 5 in `Deployment.md`.
  - Cache the readiness result for a few seconds, or rate-limit `/health/ready`.
- **Regression test:** a readiness burst never exceeds the configured pool, and a psql session can still connect.

### SA-05: Account enumeration

- **Severity:** Low. **Confidence:** high. **Status:** confirmed, locally (T1).
- **Evidence:** three separate signals reveal whether an email has an account:
  - Registering an existing email returns `DuplicateUserName` and `DuplicateEmail`.
  - Login takes a median 3.5 ms for an unknown email and 78 ms for a known one, because the password is only hashed for real accounts.
  - After 5 failures, the detail changes from `Failed` to `LockedOut`.
- **Attack scenario:** build a list of the shop's customers, for phishing or for targeted lockout (SA-06).
- **Fix:**
  - Run a dummy hash for unknown users.
  - Return `Failed` for locked-out accounts.
  - For registration, either accept the residual risk (it's common) or answer duplicates generically. Identity has no built-in option for that.
- **Regression test:** the timing difference stays under about 20 ms, and the lockout detail equals `Failed`.

### SA-06: Anyone can lock a known account

- **Severity:** Low. **Confidence:** high. **Status:** confirmed, locally (T1).
- **Component:** `SecuritySetup.cs` lines 47–48 (5 failures, 5 minutes); login is called with `lockoutOnFailure: true`.
- **Evidence:** 5 wrong passwords from another client, and the owner's correct password then got `401 LockedOut`.
- **Attack scenario:** an attacker keeps a known customer locked out indefinitely, with about 1 request a minute. Spread across several accounts, it can block sign-in for many customers. SA-03 removes the per-IP brake.
- **Fix:** replace the hard lockout with progressive delays, or lock out per account and client address, and combine it with the per-account limit from SA-03.
- **Regression test:** failed attempts from one client don't stop the owner signing in from another.

### SA-07: No email verification and no account recovery

- **Severity:** Low. **Confidence:** high. **Status:** confirmed (T1, and by design: `RequireConfirmedAccount = false`, no email sender).
- **Impact:**
  - Anyone can register any email address, which blocks the real owner from registering it.
  - A forgotten password can't be recovered (`forgotPassword` returns 200 and sends nothing).
  - Together with SA-01, a locked-out account can't be recovered at all.
- **Fix:** configure an `IEmailSender`, require confirmation, and offer a reset in the UI. Or, until then, document that accounts can't be recovered.

### SA-08: An unused cookie sign-in mode is live

- **Severity:** Low. **Confidence:** high. **Status:** confirmed, locally (T6).
- **Evidence:** `POST /api/account/login?useCookies=true` set `.AspNetCore.Identity.Application` (`HttpOnly`, `SameSite=Lax`, valid for 14 days). With only that cookie:
  - `/api/account/manage/info` → `200`;
  - checkout → the order was **linked to the account**;
  - `/api/orders` → `401` (that policy accepts bearer tokens only).
- **Why it isn't exploitable today:**
  - `SameSite=Lax` stops browsers sending the cookie on cross-site POST requests (the `onrender.com` subdomains count as separate sites);
  - JSON bodies need a CORS preflight, which is refused for other origins;
  - `text/plain` gets `415`.
- **Risk:** extra attack surface. Any future endpoint that accepts simple requests, or a same-site host, would bring CSRF risk with it.
- **Fix:** turn off cookie sign-in. Reject `useCookies` and `useSessionCookies`, or use the explicit endpoints from SA-01, and make the default authentication scheme the bearer scheme.
- **Regression test:** `login?useCookies=true` sets no cookie.

### SA-09: Authenticated responses aren't marked `no-store`

- **Severity:** Low. **Confidence:** high. **Status:** confirmed (T1).
- **Evidence:** `/api/account/login` (tokens) and `/api/orders` (personal data) send no `Cache-Control` header.
- **Risk:** Shared caches don't store responses to requests that carry `Authorization`, but browsers or intermediaries could keep personal data on disk.
- **Fix:** `Cache-Control: no-store` for `/api/account/*` and `/api/orders*`, for example in `SecurityHeadersMiddleware`.

### SA-10: Account endpoints accept bodies up to Kestrel's 30 MB default

- **Severity:** Low. **Status:** confirmed (T4).
- **Evidence:** a 5 MB registration body was parsed and validated. Checkout alone has a 64 KB limit.
- **Risk:** together with SA-03, this raises the memory and CPU cost each abusive request can impose.
- **Fix:** set a global `MaxRequestBodySize` (for example 64 KB) in Kestrel or with a `RequestSizeLimit` on the account group.

### SA-11: The production runtime patch level can't be verified

- **Severity:** Low. **Status:** not validated.
- **Component:** `backend/Dockerfile` lines 17 and 32 (floating `sdk:10.0` and `aspnet:10.0` tags); no runtime version in the logs.
- **Evidence:**
  - Data Protection, which signs every token, is part of the ASP.NET Core runtime, not a package. `deps.json` has no `Microsoft.AspNetCore.DataProtection` package, and the runtime settings allow any 10.0.x.
  - CVE-2026-40372 (CVSS 9.1) let forged Data Protection payloads pass on Linux in 10.0.0–10.0.6, fixed in 10.0.7 ([Security Boulevard](https://securityboulevard.com/2026/04/asp-net-core-privilege-escalation-vulnerability-cve-2026-40372/)).
  - The Render build log showed the runtime layers as `CACHED`, so later base-image patches may not be picked up.
  - The first Render build (24 October) almost certainly pulled 10.0.7 or later, but this is unconfirmed.
- **Fix:**
  - Log `RuntimeInformation.FrameworkDescription` at startup.
  - Run "Clear build cache & deploy" monthly, or pin base images by digest and let Dependabot or Renovate update them.

### SA-12: The static site has no security headers

- **Severity:** Low. **Status:** not validated in production.
- **Evidence:** `Deployment.md` section 10 lists headers as optional, and none are configured in the repository. The API sends `nosniff`, `DENY`, a CSP and `no-referrer`, but the site serving the JavaScript sends none of these, as far as the repository shows.
- **Risk:**
  - No CSP means a future XSS bug would run unrestricted, and could steal the refresh token (SA-02) or enable SA-01.
  - No `frame-ancestors` or `X-Frame-Options` allows the shop to be framed (clickjacking).
- **Fix (Render, Headers, path `/*`):**
  - `X-Content-Type-Options: nosniff`
  - `Referrer-Policy: strict-origin-when-cross-origin`
  - `X-Frame-Options: DENY`
  - a tested CSP such as `default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' https://fakestoreapi.com data:; connect-src https://datheon-task-4.onrender.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self'`

  Check the CSP against the browser tests before enforcing it, or start with `Content-Security-Policy-Report-Only`.

### SA-13: Database privileges and TLS mode

- **Severity:** Low. **Status:** not validated. These are dashboard settings.
- **Privileges:** on Render, the app very likely connects as the database's default owner role, the same role used for migrations. That role can drop or alter tables, so an application compromise would have full schema control. `Deployment.md` section 3 describes least-privilege grants, but there's no evidence they're in use on Render.
- **TLS:** the documented connection string uses `SSL Mode=Require`, which encrypts but **doesn't verify the server's certificate**.
- **External access:** Render databases accept external connections from any IP by default.
- **Fix:**
  - Create a runtime role with data-only grants (section 3, step 5).
  - Use `VerifyFull` where the provider's CA is available, or rely on Render's private network.
  - Restrict external access to your own IP, or close it when you're not migrating.

### SA-14: Responses from FakeStore are trusted without size or URL checks

- **Severity:** Low. **Status:** confirmed by code review.
- **Component:** `FakeStoreClient.cs` line 88 (`ReadAsStringAsync`, with no size cap); `ProductCatalogService.cs` line 208 (image URLs passed through as they are).
- **Risk:** a compromised or hijacked upstream could send a huge body and exhaust memory on a small instance, or serve tracking or `http://` images. Prices come only from upstream too, so it could set prices, though no payment is taken.
- **Fix:** cap upstream responses (`MaxResponseContentBufferSize`, for example 2 MB, or stream with a limit), and accept only `https:` image URLs.

### Informational

- **SA-15. Image contents.** The published output includes things production doesn't need:
  - `appsettings.Development.json`: with `ASPNETCORE_ENVIRONMENT=Development` set by mistake, production would switch to SQLite with migrations on startup, Swagger and unencrypted keys;
  - SQLite native libraries for every platform (33 MB);
  - Swashbuckle;
  - debug symbols (`.pdb`).

  Exclude `appsettings.Development.json` in `.dockerignore`, and consider trimming the rest.
- **SA-16. Logging.**
  - Every handled idempotency race writes two Error-level entries with a stack trace (662 Error lines during T7). That's noise that hides real errors.
  - Failed logins and lockouts produce no security-specific log events, and nothing alerts on them.
  - Logs are otherwise clean (T12).
- **SA-17. Data and recovery.**
  - The Free database has no backups and expires after 30 days.
  - Orders keep customers' names and emails indefinitely.
  - There's no way to delete an account or erase its data.
- **SA-18. Delivery pipeline.**
  - The repository has no CI, so a push to `main` deploys without running tests.
  - Branch protection, the repository's visibility, and 2FA on the GitHub and Render accounts couldn't be checked.
- **SA-19. HTTP to HTTPS.** The API's own redirect can't determine the HTTPS port (a warning is logged), so redirecting plain HTTP is left to Render's edge. HSTS is sent correctly over HTTPS (T5b).
- **SA-20. Deployment details in docs.** `Deployment.md` names the service URLs, the CORS origin and the live certificate's thumbprint. None of these are secrets, but if the repository is public, they map the deployment for anyone.

---

## 7. Positive controls verified

| Control | Evidence |
|---|---|
| Server-side pricing; client prices and IDs ignored | `price`, `unitPrice`, `userId` and `total` in the body had no effect; mismatched `expectedTotal` → 409 with a server quote (T4) |
| Input limits | Negative, zero, over-99 and 2³¹ quantities; duplicate lines; 51 lines; negative IDs; 1e30 totals; nested JSON; 70 KB bodies; form bodies → all rejected, none 500 (T4) |
| Idempotency under concurrency | T7: 20 rounds of 100 simultaneous identical requests: 100 × 200, one reference, 99 replays, 1 row. T8: one key, two orders: 1 row, 50 × 200 and 50 × 422. T9: a guest and an account on one key: 1 row, the other side all 422, never the other's order. T10: 200 different keys: 200 rows |
| Crash consistency | T10b: 8 rounds of `kill -9` during a burst of 80 orders; 5 landed mid-burst. Mixed outcomes (for example 1 confirmation, 14 committed rows), then every key retried: 80 orders per 80 keys, committed orders replayed with their original reference. 0 orphan lines, 0 orders without lines, 0 duplicate keys, 0 totals that don't add up |
| Fault injection at COMMIT | 20 of 20 existing PostgreSQL tests pass, including before, during and after COMMIT and a commit that succeeded but reported an error |
| Order isolation | Another account → 404; reusing another owner's key → 422; anonymous → 401; forged token → 401; checkout with an invalid token → 401, never a silent guest order (T1) |
| CORS | Exact origin only. `null`, look-alike suffixes, `http://` and localhost all refused. No credentials. Methods `GET` and `POST` only (T5) |
| Forwarded headers | `CF-Connecting-IP` used; forged `X-Forwarded-For` ignored (T3); unknown `Host` → 400 (T5) |
| API headers | `nosniff`, `X-Frame-Options: DENY`, `no-referrer`, CORP, CSP `default-src 'none'`; HSTS (1 year, subdomains) over HTTPS (T5, T5b) |
| No information leaks | 404 and 400 problem bodies carry no stack traces or SQL; Swagger, `/.env`, `appsettings.json` and the DLL all 404; health returns status words only (T5) |
| Log safety | Correlation IDs must match `^[A-Za-z0-9._-]{1,64}$`; route patterns are logged rather than raw paths; JSON console format; EF parameters show as `?` and PostgreSQL error detail is redacted; no emails, names, passwords, tokens or connection strings in 4,128 lines (T12) |
| Key encryption | Startup log names the certificate; key rows are `<encryptedSecret>` (also verified on Render earlier today) |
| Password hashing and policy | Identity v3 (PBKDF2); length 8 with digit, upper case and symbol (T1) |
| Secrets in history | 0 private keys, certificates, JWTs, bearer tokens, cloud tokens or Render hosts; no `.pfx`, `.db` or `.env` file ever committed. 9 hits were placeholders, identifiers or string literals (T13) |
| Frontend | No `dangerouslySetInnerHTML`, `innerHTML` or `eval`; all API calls through one client; production bundle has the API address only, no source maps, no `localhost`, the same hash as deployed; `npm audit` 0 vulnerabilities (runtime dependencies: React and React DOM only) |
| No raw SQL | No `FromSql`, `ExecuteSql`, raw commands or migration SQL |
| Container | Non-root (`USER $APP_UID`), CA refresh, `exec` entrypoint, design-time tooling not in the publish output (T14) |
| Migrations | Never automatic in production (`ApplyMigrationsOnStartup` false); readiness reports pending migrations |

---

## 8. Checks not completed, and why

| Check | Reason | How to complete it |
|---|---|---|
| Any active test against production | Render isn't reachable from the workspace or the shell on the owner's computer, and none was authorized | Run the passive checks in section 11; authorize specific active tests if wanted |
| `dotnet list package --vulnerable --include-transitive` | nuget.org is blocked here | Run in `backend/` on your machine |
| Container image scan | No Docker here | `docker build -t storefront-api backend`, then `docker scout cves storefront-api` or `trivy image storefront-api` |
| OWASP ZAP baseline | No reachable authorized target | `docker run --rm -t ghcr.io/zaproxy/zaproxy:stable zap-baseline.py -t https://datheon-task-4-1.onrender.com` (passive), with your approval |
| Runtime version in production | Not logged; no shell on Render Free | See SA-11 |
| IPv6 behaviour on Render | Needs active tests from IPv6 | With approval: two sign-in requests from different addresses in one /64 |
| Static-site response headers | Not reachable | `curl.exe -sI https://datheon-task-4-1.onrender.com/` |
| Browser test with a hostile upstream (script in titles, `javascript:` image URLs) | Not built in this phase | Add to the Playwright suite with the mock |
| SAST beyond the compiler, and a frontend linter | Not configured in the repository | For example `dotnet format analyzers`, Semgrep, ESLint with security plugins |
| GitHub repository visibility | GitHub returned 403 to this workspace for every page | Check in GitHub settings |

## 9. Dismissed and false positives

| Candidate | Why it was dismissed |
|---|---|
| Cross-user idempotency replay | Replay requires the same owner (`CheckoutService.cs` line 155); verified for account against account and guest against account (T1, T9) |
| A second guest replaying a guest's key with identical details | Returns the original order (by design). It needs the client's random key **and** the exact name, email and items; the only new information revealed is the reference and totals |
| CSRF through the Identity cookie | Blocked by `SameSite=Lax`, the JSON requirement and CORS (T6); recorded as hardening in SA-08 |
| SQL injection | No raw SQL; LINQ only; injection-looking inputs → 400 or empty results |
| Stored XSS through customer name | Stored as text; React escapes it; the API returns JSON with a `default-src 'none'` CSP |
| Log injection | Correlation IDs pattern-checked; JSON log format; route patterns rather than raw paths |
| CVE-2024-32655 (Npgsql protocol size overflow) | Fixed in Npgsql 8.0.3; the project uses 10.0.3 ([advisory list](https://vulert.com/vuln-db/ghsa/npgsql)) |
| CVE-2026-40372 in the referenced packages | The ASP.NET packages are 10.0.13 (≥ 10.0.7); the runtime is covered by SA-11 |
| Secret-scan hits | Placeholders, identifiers and string literals (T13) |
| `Cross-Origin-Resource-Policy: same-site` breaking the site | CORP doesn't apply to CORS `fetch`; the live site works |

---

## 10. Remediation plan

**Immediate (small changes):**
1. **SA-04:** add `;Maximum Pool Size=20` to `ConnectionStrings__Storefront` on Render. This is a dashboard-only change.
2. **SA-01 and SA-08:** expose only register, login and refresh, and turn off cookie sign-in.
3. **SA-09:** `Cache-Control: no-store` on account and order responses.
4. **SA-12:** static-site headers (start the CSP in report-only mode).

**Before the next release:**
5. **SA-02:** server-side sign-out (update the security stamp) and a shorter refresh lifetime.
6. **SA-03 and SA-06:** IPv6 /64 partitioning, a per-account login limit, and progressive delays instead of a hard lockout.
7. **SA-05:** dummy hash for unknown users; uniform `Failed` detail.
8. **SA-10, SA-14:** a global body limit; upstream size cap and `https:` image URLs.
9. **SA-11, SA-15:** log the runtime version; exclude `appsettings.Development.json`; clear the build cache periodically or pin images by digest.
10. **SA-13:** a least-privilege runtime role, and restricted external database access.

**Longer-term hardening:**
11. **CI:** build, tests, `npm audit`, `dotnet list package --vulnerable`, secret scan and container scan, with branch protection on `main` (SA-18).
12. Security events and alerting for failed sign-ins, lockouts and rate-limit spikes; lower the log level of handled races (SA-16).
13. Email verification and recovery (SA-07); retention and deletion of customer data; a paid database with backups (SA-17).
14. Keep the current token storage for now. A cookie session (BFF) would remove the token from JavaScript, but it needs CSRF protection and a shared site. The two `onrender.com` subdomains are separate sites, so this would need a custom domain. With SA-02 and SA-12 fixed, the current approach is proportionate for this app.

Every fix needs the regression test listed with its finding. The probe scripts from this audit can be turned into xUnit or Playwright tests in the remediation phase.

## 11. Production checks for you to do manually

**Render, API service (`datheon-task-4`), Environment:**
- [ ] `ConnectionStrings__Storefront` has `Maximum Pool Size` (SA-04). Which role does it use, and which `SSL Mode` (SA-13)?
- [ ] `ASPNETCORE_ENVIRONMENT` is unset or `Production`, never `Development` (SA-15).
- [ ] No `DataProtection__AllowUnencryptedKeys`; no `RateLimiting__*` overrides left over.
- [ ] `AllowedHosts=datheon-task-4.onrender.com`; `Cors__AllowedOrigins__0=https://datheon-task-4-1.onrender.com`, and no other `Cors__AllowedOrigins__*`.
- [ ] `ForwardedHeaders__Enabled=true`, `ClientAddressHeader=CF-Connecting-IP`, `TrustedProxyHops=1`.

**Render, database:**
- [ ] Note the expiry date, and that there are no backups (SA-17).
- [ ] Restrict inbound access (Access Control) to your own IP, or close it (SA-13).

**Render, Static Site (`Datheon-Task-4-1`):**
- [ ] The rewrite `/*` → `/index.html` exists.
- [ ] Add headers (SA-12).

**Render, account and GitHub:**
- [ ] 2FA on both accounts.
- [ ] Who has access to the Render workspace.
- [ ] Auto-deploy settings.
- [ ] Repository visibility, branch protection on `main`, and Dependabot alerts (SA-18).

**Passive checks from PowerShell.** These only read responses:
```powershell
curl.exe -sI https://datheon-task-4.onrender.com/api/store/config      # headers: nosniff, DENY, CSP, HSTS
curl.exe -sI https://datheon-task-4-1.onrender.com/                     # static site headers (SA-12)
curl.exe -sI http://datheon-task-4.onrender.com/health/live             # expect a redirect to https (SA-19)
curl.exe -s -o NUL -w "%{http_code}`n" https://datheon-task-4.onrender.com/swagger/index.html   # expect 404
```

## 12. Commands and results

All results are from 24 October 2026. "Local" means the audit workspace: PostgreSQL 16.15, .NET runtime 10.0.12 and SDK 10.0.401, Node 22.22.0. Probes used synthetic accounts and passwords, which aren't reproduced here.

| # | Command or test | Result |
|---|---|---|
| B1 | `git status`, `git log`, file checksums (owner's machine) | Clean on `b897c34`; 11 commits; audit copy identical |
| B2 | `dotnet test -c Release` with `STOREFRONT_TEST_POSTGRES` set | **329 passed, 0 failed** |
| B3 | `dotnet test -c Release --filter Category=PostgreSql` | **20 passed, 0 failed** |
| B4 | `npm run typecheck`; `npx vitest run` | Passed; **279 passed** in 24 files |
| B5 | Playwright, dev server and production build (earlier on this tree, same day) | **18 of 18** each |
| B6 | `npm audit` / `npm audit --omit=dev` | 0 vulnerabilities / 0 |
| B7 | `dotnet publish -c Release` and inspection | 43 MB; design-time tooling absent; SA-15 items present |
| T1 | Authentication probes: enumeration, timing, lockout, token lifecycle, password change, object-level authorization | See SA-02, SA-05, SA-06, SA-07; order isolation held |
| T2 | 2FA with a token only | Enabled; owner gets `RequiresTwoFactor` (SA-01) |
| T3 | 11 sign-ins: one IPv4 / 11 IPv6 in one /64 / forged `X-Forwarded-For` | 10 × 401 then 429 / 11 × 401 / 429 |
| T4 | Malformed, oversized and hostile inputs (25 cases) | All 4xx and none 500; 5 MB register body accepted (SA-10) |
| T5 | Headers, Swagger, file probes, CORS (7 origins), methods, `Host` | As in section 7; `Server: Kestrel`; no `Cache-Control` (SA-09) |
| T5b | Second instance with a real host name | HSTS sent over HTTPS; no API-level HTTP redirect (SA-19) |
| T6 | Cookie sign-in and CSRF preconditions | SA-08 |
| T7–T10 | `stress_checkout.py`: A 20 × 100 same key; B two orders on one key; C guest against account; D 200 keys; E no key | All as expected (section 7) |
| T10b | `crash_test.py`, 8 rounds of `kill -9` (5 mid-burst) | No duplicates or partial orders; invariants 0/0/0/0 |
| T11 | 600 `/health/ready`, 300 at a time | 600 × 200, peak 60 connections, 82 left idle, `psql` refused with "too many clients" (SA-04) |
| T12 | Scan of local API logs (4,128 lines) for emails, names, passwords, tokens, connection strings | 0 of each |
| T13 | Secret scan of `git log -p --all` (9 rules) and sensitive file extensions | 0 real secrets; 9 dismissed hits |
| T14 | Data Protection version source (`deps.json`, runtime settings) | Comes from the runtime; SA-11 |

There were no failed commands, apart from tooling hiccups that were fixed and re-run: a URL-encoding error in a probe script, and a missing `ss` binary. No test assertion failed unexpectedly.

The probe scripts (`probe_auth.py`, `probe_2fa.py`, `probe_http.py`, `stress_checkout.py`, `crash_test.py`) were kept in the audit workspace, not in the repository.

## 13. What was and wasn't tested against production

- **Tested by this audit:** nothing. No request was sent to either Render service, and no production data, account or setting was read or changed.
- **Production evidence from earlier the same day, run by the owner:** the CORS preflight, the live checklist, and the key-encryption check. These are cited where relevant, but they aren't part of this audit's testing.
- **Applies to production by inference:** every confirmed local finding uses the same code and the same production settings. Whether SA-03 and SA-04 can actually be exploited live depends on Render's IPv6 handling and on the pool-size setting, which are listed in section 11.

---

## 14. Remediation (24 October 2026)

This section was added after the audit. Sections 1–13 above are the original findings and evidence, kept as they were. Everything here was done in the working tree and tested locally. **Nothing has been committed, pushed or deployed, and no production setting, secret or data was touched.** The manual production steps are in 14.5 and in `Deployment.md`, section 11.

### 14.1 Status by finding

| ID | Status | What changed |
|---|---|---|
| SA-01 | **Fixed** | `MapIdentityApi` is replaced by `Security/AccountEndpoints.cs`, which maps exactly five routes: `register`, `login`, `refresh`, `logout` and `GET manage/info`. 2FA, `POST manage/info` (email and password change), `forgotPassword`, `resetPassword`, `confirmEmail` and `resendConfirmationEmail` no longer exist (404/405). Dependencies were checked first: the frontend uses only `register`, `login`, `refresh` and `GET manage/info`, so `GET manage/info` was kept. Any 2FA flag already set is ignored, because login no longer goes through the 2FA path; the check query in 14.5 confirms none was set in production. |
| SA-02 | **Fixed** | Server-side sessions (`Security/Sessions.cs`, table `UserSessions`, migration `AddUserSessions`). Every token carries a session ID; every request checks the session in the database before authentication succeeds (401 otherwise). Sign-out (`POST /api/account/logout`, now called by the frontend) ends the session for both tokens. Refresh tokens are single-use with rotation: the token just replaced is honoured for 30 seconds so a lost response can be retried, and any later reuse ends the whole session. A password or security-stamp change ends all of the account's sessions on the next request. A session never lives past 14 days from sign-in, however often it's refreshed, and an account keeps at most 10 active sessions. Revocation is in the database, so it survives restarts and applies to every instance. |
| SA-03 | **Fixed** | `Security/ClientPartition.cs`: an IPv6 client is its /64; IPv4-mapped addresses count as IPv4. Used by every rate-limit policy and by sign-in protection. The trusted `CF-Connecting-IP` handling is unchanged. |
| SA-04 | **Fixed in code**; configuration optional | (1) The pool is capped in code: `Database:MaxPoolSize` (default 20, range 1–100) applies when the connection string doesn't set `Maximum Pool Size`/`MaxPoolSize`; an explicit value in the string wins. The startup log shows `(connection pool up to N)`. (2) Readiness is single-flight (`DatabaseReadinessProbe`): simultaneous `/health/ready` calls share one database check, and a caller that disconnects doesn't cancel it for the others. Optional `Health:ReadinessCacheSeconds` reuses a recent result (off by default). The probe runs the same two queries as before, so its meaning for Render's health check is unchanged. |
| SA-05 | **Partly fixed** | Login: unknown emails are hashed against a dummy hash, and every failure returns the same 401 `Failed` body. Measured medians: unknown 84.9 ms, known 69.2 ms (before: 3.5 ms and 78 ms). **Residual:** registration still answers `DuplicateEmail` for a taken address. Removing that changes the frontend/backend contract and needs an email flow (SA-07), so it was left; see 14.4. |
| SA-06 | **Fixed** | `Security/SignInThrottle.cs` replaces Identity lockout (`Lockout.AllowedForNewUsers=false`; `CheckPasswordAsync` is used, so failures no longer write `LockoutEnd`). 5 failures for one account from one client, or 20 for one account from all clients, within 15 minutes → 429 with `Retry-After`, answered before the password is checked. An attacker on one network can't block the owner on another. Accounts are keyed by a hash of the normalised email, so no email is held in memory in clear. |
| SA-07 | **Open** | Needs an email provider and a product decision (verification, recovery). See 14.4. |
| SA-08 | **Fixed** | Cookie sign-in no longer exists: only the bearer scheme is registered (`AddIdentityCore` + `AddBearerToken`), `useCookies` is ignored, no `Set-Cookie` is ever sent, and an Identity cookie authenticates nothing. |
| SA-09 | **Fixed** | `Cache-Control: no-store` on every `/api/account`, `/api/orders` and `/api/checkout` response, including errors. Public catalogue responses keep their caching. |
| SA-10 | **Fixed** | Account endpoints refuse bodies over 16 KiB with 413 `REQUEST_TOO_LARGE`, before reading them. Kestrel's global limit is lowered to 64 KiB; checkout keeps its 64 KiB limit. |
| SA-11 | **Fixed (logging)**; cache step manual | The startup log line now names the exact runtime (`on .NET 10.0.12` locally). Keeping the runtime patched still depends on Render rebuilding the image: see 14.5. |
| SA-12 | **Fixed in code**; dashboard step manual | `frontend/src/config/securityHeaders.ts` defines the headers and CSP. `vite preview` serves them, so the browser tests run the production bundle under the exact policy and fail on any CSP violation. The Render Static Site needs the same headers added by hand (`Deployment.md`, section 10). |
| SA-13 | **Open (manual)** | Database role, TLS mode and inbound access are Render settings. See 14.5. |
| SA-14 | **Fixed** | FakeStore responses over `FakeStore:MaxResponseKilobytes` (2,048) are refused, whether or not they declare a length. Image URLs must be absolute `https://` without user info; anything else becomes empty. `http://` is allowed only when FakeStore's own base URL is `http://` (the local mock). |
| SA-15 | **Partly fixed** | `appsettings.Development.json` is excluded from the image (`backend/.dockerignore`). The SQLite native libraries, Swashbuckle and `.pdb` files are still published; trimming them is optional. |
| SA-16 | **Partly fixed** | Failed sign-ins are now logged (Information) and sign-in blocks and refresh-token reuse as warnings, with a hashed account key and the client partition, never the email, password or token. The Error-level noise from handled idempotency races was not changed (it's in the checkout path, which this phase didn't touch). No alerting exists. |
| SA-17 | **Open (manual)** | Backups, retention and account deletion: product and hosting decisions. |
| SA-18 | **Open (manual)** | CI, branch protection and account 2FA are GitHub and Render settings. |
| SA-19 | **No change** | Render's edge redirects HTTP; HSTS is correct. |
| SA-20 | **No change** | Depends on whether the repository is public. The owner should decide. |

### 14.2 Files changed

**Backend, new:**
- `Security/AccountEndpoints.cs`, `Security/Sessions.cs`, `Security/SignInThrottle.cs`, `Security/ClientPartition.cs`
- `Data/Entities/UserSession.cs`, `Data/Configurations/UserSessionConfiguration.cs`
- `Data/Migrations/20261024190032_AddUserSessions.cs` and `.Designer.cs` (SQLite); `Data/Migrations/PostgreSql/20261024190035_AddUserSessions.cs` and `.Designer.cs`

**Backend, changed:** `Program.cs`, `Security/SecuritySetup.cs`, `Security/SecurityHeadersMiddleware.cs`, `Health/HealthChecks.cs`, `Data/DatabaseSetup.cs`, `Data/StorefrontDbContext.cs`, both model snapshots, `Configuration/FakeStoreOptions.cs`, `Services/FakeStoreClient.cs`, `Services/ProductCatalogService.cs`, `backend/.dockerignore`.

**Backend tests, new:** `Integration/AccountEndpointTests.cs`, `Integration/SessionTests.cs`, `Integration/SignInProtectionTests.cs`, `Integration/ConnectionBudgetTests.cs`, `Integration/CacheControlTests.cs`, `Integration/HardeningTests.cs`, `Configuration/ConnectionPoolTests.cs`, `Configuration/ClientPartitionTests.cs`, `FakeStore/UpstreamTrustTests.cs`.
**Backend tests, changed:** `Integration/PostgreSqlTests.cs` (the old lockout test became a sign-in protection, sessions and order-history test; a new pool-cap test under load), `Configuration/DatabaseProviderTests.cs` (description now includes the pool cap).

**Frontend, new:** `src/config/securityHeaders.ts`, `src/config/securityHeaders.test.ts`, `e2e/security-headers.spec.ts`.
**Frontend, changed:** `src/services/accountService.ts` (`logout`), `src/context/SessionContext.tsx` (sign-out ends the server session), `src/services/apiClient.ts` (long `Retry-After` shown in minutes), `src/test/fixtures.ts`, `src/context/SessionContext.test.tsx`, `src/services/apiClient.test.ts`, `src/utils/authErrors.test.ts`, `e2e/shopper-journeys.spec.ts` (after sign-out, the old refresh token gets 401), `vite.config.ts`, `tsconfig.node.json`, `playwright.config.ts`.

**Docs:** `README.md`, `Documentation/Backend.md`, `Frontend.md`, `Testing.md`, `Availability.md`, `Deployment.md` (new settings, required static-site headers, section 11 rollout), and this section.

### 14.3 Test evidence

Regression tests, by finding. Each was confirmed to fail against the unfixed code (mutation check) before being accepted:

| Finding | Tests | Mutation check |
|---|---|---|
| SA-01, SA-08 | `AccountEndpointTests` (8): exactly five routes; management and email flows unreachable with a valid token; a stolen token can't enable 2FA; `manage/info` needs a bearer token; no cookie on login; an Identity cookie doesn't authenticate; error contract unchanged; refresh works and rejects garbage | Restoring `MapIdentityApi` and the cookie scheme: 10 of 14 tests fail |
| SA-02 | `SessionTests` (13): sign-out ends both tokens; a revoked token can't order as account or guest; single-use refresh and reuse detection; grace retry; parallel refreshes; password change; pre-session tokens refused; 14-day cap; 10-session cap; revocation survives a new instance; other shoppers unaffected. Frontend: 4 `SessionContext` tests; e2e sign-out check | Sessions, reuse detection, /64 partitioning and the dummy hash mutated together: 15 tests fail; removing the frontend `logout` call: caught |
| SA-03, SA-05, SA-06 | `SignInProtectionTests` (9), `ClientPartitionTests` (6); `PostgreSqlTests.Accounts_sign_in_protection_sessions_and_order_history_work_on_postgresql` | (as above) |
| SA-04 | `ConnectionPoolTests` (5), `ConnectionBudgetTests` (4), `PostgreSqlTests.A_burst_of_requests_never_uses_more_connections_than_the_pool_cap` | Removing single-flight: caught |
| SA-09 | `CacheControlTests` (2) | Removing `no-store`: caught |
| SA-10, SA-11 | `HardeningTests` (7, on real Kestrel) | — |
| SA-12 | `securityHeaders.test.ts`; `e2e/security-headers.spec.ts` (3: headers served, a whole journey with no CSP violation, framing refused) | Removing the preview headers: caught |
| SA-14 | `UpstreamTrustTests` (13) | — |

Full suites on the final tree (local: PostgreSQL 16.15, .NET 10.0.12, Node 22.22.0; disposable databases and synthetic accounts):

| # | Command | Result |
|---|---|---|
| R1 | `dotnet build -c Release` (in `backend/`) | 0 warnings, 0 errors |
| R2 | `dotnet test -c Release` with `STOREFRONT_TEST_POSTGRES` set | **416 passed, 0 failed, 0 skipped** (was 329) |
| R3 | `dotnet test -c Release --filter Category=PostgreSql` | **21 passed, 0 failed** (was 20) |
| R4 | `npm run typecheck`; `npx vitest run` (in `frontend/`) | Passed; **299 passed** in 25 files (was 279 in 24) |
| R5 | `npm run build` | Built; no source maps, no `localhost` in the bundle |
| R6 | `npm audit` | 0 vulnerabilities |
| R7 | `npx playwright test` (dev server) | **18 passed, 3 skipped**: the 3 header tests need the production build, by design |
| R8 | `E2E_PRODUCTION_BUILD=1 npx playwright test` | **21 passed** |
| R9 | Audit probes repeated on the fixed build (`probe_after.py`) | Removed routes 404/405; `useCookies` sets no cookie; `no-store` present; after logout both tokens 401 and refresh 401; 11 logins from one /64 → 11th 429; attacker blocked on the 6th attempt (`Retry-After: 900`) while the owner signs in from another network (200) |
| R10 | T11 repeated: 1,200 `/health/ready` calls | 0 extra connections (19 before, 19 at peak, cap 20) |
| R11 | `stress_checkout.py` A–E and `crash_test.py` (5 mid-burst rounds) | Unchanged from the audit: no duplicates or partial orders; invariants 0/0/0/0 |
| R12 | Migration rehearsal on a copy at the production migration level: `dotnet ef migrations script --idempotent`, applied twice | First run adds only `UserSessions`; second run changes nothing |

Checkout idempotency, transactions, crash recovery, order authorization and server-side pricing are unchanged: the checkout code wasn't edited, and its existing tests, the stress tests and the crash test all pass.

Corrections to earlier working notes: a suspicion that EF's `CanConnect` opened unpooled connections was disproved with `pg_stat_database.sessions`; the extra connections in one test came from the test helper's `Pooling=false`.

### 14.4 Unresolved issues and remaining risks

- **SA-05 residual:** registration reveals that an email is taken. Fixing it needs SA-07 (an email-confirmation flow), and it changes the API contract. The rate limit still slows bulk checks.
- **SA-06 residual:** 20 failures from many networks block an account for up to 15 minutes. This is temporary, unlike the old lockout, but a distributed attacker can repeat it.
- **Sign-in protection is in memory:** counts reset on restart and aren't shared between instances. That's fine on one Render instance; with more than one, move it to the database or Redis.
- **Sessions need the database on every authenticated request:** one indexed lookup. If the database is down, authenticated calls fail with 401 instead of working on a token alone. This is the price of real revocation.
- **Refresh reuse ends the session:** two tabs that copy the same stored tokens could trigger it after the 30-second grace. The frontend shares one session across tabs, so this hasn't been seen in tests.
- **Deploy impact:** every token issued before this change is refused (it has no session), so every signed-in shopper signs in once more.
- **Not done:** SA-07, SA-13, SA-15 (trimming), SA-16 (race log level, alerting), SA-17, SA-18, SA-20; and the checks in section 8 that need tools or access this workspace doesn't have (package vulnerability scan, container scan, ZAP, live IPv6 test).

### 14.5 Manual production steps

*Steps 1–5 were done by the owner on 25 October 2026; see 14.6. Steps 6 and 7 are still open.* In this order; the details are in `Deployment.md`, section 11.
1. Apply the `AddUserSessions` migration to the production database **before** pushing (idempotent script, with the connection string entered via `Read-Host`).
2. Run the 2FA check query; it should return 0.
3. Push to `main`, so Render deploys. Every signed-in shopper has to sign in once more.
4. Add the response headers to the Static Site (`Deployment.md`, section 10).
5. Run the verification `curl` commands (`Deployment.md`, section 11).
6. Now and then, run "Clear build cache & deploy" on the API service, so the runtime gets patched (SA-11); the startup log shows the version.
7. Earlier open items: a least-privilege role and `SSL Mode=Require`, and restricted inbound database access (SA-13); 2FA on GitHub and Render, and branch protection (SA-18); backups (SA-17).

### 14.6 Production rollout (25 October 2026)

Done by the owner, in the order of 14.5; results reported back and recorded here. No secrets were shared.

| Step | Result |
|---|---|
| Pre-check: `__EFMigrationsHistory` before applying | `20261023135229_InitialCreate`, `20261023192043_AddDataProtectionKeys`: the PostgreSQL migration set, so the right database |
| Idempotent script applied with `psql -v ON_ERROR_STOP=1` | No errors; the two existing migrations were skipped; one transaction added `UserSessions` |
| History after applying | 3 rows, the last `20261024190035_AddUserSessions` |
| 2FA check (`TwoFactorEnabled`) | `0` |
| Commit `cb809d2` pushed; API deploy log | `Using PostgreSQL … (connection pool up to 20)`; `Storefront API started in Production on .NET 10.0.13`; sign-in keys encrypted with certificate A3402286…91CB; no pending-migration warning; "Your service is live" |
| Static Site headers added (path `/*`) | `curl -sI` on the site: the exact CSP, `nosniff`, `DENY`, `strict-origin-when-cross-origin`, `same-origin`, and HSTS (Render's own, `max-age=315360000; includeSubdomains; preload`) |
| `curl -sI /api/orders` on the API | `Cache-Control: no-store` (405 for `HEAD`, as expected) |
| Browser: sign in, browse, cart, sign out, reload | Works; stays signed out after reload; no CSP violations in the console |

What this confirms in production: SA-04 (pool cap; the provider is PostgreSQL), SA-09, SA-11 (runtime 10.0.13, past the 10.0.7 fix), SA-12, and SA-02 sign-out. Still open: 14.5 steps 6–7, and the items in 14.4.

### 14.7 Follow-up: continuous integration (SA-18), 25 October 2026

SA-18 is now **fixed in code**; turning on the gating is a manual dashboard step. `.github/workflows/ci.yml` builds and tests every push and pull request:
- the backend, with all 416 tests including PostgreSQL, failing on any skipped test;
- the frontend: typecheck, tests with coverage thresholds, and the production build;
- both browser-test modes;
- the Docker image;
- a full-history secret scan.

`.github/workflows/security.yml` runs weekly and covers the checks from section 8 that couldn't run during the audit: `dotnet list package --vulnerable --include-transitive`, `npm audit`, and a Trivy scan of the container image.

Actions are pinned by commit SHA, and the scanners by checksum. The workflow token is read-only. Details and local verification are in `Testing.md`, section 2a, and the owner's steps in `Deployment.md`, section 12 (Render "After CI Checks Pass" on both services, and a ruleset on `main`).

The first full-history secret scan found one result, the sample `Idempotency-Key` UUID in `Storefront.Api.http`. It's a false positive, consistent with T13, and is listed in `.gitleaksignore`.

**SA-20:** the repository could be cloned without credentials, so it appears to be public. Nothing secret is in it (the secret scan and T13 agree), but the service URLs and the certificate thumbprint in these documents are visible to anyone. The owner should decide whether to keep it public.

### 14.8 Follow-ups on 25 October 2026: CI live (SA-18), least-privilege database role (SA-13)

**SA-18: fixed.** CI is live and gating production.
- Both Render services use **Auto-Deploy: After CI Checks Pass**.
- The `ci-bro` ruleset blocks force pushes to `main` and its deletion.
- The weekly **Security scan** passed both jobs on its first run: NuGet advisories, the full `npm audit`, and Trivy on the container image. Those were three of the checks section 8 couldn't complete.
- CI found and fixed one real defect on its first run: product title links overlapping their "Add to cart" buttons. It also exposed two timing-dependent test problems, now fixed. Details are in `Testing.md`, section 2a.

**SA-13: fixed for the role; TLS confirmed.** The API now connects as `storefront_app`, created by `tools/db/create-app-role.sql` (`Deployment.md`, section 13).
- **Access:** row access only (no DDL, no roles, no `TRUNCATE`); a read-only migration history; tables from future migrations granted automatically.
- **Limits:** 25 connections, a 30-second statement timeout, and a 60-second limit on idle open transactions.
- **Rehearsal:** done locally against a non-superuser owner, including the smoke test (68 of 68) and the checkout stress tests as `storefront_app`.
- **In production:** the API's connection is `storefront_app`, over Render's private network, with TLS 1.3. Migrations still run as the owner over the External URL with `sslmode=require`.
- **Still open (manual):** restricting inbound access to the database on Render.

**New, open: FakeStoreAPI refuses requests from Render.** On the evening of 25 October 2026, checkout failed twice with 502 `UPSTREAM_UNAVAILABLE`, because FakeStoreAPI answered 403 to the API while answering 200 elsewhere. The API failed safely, as designed: no order, the cart kept, a reference shown. It's an availability risk of depending on a third-party demo API, not a security flaw. To be retried; the options are in `Deployment.md`, section 13.
