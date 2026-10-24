# Storefront backend: design notes

This explains how the ASP.NET Core API is organised and why. It covers the layers, authentication and security, the database, idempotent checkout and error handling. Setup commands are in the [README](../README.md).

## 1. Architecture

### Layers

| Folder | Responsibility | Knows about HTTP? | Knows about the database? |
|---|---|---|---|
| `Controllers/` | Read the request (body, headers, signed-in user), call one service, shape the response. No business rules. | Yes | No |
| `Services/` | Business rules: catalogue filtering, pricing, checkout validation, price verification, order placement and order queries. | No | Checkout and order queries use `StorefrontDbContext` |
| `Data/` | `StorefrontDbContext`, entities (`Order`, `OrderLine`, `StorefrontUser`), table configuration (constraints, indexes), migrations, setup. | No | Yes |
| `Models/` | In-memory domain types: the normalised catalogue and price breakdowns. | No | No |
| `DTOs/` | Request and response contracts. Entities are never returned directly. | No | No |
| `Security/` | Identity setup, the authorization policy, rate limits, security headers, `CurrentUser`. | Yes | Identity only |
| `Middleware/` | `GlobalExceptionHandler`: exceptions become ProblemDetails. | Yes | Classifies database errors |
| `Configuration/` | Strongly typed options, bound from `appsettings.json` and validated at startup. | No | No |

Dependency direction: Controllers → Services → (Data, Models). Nothing in `Services/` depends on `Controllers/` or on `HttpContext`. The controller turns the token and headers into a plain `CheckoutCaller` record (user ID and idempotency key), and the service only sees that.

### Checkout, step by step

1. `CheckoutController` reads the JSON body, the `Idempotency-Key` header and the user ID from the validated token (`User.GetUserId()`; null for guests).
2. `CheckoutService` validates the request (`CheckoutRequestValidator`, plus the key format).
3. If a key was sent and an order with that key exists, it returns that order. That applies only if the same caller sent the same items and customer; otherwise it's 422.
4. It loads the catalogue (`ProductCatalogService`, cached for 5 minutes), rejects unknown products (422), and prices every line from the catalogue with `PricingCalculator`. The client's prices are never used.
5. If `expectedTotal` differs from the server total, it returns 409 with a fresh quote. Nothing is saved.
6. It builds the `Order` with its `OrderLine`s and saves them with **one `SaveChanges`**. EF Core wraps that in a single database transaction: the order and all its lines are saved, or none are.
7. It returns the same `CheckoutConfirmationDto` as before.

### Why there is no repository layer

`StorefrontDbContext` already provides a unit of work (`SaveChanges`) and typed collections (`Orders`). Wrapping it in `IOrderRepository` would add a file per entity and hide useful EF features (`Include`, `AsNoTracking`, projections) without making anything easier to test. The tests run against **real SQLite** built from the real migrations, which catches constraint and query mistakes that a mocked repository would hide. Services that read or write orders take `StorefrontDbContext` directly. The query side has its own small service, `OrderQueryService`, so the "only your own orders" rule lives in one place.

## 2. Authentication and security

### Who can call what

| Endpoint | Access | Why |
|---|---|---|
| `GET /api/products`, `/api/products/{id}`, `/api/categories`, `/api/store/config`, `/health`, `/health/live`, `/health/ready` | Public | Catalogue browsing needs no account. |
| `POST /api/checkout` | Public (guest checkout). With a bearer token, the order is linked to that account. | The existing frontend checks out without signing in, and that contract is kept. |
| `GET /api/orders`, `GET /api/orders/{reference}` | **Signed-in customer** (`Customer` policy) | Order history is personal data. |
| `/api/account/*` | Public, rate-limited | Registration and sign-in. |
| `/swagger` | Development only | API documentation is not exposed in production. |

### How it works

- **ASP.NET Core Identity** with its built-in API endpoints (`MapIdentityApi`). Passwords are hashed with PBKDF2 (Identity's `PasswordHasher`), never stored or logged in plain text.
  - **Password rules:** at least 8 characters, with upper case, lower case, a digit and a symbol.
  - **Lockout:** 5 failed sign-ins lock the account for 5 minutes.
  - **Emails:** must be unique.
- **Bearer tokens**, issued by `/api/account/login` and sent as `Authorization: Bearer …`. They are Identity's own tokens, signed with the Data Protection key ring; there is no custom token code. Access tokens last 1 hour; `/api/account/refresh` swaps a refresh token for a new one.
- **Why bearer tokens rather than cookies:** the frontend runs on a different origin (`:5173`). The browser never attaches header tokens automatically, so cross-site request forgery isn't possible. The cookie scheme Identity also registers is configured to answer 401/403 instead of redirecting to a login page.
- **Authorization is enforced on the server.** `OrdersController` carries `[Authorize(Policy = "Customer")]`. That policy requires an authenticated user via the bearer scheme.
- **Ownership:** every order query filters by the caller's user ID. Another customer's order returns **404**, not 403, so a valid reference can't be confirmed by probing.
- **Checkout with a bad token fails loudly:** checkout is open to guests, but a request that sends an `Authorization` header the server can't authenticate (expired, forged) gets **401** before anything is saved. Without this, an expired session would quietly place a guest order that never shows up in the shopper's account.
- **No mass assignment:** request DTOs contain only the fields a client may set. The order's owner comes from the token, never the body; extra fields such as `userId` or `unitPrice` are ignored (there's a test for this).
- **Idempotency keys are scoped to their caller:** a key can only replay an order for the same user (or guest) with the same items and customer, so a key can't be used to read someone else's confirmation.

### Hardening

| Control | Setting |
|---|---|
| Rate limiting | Per client IP, fixed 60-second window: 10 requests for `/api/account/*`, 30 for checkout. Over the limit returns 429 ProblemDetails with `Retry-After`. Configurable under `RateLimiting`. |
| Request size | Checkout bodies over 64 KiB → 413; more than 50 lines → a single 400 error. |
| Security headers | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cross-Origin-Resource-Policy: same-site`, and `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (not on Swagger UI). |
| HTTPS | Outside Development: `UseHsts()` (1 year) and `UseHttpsRedirection()`. Development stays on `http://localhost:5080` for the Vite dev server. |
| CORS | Only the configured origins; methods GET and POST; headers `Content-Type`, `Accept`, `Authorization`, `Idempotency-Key`, `X-Correlation-Id`. Exposes `Idempotent-Replayed`, `Retry-After`, `X-Correlation-Id` and `Catalogue-Stale`. |
| Host filtering | `AllowedHosts: localhost;127.0.0.1`. Other `Host` headers get 400. |
| Secrets | None in the repository. SQLite has no password. The Data Protection keys that sign tokens are created at runtime and stored in the app database (`DataProtectionKeys`). Outside Development they're encrypted with a certificate supplied as a secret (`DataProtection__Certificate__Pfx` and `__Password`); the API refuses to start without it (see `Deployment.md`, section 8). |
| Development vs production | `appsettings.Development.json` turns on migrations at startup and allows `127.0.0.1:5173`; everything else defaults to the stricter setting. |

### Not implemented (deliberately, and documented)

- **No login UI in the frontend.** Accounts work through the API (Swagger or curl); the frontend keeps checking out as a guest.
- **No email confirmation or password-reset delivery.** No email sender is configured, so `RequireConfirmedAccount` is off and Identity's default no-op sender is used.
- **No roles or admin area.** Nothing in the current scope needs one.
- **Data Protection keys are kept in the app database**, so tokens keep working across restarts, new containers and multiple instances. Outside Development each key is encrypted with a certificate before it's written, so a copy of the database alone can't forge tokens. Losing the certificate signs everyone out; it doesn't take the API down.

## 3. Database

### Choice: SQLite with EF Core 10

SQLite is a single file with no server to install, so a fresh checkout runs with `dotnet run`. EF Core gives migrations, constraints and transactions with the same code that would target SQL Server or PostgreSQL later; only the provider line in `DatabaseSetup` and the migrations would change. Versions are EF Core and Identity 10.0.13, matching the ASP.NET Core 10 runtime.

### What is stored

| Table | Contents |
|---|---|
| `Orders` | `Reference` (unique), `CreatedAtUtc`, `UserId` (nullable FK to `AspNetUsers`, set to null if the account is deleted), customer name and email, `Currency`, `Subtotal`, `Tax`, `Shipping`, `Total`, `IdempotencyKey` (unique when present), `RequestFingerprint` |
| `OrderLines` | `OrderId` (FK, cascade delete), `ProductId`, `Title`, `UnitPrice`, `Quantity`, `LineTotal`; unique on (`OrderId`, `ProductId`) |
| `AspNetUsers` etc. | Identity's tables: users, password hashes, lockout state, tokens |

- **Historical snapshot.** Each line stores the title and unit price as they were at checkout, and the order stores its totals. Later catalogue changes don't alter past orders (tested).
- **Catalogue not stored.** FakeStoreAPI remains the source of truth for products and prices; a local copy would be a second price list that could go stale. `ProductId` is therefore not a foreign key.
- **Constraints.** Quantities and product IDs must be positive, and amounts can't be negative (check constraints). SQLite stores decimals as text, so the amount checks `CAST` to a number first. Reference and idempotency key are unique; there's one line per product per order.
- **Indexes.** The unique ones above, plus (`UserId`, `CreatedAtUtc`) for "my orders, newest first".
- **Times** are stored as UTC and read back marked as UTC.

### Idempotency and concurrent checkouts

- Clients may send `Idempotency-Key: <8–100 chars of A–Z a–z 0–9 - _>`; the frontend sends a UUID.
- **Repeat of the same request:** the stored order comes back with `Idempotent-Replayed: true`.
- **Same key, different request:** 422 `IDEMPOTENCY_KEY_REUSED`. Different items or customer count as different, and so does a different caller (another user, or a guest replaying a user's key).
- **Two requests with the same key at the same moment:** both may pass the "does it exist?" check, but the unique index lets only one insert succeed. The loser catches the unique-constraint error, reads the winner's order and returns it. A test fires 8 parallel requests and checks that exactly one order exists.
- **Order reference collisions** (random 6-character suffix) are handled the same way: on a unique violation without a matching key, a new reference is generated (up to 3 attempts).
- **Without a key**, each request is a new order, as before.

### Migrations and local data

```bash
cd backend
dotnet tool restore                                    # dotnet-ef 10.0.13, pinned in .config/dotnet-tools.json
dotnet ef database update --project Storefront.Api --context StorefrontDbContext     # create or upgrade App_Data/storefront.db
# after changing the model, one migration per provider:
dotnet ef migrations add <Name> --project Storefront.Api --context StorefrontDbContext --output-dir Data/Migrations
dotnet ef migrations add <Name> --project Storefront.Api --context PostgreSqlStorefrontDbContext --output-dir Data/Migrations/PostgreSql
```

- **Providers:** SQLite for development and tests, PostgreSQL for production, chosen by `Database:Provider` with no default (see `Deployment.md`). The two have separate migration sets, because generated migrations aren't portable between providers.
- **Location:** in Development, `ConnectionStrings:Storefront` is `Data Source=App_Data/storefront.db` (from `appsettings.Development.json`), resolved against the `Storefront.Api` folder. The file is git-ignored.
- **Development:** applies pending migrations at startup (`Database:ApplyMigrationsOnStartup`).
- **Reset:** stop the API and delete the `.db` file; the next start (or `dotnet ef database update`) recreates an empty database.
- **Inspect:** open the file with any SQLite viewer. `AspNetUsers.PasswordHash` holds hashes only.
- **Tests never touch this file.** Each test gets its own in-memory or temporary SQLite database, created by running the real migrations. A test (`Migrations_match_the_model`) fails if someone changes the model without adding a migration.

## 4. Error handling

All errors are RFC 7807 `application/problem+json` with `status`, `title`, `traceId` and a stable `code`. `traceId` equals the response's `X-Correlation-Id`. 5xx responses never include exception messages, SQL, stack traces or configuration. Every 503 carries `Retry-After: 5`.

| Status | `code` | When |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Invalid body, query, `Idempotency-Key`, malformed JSON, Identity validation (for example a weak password). Field errors are in `errors`. |
| 400 | `BAD_REQUEST` | Unreadable request (for example a truncated body) |
| 401 | `UNAUTHORIZED` | Missing or invalid bearer token on a protected endpoint; an invalid token on checkout; wrong password at login |
| 403 | `FORBIDDEN` | Authenticated but not permitted (no such endpoints yet; mapped for consistency) |
| 404 | `NOT_FOUND` | Unknown route, product or order (including another customer's order) |
| 405 / 415 | `METHOD_NOT_ALLOWED` / `UNSUPPORTED_MEDIA_TYPE` | Wrong method or content type |
| 409 | `PRICE_CHANGED` | `expectedTotal` differs from the server total; includes a `quote` |
| 409 | `CONSTRAINT_CONFLICT` / `CONCURRENCY_CONFLICT` | A database uniqueness or check constraint, or a concurrent update |
| 413 | `REQUEST_TOO_LARGE` | Body over the limit |
| 422 | `PRODUCTS_UNAVAILABLE` | Unknown product IDs (`unavailableProductIds`) |
| 422 | `IDEMPOTENCY_KEY_REUSED` | Key reused for a different order or by a different caller |
| 429 | `TOO_MANY_REQUESTS` | Rate limit hit; see `Retry-After` |
| 500 | `DATABASE_ERROR` | Any other database failure, including a checkout whose COMMIT failed ("We couldn't confirm whether the order was saved"). The outcome is unknown: retry with the same `Idempotency-Key` |
| 500 | `INTERNAL_ERROR` | Anything unexpected |
| 502 / 504 | `UPSTREAM_UNAVAILABLE` / `UPSTREAM_MALFORMED` / `UPSTREAM_TIMEOUT` | FakeStoreAPI failed, sent bad data, or was too slow (after bounded retries). Browsing endpoints return these only when there is no saved catalogue under 60 minutes old; otherwise 200 with `Catalogue-Stale: true`. Checkout always returns them. |
| 503 | `DATABASE_BUSY` | SQLite was locked. On checkout, only returned when COMMIT was never sent, so nothing was saved; safe to retry |
| 503 | `DATABASE_UNAVAILABLE` | The SQLite file couldn't be opened. On checkout, only returned when COMMIT was never sent, so nothing was saved; safe to retry |

**Logging.**
- Unexpected 5xx errors are logged at Error, with the exception. Expected dependency failures (upstream, database busy or unavailable) are one Warning line with the code and no stack trace.
- Every line written during a request carries its `CorrelationId`. Every error response is logged exactly once: by the exception handler, or, for errors answered without an exception, as `Request {Method} {Route} answered {StatusCode}.` (route pattern, not raw path). Logs are JSON outside Development.
- 4xx rejections are logged at Information, with the code and the exception's message.
- Checkout logs the order reference, line count, total and guest or signed-in status. It never logs passwords, tokens or customer emails.
- EF's SQL command log is set to Warning, so parameter values aren't written to the log.

## 5. Production checklist (not done here)

- The Data Protection key ring is persisted to the database and encrypted at rest with a certificate (done; `Deployment.md`, section 8). A key vault would remove the certificate from the platform's environment settings.
- Behind a reverse proxy, set `ForwardedHeaders` (off by default) once the right header and hop count are confirmed on the platform (`Deployment.md`, section 7), so rate limits and HTTPS detection see the real client.
- Set `AllowedHosts` and `Cors:AllowedOrigins` to the real host and origin.
- For more than one server or instance, move from SQLite to a server database (change the provider and regenerate migrations), or keep a single instance.
- Apply migrations deliberately, following the reviewed-script procedure in `Deployment.md`, rather than at startup.
- Add an email sender and turn on `RequireConfirmedAccount`, if accounts become customer-facing.
- Point liveness and readiness probes at `/health/live` and `/health/ready`; ship logs and alert on readiness 503 (see `Availability.md`).
- Consider a circuit breaker (`Microsoft.Extensions.Http.Resilience`) on top of the existing bounded retries if FakeStoreAPI outages are long.

## 6. Questions you may be asked in review

- **Why not a JWT library?** Identity's API endpoints already issue and validate tokens, hash passwords and handle lockout, using maintained framework code. A hand-rolled JWT setup would add signing-key management and more ways to get it wrong, with no benefit at this scope.
- **Why does checkout stay anonymous?** Requiring sign-in would break the existing frontend, which has no login screen. Guests can still order; signed-in users additionally get order history.
- **Why 404 instead of 403 for someone else's order?** A 403 would confirm that the reference exists.
- **Why require `expectedTotal` and also support idempotency keys?** They solve different problems. `expectedTotal` makes sure the customer agreed to the price; the key makes sure a retry doesn't buy twice.
- **Is one `SaveChanges` really a transaction?** Yes. EF Core opens a transaction around all the inserts in one `SaveChanges` call and rolls back if any fails. A test inserts an order with one invalid line and checks that nothing was saved.
