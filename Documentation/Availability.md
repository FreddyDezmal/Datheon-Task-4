# Live availability: health checks, failure handling and diagnosis

This document covers what the storefront does when something it depends on fails, how to check whether it is up, and how to find out what went wrong.

**Scope of the evidence.** Everything marked *verified* below was exercised by automated tests or by a manual run in a Linux workspace and on a developer machine. None of it has been deployed. There is no hosting platform, load balancer, monitoring system or uptime record, so nothing here establishes high availability, production readiness or an uptime figure. Passing tests show the safeguards behave as designed, not that the live system stays up. Items marked *recommendation* need real deployment infrastructure to be done or proven.

---

## 1. Audit summary (before this work)

| Area | Finding |
|---|---|
| Health | A single `GET /health` returned a fixed `{"status":"ok"}`. It said nothing about the database, and could not tell "process running" from "able to serve". |
| Upstream (FakeStoreAPI) | One 10 s timeout and no retry: one dropped connection failed a browse request. When the 5-minute cache expired during an outage, browsing failed with 502 even though a good catalogue had loaded minutes before. Concurrent cold-cache requests each called FakeStoreAPI. |
| Database | A locked SQLite file was handled (503 `DATABASE_BUSY`). An unopenable file (missing folder, permissions) fell through to 500 `DATABASE_ERROR`, which the frontend then treated as an *uncertain* checkout. |
| Checkout safety | Already sound: no server-side retry, idempotency keys, uncertain-outcome recovery with the same key, cart kept on every failure. Preserved unchanged. |
| Frontend | No offline detection. A failed catalogue load told shoppers to "check that the API is running on the configured URL" (developer text). No way to quote an error to support. |
| Observability | Plain-text console logs. `traceId` in error bodies was the ASP.NET trace ID, not linked to anything the caller could send. Expected dependency failures were logged as errors with full stack traces. |
| Startup and shutdown | Options already validated at startup. Shutdown used the default 30 s with no signal to anything routing traffic. |

---

## 2. Health endpoints (*verified*)

| Endpoint | Question it answers | Checks | Status codes |
|---|---|---|---|
| `GET /health/live` | Is the process running and able to answer HTTP? | **None.** It never touches the database or FakeStoreAPI, so a dependency outage can't get a healthy process restarted. | 200 `Healthy` |
| `GET /health` | Same as `/health/live` (the original path, kept so existing scripts don't break). | None | 200 |
| `GET /health/ready` | Should this instance receive traffic right now? | `database`: can connect, and no migration is pending (3 s timeout). `catalogue`: did the last FakeStoreAPI call fail? (reads stored state, makes no call). `lifecycle`: has shutdown begun? | 200 `Healthy` or `Degraded`; **503** `Unhealthy` |

```bash
curl -s http://localhost:5080/health/ready
# {"status":"Healthy","checks":{"database":"Healthy","catalogue":"Healthy","lifecycle":"Healthy"}}
```

- **Catalogue is never Unhealthy, only Degraded.** During a FakeStoreAPI outage the API can still browse from its saved copy, serve order history and sign people in. Taking it out of rotation would turn a partial outage into a full one.
- **Database Unhealthy → 503.** Nothing that writes can work without it.
- **Lifecycle Unhealthy → 503** from the moment shutdown starts, so a load balancer that polls readiness stops sending new requests while in-flight ones finish.
- **Response body:** status names only. No exception text, connection strings, file paths or descriptions. Reasons are written to the server log instead (for example `Readiness: 1 database migration(s) not applied.`).
- Health endpoints are not rate-limited and need no authentication.
- **`AllowedHosts`** applies to health checks too. A probe that calls the API by an IP address or an internal host name gets 400 unless that name is in `AllowedHosts`. *Recommendation:* add the probe's host name when deploying.

---

## 3. Dependency failure handling

### FakeStoreAPI (*verified*)

| Safeguard | Behaviour |
|---|---|
| Timeouts | Each attempt is cut off after `AttemptTimeoutSeconds` (4 s). The whole call, retries and waits included, never exceeds `TimeoutSeconds` (10 s). |
| Bounded retries | Up to `RetryCount` (2) retries, only for **GET and HEAD**, only on network errors, attempt timeouts and 408, 429, 500, 502, 503 and 504. A 400 or 404 is returned at once. A caller's own cancellation is never retried. |
| Backoff | 200 ms, then 400 ms (doubling, plus up to 20% random jitter so many callers don't retry in step), capped at 2 s. |
| One call at a time | When the cache is empty, concurrent requests wait for one shared FakeStoreAPI call instead of each making their own. |
| Stale browsing | If FakeStoreAPI fails, product lists, single products and categories are served from the last catalogue that loaded successfully, if it is no older than `StaleCatalogueMaxMinutes` (60). These responses carry `Catalogue-Stale: true`. `0` turns the fallback off. |
| **Checkout fails closed** | Checkout always needs a fresh catalogue for authoritative prices. It never uses the stale copy: during an outage it returns 502 `UPSTREAM_UNAVAILABLE`, 502 `UPSTREAM_MALFORMED` or 504 `UPSTREAM_TIMEOUT`, and nothing is saved. |
| No catalogue at all | If FakeStoreAPI has never answered since startup, browsing returns 502 or 504 as before. |

### Database (*verified*)

Checkout writes the order in an explicit transaction, so the commit is a separate step. How a failure is reported depends on **when** it happened, not only on the SQLite error code:

| When checkout's database step fails | Response | Was the order saved? |
|---|---|---|
| Opening the connection, beginning the transaction, the idempotency lookup, or the inserts: database **locked** | 503 `DATABASE_BUSY`, `Retry-After: 5` | **No, guaranteed**: COMMIT was never sent, and the transaction is rolled back |
| The same steps: database **can't be opened** (missing folder, permissions, disk) | 503 `DATABASE_UNAVAILABLE`, `Retry-After: 5` | **No, guaranteed**, for the same reason |
| **During COMMIT**, whatever the error code (busy, can't open, I/O) | 500 `DATABASE_ERROR`: "We couldn't confirm whether the order was saved." | **Unknown.** The data may already be durable when the error is reported, so it's never called a definite failure |
| Any other database error | 500 `DATABASE_ERROR` | Unknown |

The frontend presents 503 `DATABASE_BUSY` and `DATABASE_UNAVAILABLE` as "not placed" and any other 5xx as "couldn't confirm".

**Retrying after an unknown outcome never creates a second order** (*verified*):
- **The client keeps the original key.** After any uncertain outcome (network error, timeout, a 5xx other than the two above, an unreadable response, or a request abandoned mid-flight), the frontend pins the `Idempotency-Key`. It stores the key in this tab's sessionStorage, so it survives a reload, and reuses it for every attempt, **even if the shopper changes their details or cart**. Only two things release it: the server confirming an order, or the server answering 422 `IDEMPOTENCY_KEY_REUSED`, which means an order already exists under that key. Before this check, a changed order got a new key straight away, so correcting a name after a lost response could have placed a second order.
- **The server reconciles by key.** The key is looked up before the catalogue is fetched and before anything is written:
  - same key, same details, order saved → that order is returned (`Idempotent-Replayed: true`), even if FakeStoreAPI is down at the time;
  - same key, different details or a different shopper, order saved → 422 `IDEMPOTENCY_KEY_REUSED`, and the shopper is told an earlier attempt may already have been placed;
  - no order under the key → the order is placed, once.
- **The 500's wording** says to retry with the same key, and that a new key could create a second order.

The pinned key lives in one tab's sessionStorage. A retry from another tab or device, or after the tab is closed, uses a new key. The shopper is told to check before ordering again, and signed-in shoppers can check **Your account → Orders**.

*Verified:* a test injects a real SQLite error (busy and can't-open) at each stage: inserting, just before COMMIT, and just **after** a COMMIT that succeeded. It then checks the response against what is actually in the database file, and checks that the retry with the same key gives exactly one order. With the earlier single-step save, the after-commit case returned 503 `DATABASE_UNAVAILABLE` / `DATABASE_BUSY` for an order that had been saved. That was the flaw this fixes.

**The no-change guarantee is for checkout only.** Clients must not read every 503 as "nothing was saved":
- **Reads** (`/api/orders`, catalogue) write nothing, so a 503 changes nothing.
- **Account endpoints** (Identity) save in one step, with no separate commit phase and no idempotency key. On those, 503 `DATABASE_BUSY` or `DATABASE_UNAVAILABLE` describes the error. It does **not** guarantee the operation was rolled back. *Limitation, accepted for this scope:*
  - **Registration is not idempotent.** After a 5xx or a lost response the account may or may not exist. The outcome is *recoverable*, not idempotent: repeating it either creates the account or is refused as a duplicate email, and never creates two accounts for one email. The frontend says exactly that: "We couldn't confirm whether your account was created. Try signing in first; if that doesn't work, create the account again."
  - **Sign-in and token refresh** create no stored data (tokens are issued, not saved), so repeating them is harmless.
- A fully idempotent registration would need its own key and a stored outcome, like checkout's. It isn't implemented.

Product browsing doesn't use the database, so it keeps working while the database is down. Readiness reports 503.

### Which operations are safe to retry

| Operation | Retried automatically? | Safe for the caller to retry? |
|---|---|---|
| `GET /api/products`, `/api/products/{id}`, `/api/categories`, `/api/store/config` | Upstream GETs are retried by the API (above). The browser does **not** retry by itself, except **once** when it comes back online after a failed or stale load. | Yes: read-only |
| `GET /api/orders`, `/api/orders/{reference}` | No | Yes: read-only |
| `POST /api/checkout` | **Never**, on the server or in the browser | After 503 `DATABASE_BUSY` / `DATABASE_UNAVAILABLE` nothing was saved, so any retry is safe. After any other failure, only by resending the **same request with the same `Idempotency-Key`**. If the first attempt was saved, the API returns that order (`Idempotent-Replayed: true`) instead of creating a second one. The frontend does exactly this when the shopper selects **Try again**. |
| `POST /api/account/login`, `/refresh` | No | Yes: nothing is stored |
| `POST /api/account/register` | No | **Not idempotent, but recoverable.** After an error the account may exist, even on a 503. Try signing in first. A repeat either creates the account or is refused as a duplicate email; it never creates a second account. |
| Any 503 with `Retry-After` | No | Yes, after waiting the given seconds |

---

## 4. Frontend behaviour (*verified*)

| Situation | What the shopper sees |
|---|---|
| Browser goes offline | A banner under the header (`role="status"`, announced politely): "You're offline. Your cart is saved on this device…". It goes away on reconnect. |
| Back online after a failed or stale catalogue load | The product list is fetched again **once**. Another failure leaves the error with **Try again**; nothing loops. |
| Place order while offline | Nothing is sent: "You're offline, so your order hasn't been sent. Your cart is saved." The outcome is certain, so a normal retry is safe. |
| Catalogue served from the saved copy | The products show with a notice: "We couldn't refresh the product list, so you're seeing a recently saved copy. Prices and availability are checked again when you place an order." It has a **Refresh** button. |
| Catalogue can't load at all | The error panel explains the cause in shopper terms (offline, timeout, supplier not responding) and says the cart is saved. There's no developer text. |
| Server-side failure (5xx) | A **Reference** (the request's correlation ID) is shown under the message, so a shopper can quote it and the request can be found in the log. |
| Checkout 503 `DATABASE_UNAVAILABLE` or `DATABASE_BUSY` (only sent when COMMIT was never attempted), or an upstream outage | A definite failure: "…Your cart is saved." with **Try again**. |
| Checkout network error, timeout, 500 (including a failed COMMIT) or unreadable response | "We couldn't confirm whether your order went through", with the reference when there is one. The key is pinned: **Try again** reuses it, even after a reload or after the details change, until the server confirms an order or reports the key reused. |
| Registration 5xx (including 503) or lost response | "We couldn't confirm whether your account was created. Try signing in first…" |
| Place order while offline | No request is made (checked in the browser test by watching network requests, and in Vitest by asserting the fake API received no checkout call). |

The cart is never cleared except on a confirmed order. The offline, stale and checkout-outage states pass a layout check at 320 px and an axe WCAG 2.2 A/AA scan.

---

## 5. Observability (*verified*)

- **Correlation IDs.** Every response carries `X-Correlation-Id`. A caller may send one (letters, digits, `.`, `_`, `-`; up to 64 characters). Anything else is replaced with a generated 32-character ID, so log lines can't be forged or broken by header content. The same value is the `traceId` in every error body and is attached to every log line written during that request.
- **Every error response is logged exactly once**, under its ID:
  - Errors raised as exceptions (validation, price change, unavailable products, upstream and database failures, unexpected errors) are logged by the exception handler.
  - Errors answered without an exception (a controller's 404, model validation, 401, 429, an unknown route, a failing readiness check) get one line from the correlation middleware: `Request {Method} {Route} answered {StatusCode}.`. That line names the **route pattern** (`api/products/{id:int}`), never the raw path, so no identifiers or caller-supplied text reach the log.
  - Successful requests add no line.
  - *Verified* by `CorrelationIdTests` for 11 kinds of error (400 ×3, 401, 404 ×2, 409, 422, 429, 502, 503), plus the commit-failure 500 in `CheckoutOutcomeTests`: header = `traceId` = log scope, with exactly one line per request. Also checked by hand in a Production-mode run with JSON logs.
- **Structured logs.** Outside Development the console writes one JSON object per line (UTC timestamp, level, category, message, the template's fields, and the `CorrelationId` scope). Development keeps readable single-line text with the scope included.
- **Levels.** Expected dependency failures (FakeStoreAPI down, slow or malformed; database busy or unavailable) are one Warning line each, with the error code and no stack trace. Unexpected errors stay at Error, with the exception. Each upstream retry is a Warning naming the method, path, reason and wait.
- **Lifecycle.** "Storefront API started in {Environment}; catalogue source {host}", "stopping: readiness now reports unavailable; finishing in-flight requests", and "stopped".
- **Never logged.** Passwords, access or refresh tokens, `Authorization` headers and customer emails are never logged. A test signs in, checks out with a token and asserts that none of these appear anywhere in the captured logs.

**Finding a request from a shopper's reference:**

```bash
# JSON logs (any non-Development environment)
grep '"CorrelationId":"<reference>"' api.log
# Development (text logs)
grep 'CorrelationId:<reference>' api.log
```

---

## 6. Startup, shutdown and configuration

- **Fail fast** (*verified*). All options are validated at startup. New rules: `AttemptTimeoutSeconds` 1–60 and not greater than `TimeoutSeconds`; `RetryCount` 0–5; `RetryBaseDelayMilliseconds` 0–5000; `StaleCatalogueMaxMinutes` 0–1440. An invalid value stops the app with a message naming the key.
- **Graceful shutdown** (*verified* with SIGTERM in a manual Production-mode run). On SIGTERM or Ctrl+C, readiness turns 503 at once, in-flight requests get up to `Hosting:ShutdownTimeoutSeconds` (30) to finish, then the process exits. The stopping and stopped lines are logged.
- **Migrations** are still not applied at startup outside Development. A pending migration makes readiness 503 and logs the count, so a deploy that forgot `dotnet ef database update` is visible instead of failing on the first order.

| Key | Default | Meaning |
|---|---|---|
| `FakeStore:TimeoutSeconds` | 10 | Total budget for one upstream call, retries included |
| `FakeStore:AttemptTimeoutSeconds` | 4 | Per attempt |
| `FakeStore:RetryCount` | 2 | Retries after the first attempt (GET/HEAD only) |
| `FakeStore:RetryBaseDelayMilliseconds` | 200 | First backoff delay, doubled each time |
| `FakeStore:CatalogueCacheSeconds` | 300 | How long a fresh catalogue is reused |
| `FakeStore:StaleCatalogueMaxMinutes` | 60 | Oldest saved catalogue browsing may fall back to; `0` disables it |
| `Hosting:ShutdownTimeoutSeconds` | 30 | Time allowed for in-flight requests at shutdown |
| `Logging:Console:FormatterName` | `json` (`simple` in Development) | Log format |

Environment variables use `__`, for example `FakeStore__RetryCount=0`.

No containers, orchestration, cloud services or new packages were added. The health checks are ASP.NET Core's built-in `Microsoft.Extensions.Diagnostics.HealthChecks`, and the retry handler is about 80 lines of code.

---

## 7. Diagnosis

| Symptom | Check | Likely cause and fix |
|---|---|---|
| `/health/live` doesn't answer | Is the process running? Is the port right? Does `AllowedHosts` include the host you're calling? | The process has stopped or is listening elsewhere. A 400 means a host-name mismatch. |
| `/health/ready` is 503, `database: Unhealthy` | Log: `Readiness: N database migration(s) not applied.`, or `Health check database … 'Database unreachable.'` | Run `dotnet ef database update --project Storefront.Api`; or check that the `Data Source` folder exists and is writable |
| `/health/ready` is 503, `lifecycle: Unhealthy` | Log: "Storefront API stopping" | Shutdown in progress; expected during a restart |
| `catalogue: Degraded`, products show "recently saved copy" | Log: `FakeStoreAPI GET /products failed (…); retry 1 of 2` and `Dependency failure: UPSTREAM_…` | FakeStoreAPI is down or slow. Browsing continues for up to 60 minutes; checkout returns 502/504 until it recovers. Try `curl https://fakestoreapi.com/products`. |
| Checkout 503 `DATABASE_UNAVAILABLE` | Log: `Dependency failure: DATABASE_UNAVAILABLE` | The SQLite file can't be opened: path, permissions or disk. Nothing was saved. |
| Checkout 500 "couldn't confirm whether the order was saved" | Log: `Request failed: DATABASE_ERROR` with the exception | COMMIT failed. Look the order up by the shopper's `Idempotency-Key`, or have them select **Try again**, which returns the order if it was saved. |
| A shopper quotes a reference | `grep` the log for it (section 5) | Every line from that request, including the error code |

---

## 8. Running and testing

```bash
# API (Development, text logs)
cd backend/Storefront.Api && dotnet run --launch-profile http
curl -s http://localhost:5080/health/live
curl -s http://localhost:5080/health/ready

# JSON logs as in production
ASPNETCORE_ENVIRONMENT=Production dotnet run --no-launch-profile
```

| Suite | Command | Availability tests |
|---|---|---|
| Backend unit | `dotnet test -c Release --filter Category=Unit` (from `backend/`) | `UpstreamRetryHandlerTests` (retry limits, GET-only, attempt and total timeouts, caller cancellation, backoff bounds, config validation) and `CatalogueResilienceTests` (stale window, checkout strictness, single-flight, health checks) |
| Backend API | `dotnet test -c Release --filter Category=Api` | `HealthAndAvailabilityTests`: real HTTP against the app with an unreachable or unmigrated database, a database lost and restored while running, an upstream outage and recovery, no secrets in logs, invalid config. `CheckoutOutcomeTests`: database failures injected before, at and after COMMIT, checked against the database file. `CorrelationIdTests`: one ID across header, `traceId` and log for every kind of error |
| Frontend | `npm run test:coverage` (from `frontend/`) | `services/availability.test.ts`, `hooks/useOnlineStatus.test.tsx`, `components/Availability.test.tsx` |
| Browser | `npm run test:e2e` | Outage journey (saved copy, safe checkout failure with reference, recovery), offline journey (`context.setOffline`: nothing sent, then order placed), 320 px plus axe check of these states |
| API smoke | `python3 tools/smoke_test.py --api http://localhost:5080 --mock http://localhost:9100` (start the API with the mock, as in the README) | Health endpoints, correlation IDs, stale browsing, checkout fail-closed, readiness Degraded and recovery, 504 within the time budget |

All tests are deterministic: retry delays are injected, clocks are controllable, upstream failures come from in-memory fakes or the local mock, and every API test uses its own temporary database. No test calls the real FakeStoreAPI.

---

## 9. Limitations and recommendations

**Limitations (true today):**
- Single process, single SQLite file. If that process or machine stops, the store is down. Nothing here provides failover.
- The saved catalogue and the "last failure" state live in memory, so they are lost on restart. A restart during a FakeStoreAPI outage means browsing returns 502 until FakeStoreAPI is back.
- There is no circuit breaker. During a long outage each browse request still makes one call (with retries) to FakeStoreAPI before falling back, so users wait up to `TimeoutSeconds` on cache misses.
- `navigator.onLine` reliably detects "no network at all". It can't detect a captive portal or a network that is up but can't reach the API; those show as normal request errors.
- Rate limiting is per IP in memory. Behind a proxy without forwarded headers, all clients share one limit.
- Correlation IDs are not propagated to FakeStoreAPI (it wouldn't use them). There is no distributed tracing or metrics exporter.
- Logs go to the console only. Keeping, rotating and searching them is up to whatever runs the process.

**Recommendations (need deployment infrastructure, not done or verified here):**
- Point the host's liveness probe at `/health/live` and its readiness or load-balancer health check at `/health/ready`. Allow at least `Hosting:ShutdownTimeoutSeconds` between SIGTERM and a forced kill.
- Ship console logs to a log store and alert on: readiness 503, repeated `Dependency failure` warnings, and any Error-level line.
- Run an external uptime check against `/health/ready` from outside the network. Only that kind of measurement, over time, can support any availability figure.
- For more than one instance: a server database instead of SQLite, a shared Data Protection key ring, a distributed or per-instance catalogue cache, and forwarded headers. See the production checklist in `Backend.md`.
- Consider `Microsoft.Extensions.Http.Resilience` (adds a circuit breaker) if FakeStoreAPI outages become long or frequent.
