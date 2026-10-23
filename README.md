# Greenhaus: full-stack e-commerce catalogue and cart

A demo storefront with a React + TypeScript frontend and an ASP.NET Core Web API backend-for-frontend over [FakeStoreAPI](https://fakestoreapi.com). It covers catalogue search, filtering and sorting, a persistent slide-out cart, exact money arithmetic, and a checkout that the server re-prices, re-validates and saves to a local SQLite database. Shoppers can check out as guests or create an account, sign in, and see their order history. Every product has a details view with a shareable link.

> Checkout is a **demo**: no payment is taken and no card details are requested. Orders are saved locally in `backend/Storefront.Api/App_Data/storefront.db`.

**Design notes:** [`Documentation/Backend.md`](Documentation/Backend.md) (layers, authentication, database, idempotency, error codes) and [`Documentation/Frontend.md`](Documentation/Frontend.md) (API layer, sessions, checkout outcomes, responsive and accessibility decisions). **Testing:** [`Documentation/Testing.md`](Documentation/Testing.md) (test categories, commands, coverage reports, results) and the [manual test plan](Documentation/Testing/ManualTestPlan.md).

---

## 1. Architecture

```
 Browser (React, :5173)                  ASP.NET Core API (:5080)                    FakeStoreAPI
┌─────────────────────────────┐        ┌──────────────────────────────────┐        ┌──────────────┐
│ CatalogPage ── useProducts ─┼─GET───▶│ ProductsController               │        │ /products    │
│ ProductDetailsDialog ───────┼─GET───▶│   └ ProductCatalogService ───────┼─GET───▶│ /products/id │
│ CartContext (useReducer)    │        │       ├ CategoryNormalizer       │        └──────────────┘
│   └ localStorage (v1)       │        │       └ ProductBadgeRules        │
│ CartDrawer / CheckoutDialog ┼─POST──▶│ CheckoutController (guest or     │
│   (Idempotency-Key header)  │        │   signed-in) └ CheckoutService ──┼──┐
│ utils/pricing (int cents)   │◀───────┤       ├ CheckoutRequestValidator │  │     SQLite (EF Core)
│ StoreConfigContext ─────────┼─GET───▶│       └ PricingCalculator        │  ├──▶ App_Data/storefront.db
│ AccountDialog / MyOrders ───┼─GET───▶│ OrdersController [Authorize]     │  │     Orders, OrderLines,
│ SessionContext (bearer) ────┼─POST──▶│   └ OrderQueryService ───────────┼──┤     Identity users
└─────────────────────────────┘        │ /api/account/* (Identity API)    ┼──┘
                                       │ GlobalExceptionHandler → ProblemDetails │
                                       └──────────────────────────────────┘
```

**Backend responsibilities.** Fetch and validate upstream data; normalise categories (`jewelery` → `jewelry`, men's and women's clothing grouped under `clothing`); compute badges; apply search, category, price range and sort; publish the pricing rules. At checkout: validate the request, look up authoritative prices, calculate totals with `decimal`, and save the order and its lines in one transaction. It also manages customer accounts (ASP.NET Core Identity), serves each signed-in customer their own orders, and returns structured errors.

**Frontend responsibilities.** Render the catalogue and product details, keeping filters and the open product in the URL; manage the cart in one reducer and persist it to localStorage; show estimated totals using the same rules as the server; manage the sign-in session (bearer tokens, refresh, expiry); run the checkout flow and recover from every failure without losing the cart or placing an order twice.

**What lives where.** The cart is local to the browser (localStorage) for guests and signed-in shoppers alike; it is not synced to the account. Orders are saved on the server, and signed-in shoppers can list them. The sign-in session keeps the refresh token in this tab's sessionStorage and the short-lived access token in memory only.

### Key decisions

| Decision | Choice | Why |
|---|---|---|
| Cart ownership | **Client-managed cart in localStorage; server validates at checkout** (option 1 in the brief) | Refresh-survival is free, guests need no account, and the security boundary stays where it matters: the server never trusts client prices. `GET /api/cart` is therefore not implemented. |
| Database | **SQLite file + EF Core 10, with migrations** | Runs on any machine with no server to install. Orders snapshot titles and prices, so history survives catalogue changes. The catalogue itself stays upstream (FakeStoreAPI). |
| Accounts | **ASP.NET Core Identity API endpoints, bearer tokens** | Built-in password hashing, lockout and tokens; no custom JWT code. Guest checkout stays open. The frontend signs in with the same endpoints and refreshes the token when it expires. |
| Expired session at checkout | **401, never a silent guest order** | A checkout sent with an invalid or expired token is rejected, so the frontend renews the token and resends the same order (same key), or tells the shopper and lets them choose guest checkout. |
| Retries | **`Idempotency-Key` header on checkout** | Repeating a request returns the original order instead of a second one; the frontend reuses the key on **Try again**. |
| Where filtering happens | Backend (`GET /api/products?search=&category=&sort=`) | One implementation of the rules, tested once in xUnit. The frontend debounces search (250 ms) and aborts superseded requests. |
| Money on the client | Integer minor units (`priceMinor` from the API) | No float accumulation. Tax uses basis points (`subtotal × 800 / 10 000`) so the result is exact before rounding. |
| Money on the server | `decimal`, `MidpointRounding.AwayFromZero` | Matches `Math.round` for positive amounts, so both sides agree to the cent. |
| Stale cart prices | Client must send `expectedTotal` (the total the shopper saw); server returns **409** with a fresh quote if it differs, and **400** if it's missing | A cart saved last week can't be charged at last week's prices, and the server never charges an amount the shopper wasn't shown. `expectedTotal` is never used as a price. |
| Pricing rules | Served by `GET /api/store/config` | The frontend never hard-codes tax or shipping (it has defaults only until the config loads). |
| Decrement at quantity 1 | **Prevented**; the "−" button is `aria-disabled` and shows a hint pointing to **Remove** | A mis-tap can't silently delete a line. Removal is always explicit. |
| Errors | RFC 7807 ProblemDetails with a machine-readable `code` | The UI chooses a recovery action by `code`, not by parsing text. |

### Pricing policy

All rules come from the `Pricing` section of `appsettings.json` (defaults shown). They're demonstration values, not a claim about any jurisdiction's tax law.

1. Unit prices are rounded to 2 dp; **line total = unit price × quantity**.
2. **Subtotal** = sum of line totals (merchandise only).
3. **Tax** = subtotal × 8 %, rounded **once** at the end (not per line). Shipping is **not** taxed.
4. **Shipping** = $5.99 when 0 < subtotal < $50.00. It's free when the **pre-tax** subtotal is **at or above** $50.00, and $0 for an empty cart.
5. **Total** = subtotal + tax + shipping.

| Cart | Subtotal | Tax | Shipping | Total |
|---|---:|---:|---:|---:|
| empty | 0.00 | 0.00 | 0.00 | **0.00** |
| 1 × 22.30 | 22.30 | 1.78 | 5.99 | **30.07** |
| 1 × 49.99 (tax pushes total over 50, still pays shipping) | 49.99 | 4.00 | 5.99 | **59.98** |
| 1 × 50.00 | 50.00 | 4.00 | 0.00 | **54.00** |
| 3 × 22.30 | 66.90 | 5.35 | 0.00 | **72.25** |
| 3 × 9.95 (tax rounded once → 2.39, not 3 × 0.80) | 29.85 | 2.39 | 5.99 | **38.23** |

**Currency.** The default is **USD** (`en-US` formatting) because FakeStoreAPI's prices read like dollars, but nothing about the data guarantees it. Change `Pricing:Currency` and `Pricing:Locale` (for example `ZAR` / `en-ZA`); the frontend picks them up from `/api/store/config`. Only currencies with two minor-unit digits are supported.

**Badges** (first match wins): **Top Rated** for rating ≥ 4.5 from ≥ 100 reviews; **Popular** for ≥ 400 reviews; **Best Value** for price ≤ 25.00 with rating ≥ 3.5.

---

## 2. Folder structure

```
storefront/
├── README.md
├── Documentation/
│   ├── Backend.md                   # backend design: layers, auth, database, idempotency, error codes
│   ├── Frontend.md                  # frontend design: API layer, sessions, checkout outcomes, responsive/a11y
│   ├── Testing.md                   # test strategy, commands, coverage reports, results, known gaps
│   ├── Testing/ManualTestPlan.md    # checklist for human testing (devices, screen readers, zoom)
│   └── Audits/                      # AUDIT.md (audit report) and storefront-audit.patch
├── .gitignore
├── tools/
│   ├── fakestore_mock.py            # optional offline stand-in for FakeStoreAPI, with failure modes
│   └── smoke_test.py                # end-to-end API checks (endpoints, repricing, idempotency, accounts, CORS, Swagger, outages)
├── backend/
│   ├── Storefront.slnx
│   ├── .config/dotnet-tools.json    # pins dotnet-ef and ReportGenerator (dotnet tool restore)
│   ├── coverage.runsettings         # what backend coverage measures and excludes
│   ├── Storefront.Api/
│   │   ├── Storefront.Api.csproj
│   │   ├── Storefront.Api.http      # example requests
│   │   ├── Program.cs               # DI, options validation, HttpClient, auth, rate limits, CORS, ProblemDetails, Swagger
│   │   ├── appsettings.json / appsettings.Development.json
│   │   ├── Properties/launchSettings.json
│   │   ├── Configuration/           # FakeStoreOptions, PricingOptions, CorsSettings, DatabaseOptions
│   │   ├── Controllers/             # Products, Categories, Checkout, Orders, Store (thin)
│   │   ├── Data/                    # StorefrontDbContext, Entities/, Configurations/, Migrations/, setup
│   │   ├── Security/                # Identity setup, policies, rate limits, security headers
│   │   ├── DTOs/                    # request/response contracts
│   │   ├── Exceptions/              # Upstream, validation, unavailable, price-changed, idempotency
│   │   ├── Middleware/              # GlobalExceptionHandler → ProblemDetails
│   │   ├── Models/                  # upstream shape, normalised catalogue, price breakdown
│   │   └── Services/                # FakeStoreClient, catalogue, categories, badges, pricing,
│   │                                #   checkout validation, checkout, order queries, order references
│   └── Storefront.Api.Tests/        # xUnit
│       ├── Catalogue/  Checkout/  Configuration/  FakeStore/  Pricing/  TestSupport/
│       ├── Persistence/             # real SQLite: snapshots, idempotency, concurrency, constraints
│       └── Integration/             # whole API in memory: auth, ownership, restarts, errors, headers
└── frontend/
    ├── package.json  vite.config.ts  tsconfig*.json  index.html  .env.example
    ├── playwright.config.ts         # browser tests: starts mock, API (fresh SQLite) and Vite on ports 9101/5081/5174
    ├── e2e/                         # shopper-journeys, responsive-accessibility specs; support/ helpers
    ├── public/favicon.svg
    └── src/
        ├── main.tsx  App.tsx  App.test.tsx  vite-env.d.ts
        ├── components/   # Navbar, SearchField, CategoryNav, ProductCard, ProductDetailsDialog (+test), SortSelect,
        │                 # StatusMessage, CartDrawer, CartLineItem, QuantityStepper, OrderSummary,
        │                 # CheckoutDialog (+test), CheckoutForm, OrderConfirmation,
        │                 # AccountDialog, AuthForm, MyOrders (Account.test), Icons
        ├── pages/        # CatalogPage
        ├── context/      # cartReducer (+test), CartContext, SessionContext, StoreConfigContext, ToastContext
        ├── hooks/        # useProducts, useCategories, useCatalogQuery, useDebouncedValue, useModalBehavior
        ├── services/     # apiClient (+test), productService, checkoutService, accountService, responseParsers
        ├── types/        # product, cart, checkout, store, account
        ├── utils/        # pricing (+test), money, cartStorage (+test), sessionStorage, checkoutValidation
        ├── config/       # env
        ├── styles/       # tokens, base, layout, catalog, cart, checkout
        └── test/         # setup, fixtures (fake API)
```

**Deviations from the suggested layout.** The backend tests are a separate project (`backend/Storefront.Api.Tests`) rather than a `Tests/` folder inside the API, because an SDK-style web project would otherwise compile the tests into the API. Frontend tests sit next to the code they test. `Exceptions/` was added so domain failures are typed and mapped in one place.

---

## 3. Prerequisites and installation

| Tool | Version |
|---|---|
| .NET SDK | **10.0** (LTS). Check with `dotnet --version`. |
| Node.js | **22.12+** or 20.19+ (required by Vite 8). Check with `node -v`. |
| npm | 10+ |
| Python | 3.9+ (only for `tools/`; standard library only) |

```bash
# Backend
cd storefront/backend
dotnet restore
dotnet tool restore            # installs dotnet-ef 10.0.13 for this repo (for migrations)

# Frontend
cd ../frontend
npm install
cp .env.example .env.local     # optional, the defaults already point at http://localhost:5080
```

**Packages.** Backend: `Swashbuckle.AspNetCore` 10.x (Swagger UI; v10 is the line that supports ASP.NET Core 10, v9 supports 8.0/9.0 only), `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Design` (design-time only), all 10.0.13. Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`. Frontend runtime: `react`, `react-dom` only. Frontend dev: `vite`, `@vitejs/plugin-react`, `typescript`, `vitest`, `jsdom`, `@testing-library/*`, `@types/*`. React Router isn't used because the app is a single page and its view state (search, category, sort) lives in the URL query string.

---

## 4. Running locally

Run the two halves in separate terminals.

```bash
# Terminal 1: API on http://localhost:5080
# Swagger UI: http://localhost:5080/swagger   OpenAPI JSON: http://localhost:5080/swagger/v1/swagger.json
# (Swagger is mapped only when ASPNETCORE_ENVIRONMENT=Development, which the "http" profile sets.)
cd storefront/backend/Storefront.Api
dotnet run --launch-profile http

# Terminal 2: frontend on http://localhost:5173
cd storefront/frontend
npm run dev
```

Open **http://localhost:5173**. Health checks: `http://localhost:5080/health/live` (process up) and `http://localhost:5080/health/ready` (database reachable and migrated; catalogue state). See `Documentation/Availability.md`.

### Database

In Development the API applies pending migrations when it starts, so `dotnet run` is enough: the first run creates `backend/Storefront.Api/App_Data/storefront.db`. To create or update the database yourself (and in any non-Development environment):

```bash
cd storefront/backend
dotnet tool restore
dotnet ef database update --project Storefront.Api
```

- **Inspect** the file with any SQLite viewer (for example "DB Browser for SQLite", or the SQLite extension for VS Code). Tables: `Orders`, `OrderLines`, `AspNetUsers` (password hashes only, never plain text) and the other Identity tables.
- **Reset** local data: stop the API, delete `backend/Storefront.Api/App_Data/storefront.db`, then start the API again (or run `dotnet ef database update`). The file is git-ignored and never committed.
- **Change the model**: edit an entity or its configuration in `Data/`, then `dotnet ef migrations add <Name> --project Storefront.Api --output-dir Data/Migrations`, and commit the generated migration. A test fails if the model and migrations ever drift apart.

### Accounts (optional)

In the app, select **Sign in** in the header, then **Create an account**. Orders placed while signed in appear under **Account → Orders**; you can also sign in from checkout and come back to your order with your details kept. Guest checkout always works.

The same endpoints work from Swagger (`/swagger`, the **Account** section) or any HTTP client:

```bash
curl -X POST http://localhost:5080/api/account/register -H "Content-Type: application/json" -d '{"email":"ada@example.com","password":"Str0ng!Passw0rd"}'
curl -X POST http://localhost:5080/api/account/login    -H "Content-Type: application/json" -d '{"email":"ada@example.com","password":"Str0ng!Passw0rd"}'
# -> {"tokenType":"Bearer","accessToken":"...","expiresIn":3600,"refreshToken":"..."}
curl http://localhost:5080/api/orders -H "Authorization: Bearer <accessToken>"
```

Passwords need at least 8 characters with an upper-case letter, a lower-case letter, a digit and a symbol. Five wrong passwords lock the account for 5 minutes.

**Running offline, or testing failures.** Start the stand-in and point the API at it:

```bash
python3 tools/fakestore_mock.py 9100
FakeStore__BaseUrl=http://localhost:9100/ dotnet run --launch-profile http       # PowerShell: $env:FakeStore__BaseUrl="http://localhost:9100/"
curl http://localhost:9100/__mode/500        # also: slow, malformed, ok
```

Set `FakeStore__CatalogueCacheSeconds=0` when testing failures, so a cached catalogue doesn't hide them.

The mock's modes, switched with `GET /__mode/<mode>`: `ok` (default), `500` (every call fails), `slow` (sleeps 15 s, longer than the API's 10 s timeout, so you get 504), and `malformed` (returns HTML instead of JSON).

### Tests

Full details, categories and coverage reports: [`Documentation/Testing.md`](Documentation/Testing.md).

```bash
# Backend: needs network access to nuget.org for the first restore
cd storefront/backend
dotnet restore
dotnet tool restore                          # dotnet-ef and ReportGenerator
dotnet build -c Release
dotnet test -c Release                       # 190 xUnit tests; add --filter Category=Unit|Database|Api to run one group

# Frontend
cd storefront/frontend
npm ci
npm run typecheck                            # strict tsc -b (app, tests and browser tests)
npm test                                     # 230 Vitest tests (npm run test:unit / test:component for one group)
npm run test:coverage                        # coverage report in coverage/index.html, with thresholds
npm run build                                # type-check + production bundle

# Browser journeys (Playwright): starts its own API, database, mock and Vite on separate ports
npx playwright install chromium              # first time only
npm run test:e2e                             # 16 tests; report: npm run test:e2e:report
```

### End-to-end API smoke test

`tools/smoke_test.py` checks a running API: every endpoint, Swagger, 200 random checkouts repriced against the documented policy, validation errors, request-size limits, idempotent retries, accounts and protected orders, CORS (including on error responses), health endpoints and correlation IDs, and, with `--mock`, upstream 500, timeout and malformed-data handling (browsing from the saved catalogue, checkout failing closed).

It sends a few hundred checkouts, far more than the normal rate limit (30 a minute per client), so start the API with higher limits for the run:

```bash
# Offline, with failure modes (three terminals):
python3 tools/fakestore_mock.py 9100
cd backend/Storefront.Api && FakeStore__BaseUrl=http://localhost:9100/ FakeStore__CatalogueCacheSeconds=0 RateLimiting__CheckoutPermitLimit=10000 RateLimiting__AccountPermitLimit=1000 dotnet run --launch-profile http
python3 tools/smoke_test.py --api http://localhost:5080 --mock http://localhost:9100

# Live FakeStoreAPI (default configuration; upstream failure checks are skipped):
python3 tools/smoke_test.py --api http://localhost:5080
```

The exit code is non-zero if any check fails.

---

## 5. Configuration

**Backend** (`appsettings.json`, overridable with environment variables using `__`, e.g. `Pricing__Currency=ZAR`):

```json
{
  "AllowedHosts": "localhost;127.0.0.1",
  "ConnectionStrings": { "Storefront": "Data Source=App_Data/storefront.db" },
  "Database": { "ApplyMigrationsOnStartup": false },
  "RateLimiting": { "AccountPermitLimit": 10, "CheckoutPermitLimit": 30, "WindowSeconds": 60 },
  "FakeStore": {
    "BaseUrl": "https://fakestoreapi.com/", "TimeoutSeconds": 10, "AttemptTimeoutSeconds": 4,
    "RetryCount": 2, "RetryBaseDelayMilliseconds": 200, "CatalogueCacheSeconds": 300, "StaleCatalogueMaxMinutes": 60
  },
  "Hosting": { "ShutdownTimeoutSeconds": 30 },
  "Pricing": {
    "Currency": "USD", "Locale": "en-US", "TaxRate": 0.08,
    "FlatShippingFee": 5.99, "FreeShippingThreshold": 50.00,
    "MaxQuantityPerLine": 99, "MaxLineItems": 50
  },
  "Cors": { "AllowedOrigins": [ "http://localhost:5173" ] }
}
```

Options are validated at startup (`ValidateOnStart`), so an invalid URL, a tax rate with more than 4 decimal places, or a negative fee stops the app with a clear message.

- **Database:** a relative `Data Source` is resolved against the `Storefront.Api` folder, wherever the app is started from. `ApplyMigrationsOnStartup` is `true` in `appsettings.Development.json` only.
- **Rate limits** are per client IP and per window: `AccountPermitLimit` covers all `/api/account/*` endpoints, `CheckoutPermitLimit` covers `POST /api/checkout`. Over the limit returns 429 with `Retry-After`.
- **AllowedHosts** rejects requests for other host names (400). Set your real host name in production, for example `AllowedHosts=shop.example.com`, and include the host name a health probe uses.
- **FakeStore retries and fallback, shutdown and logging:** see `Documentation/Availability.md`. Logs are JSON outside Development and readable text in Development.

**CORS in other environments.** `Cors:AllowedOrigins` has no built-in default in code, so configured origins replace the development ones rather than adding to them. In production, set your real origin(s), for example `Cors__AllowedOrigins__0=https://shop.example.com`. Development (`appsettings.Development.json`) also allows `http://127.0.0.1:5173`. Swagger is not mapped outside Development.

**Frontend** (`frontend/.env.example`). Only `VITE_*` variables reach the browser, and everything in them is public, so never put secrets there.

```
VITE_API_BASE_URL=http://localhost:5080
VITE_DEFAULT_CURRENCY=USD
VITE_DEFAULT_LOCALE=en-US
```

No secrets are needed anywhere: FakeStoreAPI is public, there is no payment provider, and SQLite needs no password. Identity's tokens are signed with ASP.NET Core's Data Protection keys, which are created automatically in your user profile; in production they must be persisted and protected (see `Documentation/Backend.md`).

---

## 6. API reference with examples

All errors use `application/problem+json` with `status`, `title`, `code`, `traceId` and, where relevant, `detail`, `errors` or extra fields.

| Method and path | Purpose | Success | Errors |
|---|---|---|---|
| `GET /api/products` | List with `search`, `category` (slug, group or `all`), `sort` (`featured`, `price-asc`, `price-desc`, `rating`), `minPrice`, `maxPrice` | 200 | 400 `VALIDATION_FAILED`, 502 `UPSTREAM_UNAVAILABLE` / `UPSTREAM_MALFORMED`, 504 `UPSTREAM_TIMEOUT` |
| `GET /api/products/{id}` | One product | 200 | 400, 404 `NOT_FOUND`, 502, 504 |
| `GET /api/categories` | Normalised categories with counts and groups | 200 | 502, 504 |
| `GET /api/store/config` | Currency and pricing rules | 200 | none |
| `POST /api/checkout` | Places and saves an order (no payment). Public; a bearer token links the order to that account, and an invalid or expired token is rejected with 401 rather than becoming a guest order. Body: `customer`, `items[]` (`productId`, `quantity`), `expectedTotal` (required). Optional `Idempotency-Key` header | 200 (`Idempotent-Replayed: true` on a repeat) | 400 `VALIDATION_FAILED`, 401 `UNAUTHORIZED`, 409 `PRICE_CHANGED`, 413 `REQUEST_TOO_LARGE` (body over 64 KiB), 415, 422 `PRODUCTS_UNAVAILABLE` / `IDEMPOTENCY_KEY_REUSED`, 429 `TOO_MANY_REQUESTS`, 502, 504 |
| `GET /api/orders` | **Signed in.** Your orders, newest first (up to 100) | 200 | 401 `UNAUTHORIZED` |
| `GET /api/orders/{reference}` | **Signed in.** One of your orders, as stored at checkout | 200 | 401, 404 `NOT_FOUND` (also for someone else's order) |
| `POST /api/account/register`, `/login`, `/refresh` | ASP.NET Core Identity endpoints (also `/manage/info`, `/forgotPassword`, `/resetPassword`, 2FA) | 200 | 400 `VALIDATION_FAILED`, 401, 429 `TOO_MANY_REQUESTS` |

Every response also carries `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` and a `Content-Security-Policy` that allows nothing (JSON only). The full list of error codes is in [`Documentation/Backend.md`](Documentation/Backend.md#error-handling).

**`GET /api/products?category=clothing&sort=price-asc&search=women`** returns (truncated):

```json
{
  "items": [
    {
      "id": 19, "title": "Opna Women's Short Sleeve Moisture", "description": "…",
      "price": 7.95, "priceMinor": 795,
      "category": "womens-clothing", "categoryName": "Women's Clothing", "categoryGroup": "clothing",
      "image": "https://fakestoreapi.com/img/…jpg",
      "rating": { "rate": 4.5, "count": 146 },
      "badge": { "code": "top-rated", "label": "Top Rated" }
    }
  ],
  "count": 6
}
```

**`GET /api/categories`** returns:

```json
[
  { "slug": "electronics", "name": "Electronics", "productCount": 6, "parentSlug": null, "isGroup": false },
  { "slug": "jewelry", "name": "Jewelry", "productCount": 4, "parentSlug": null, "isGroup": false },
  { "slug": "clothing", "name": "Clothing", "productCount": 10, "parentSlug": null, "isGroup": true },
  { "slug": "mens-clothing", "name": "Men's Clothing", "productCount": 4, "parentSlug": "clothing", "isGroup": false },
  { "slug": "womens-clothing", "name": "Women's Clothing", "productCount": 6, "parentSlug": "clothing", "isGroup": false }
]
```

**`POST /api/checkout`**, a successful order:

```json
{
  "customer": { "name": "Ada Lovelace", "email": "ada@example.com" },
  "items": [ { "productId": 2, "quantity": 2 }, { "productId": 19, "quantity": 1 } ],
  "expectedTotal": 56.75
}
```

```json
{
  "orderReference": "DEMO-20261022-TQYCDB",
  "createdAt": "2026-10-22T10:41:33.0054102+00:00",
  "customerName": "Ada Lovelace",
  "customerEmail": "ada@example.com",
  "lines": [
    { "productId": 2, "title": "Mens Casual Premium Slim Fit T-Shirts", "unitPrice": 22.3, "unitPriceMinor": 2230, "quantity": 2, "lineTotal": 44.6 },
    { "productId": 19, "title": "Opna Women's Short Sleeve Moisture", "unitPrice": 7.95, "unitPriceMinor": 795, "quantity": 1, "lineTotal": 7.95 }
  ],
  "totals": { "subtotal": 52.55, "tax": 4.20, "shipping": 0, "total": 56.75, "currency": "USD" },
  "isSimulated": true,
  "notice": "This is a demo checkout. No payment was taken. Your order has been saved."
}
```

When the client's total is stale, the response is **409**:

```json
{
  "status": 409, "title": "Prices have changed.", "code": "PRICE_CHANGED",
  "detail": "Your cart has been re-priced. Review the new total and confirm your order.",
  "expectedTotal": 1.00,
  "quote": {
    "lines": [ { "productId": 2, "title": "…", "unitPrice": 22.3, "unitPriceMinor": 2230, "quantity": 1, "lineTotal": 22.3 } ],
    "totals": { "subtotal": 22.3, "tax": 1.78, "shipping": 5.99, "total": 30.07, "currency": "USD" }
  },
  "traceId": "…"
}
```

An invalid request returns **400**:

```json
{
  "status": 400, "title": "The checkout request is invalid.", "code": "VALIDATION_FAILED",
  "errors": {
    "customer.name": ["Name must be between 2 and 100 characters."],
    "customer.email": ["Enter a valid email address."],
    "items[0].quantity": ["Quantity must be between 1 and 99."],
    "items[1].productId": ["Product 2 appears more than once."]
  }
}
```

An unknown product returns **422**:

```json
{ "status": 422, "title": "Some products are no longer available.", "code": "PRODUCTS_UNAVAILABLE", "unavailableProductIds": [404] }
```

A missing `expectedTotal` returns **400** with `"errors": { "expectedTotal": ["Send the order total shown to the customer."] }`. Over 50 lines returns only the line-count error, so error responses stay small.

Any extra fields the client sends (such as `unitPrice`) are ignored; the server always prices from the catalogue. The examples in this section were captured against `tools/fakestore_mock.py`, so titles and counts can differ slightly from live FakeStoreAPI data. More requests are in `backend/Storefront.Api/Storefront.Api.http`.

---

## 7. How the UI handles failure

| Situation | What the user sees |
|---|---|
| API or FakeStoreAPI down on first load | An error panel with the reason in shopper terms, a **Reference** for 5xx, and **Try again**. Filters still render (fallback categories). |
| FakeStoreAPI down after the catalogue loaded once | Products show from the API's saved copy (up to 60 minutes old) with a notice and **Refresh**. Checkout still fails closed. |
| Browser offline | A banner says so; checkout sends nothing and says the order hasn't been sent; the catalogue reloads once on reconnect if the last load failed. |
| Failure while refining a search | The previous results stay, with an inline alert and **Try again**. |
| Corrupt, unknown-version or partly invalid saved cart | Invalid data is discarded or repaired, a notice is shown, and the app never crashes. |
| Checkout network error, 5xx, timeout or unreadable response | "We couldn't confirm whether your order went through." The cart is kept and **Try again** resubmits with the same `Idempotency-Key`: if the first attempt did reach the server, that order is shown (with a note that it wasn't placed twice) instead of a second one being created. The key stays pinned across a reload and even if the details change, until the server confirms an order or reports the key reused. Nothing is resubmitted automatically. |
| 429 rate limited | "Too many requests. Wait N seconds" (from `Retry-After`); the cart is kept. |
| 413 request too large / 503 database busy or unavailable | A plain explanation and **Try again**. On checkout the API only sends these 503s when COMMIT was never attempted, so nothing was saved; a failed COMMIT is a 500 and shows the "couldn't confirm" message. A 503 is **not** a no-change guarantee on account endpoints: after a registration error the shopper is told to try signing in first. |
| Signed-in token expired during checkout | Renewed once and the same order resent. If the session can't be renewed: "Your session expired, so this order hasn't been placed", with **Sign in again** or **Place order as a guest**. |
| 422 idempotency key reused | "An earlier attempt at this order may already have been placed. Check before placing it again." A new key is used for the next attempt. |
| Saved session rejected on load, or orders return 401 | The session is renewed once; if that fails the shopper is signed out with a notice. Nothing redirects, so there's no loop. |
| Wrong password, locked account, duplicate email, password rules | Shown on the field it belongs to (or above the button), with the typed values kept. |
| Product link to a missing product | "This product is no longer in the catalogue." |
| 409 price change | Cart prices update from the server quote, the new total is shown, and the button becomes **Confirm new total and place order**. |
| 422 unavailable products | The affected items are named, with **Remove unavailable items**. |
| 400 validation | Field errors appear under the inputs. |

Accessibility: semantic landmarks, a skip link and one `h1` per view with `h2` product titles; the drawer and dialogs use `role="dialog"` and `aria-modal`, move focus in, trap Tab, close on Escape or backdrop click, and return focus to the trigger. Only one overlay is open at a time. The page behind is made `inert` and scroll-locked. Focus rings are visible, live regions announce toasts, quantities and result counts, and form errors are tied to their fields with `aria-describedby`. `prefers-reduced-motion` disables transitions and the skeleton shimmer.

Responsive: layouts were checked at 320, 375, 768, 1024 and 1440 px wide with no horizontal scrolling. On phones, dialogs become bottom sheets sized with `dvh`, respect the safe-area inset, and checkout scrolls its message and buttons into view. Toasts let taps pass through and are cleared when the cart or checkout opens.

---

## 8. Troubleshooting

**CORS error in the browser console** ("No 'Access-Control-Allow-Origin' header").
- The frontend must run on exactly `http://localhost:5173` (Vite is set to `strictPort`). If you use another origin, such as `127.0.0.1` or a different port, add it to `Cors:AllowedOrigins`. Development already allows `127.0.0.1:5173`.
- Make sure `VITE_API_BASE_URL` matches the API URL exactly (`http`, not `https`), then restart `npm run dev` after editing `.env.local`.
- A CORS error can also hide a crashed or stopped API. Check that `http://localhost:5080/health/live` loads.

**"We couldn't load the catalogue", a "recently saved copy" notice, or 502/504 from the API.**
- FakeStoreAPI is occasionally slow or down. Try `curl https://fakestoreapi.com/products`. Raise `FakeStore:TimeoutSeconds` if your network is slow, or run `tools/fakestore_mock.py` and point `FakeStore__BaseUrl` at it.
- Behind a corporate proxy, set `HTTPS_PROXY` for the API process.

**Database errors.**
- `SQLite Error 1: 'no such table: Orders'` (500 `DATABASE_ERROR`): migrations haven't run, which happens outside Development. Run `dotnet ef database update --project Storefront.Api` from `backend/`.
- 503 `DATABASE_BUSY`: another process (often a SQLite viewer holding a write transaction) has the file locked. Close it and retry.
- 503 `DATABASE_UNAVAILABLE`: the SQLite file can't be opened (missing folder, permissions or disk). `/health/ready` also reports 503.
- A shopper quoted a **Reference**: search the API log for it (`Documentation/Availability.md`, section 5).
- To start over, stop the API and delete `backend/Storefront.Api/App_Data/storefront.db`.

**Signed out unexpectedly, or "Your session has expired".** Sessions last for the browser tab; closing the tab signs you out. Restarting the API keeps sessions only if its Data Protection keys survive (they do by default on a developer machine).

**401 on `/api/orders`.** Send the `accessToken` from `/api/account/login` as `Authorization: Bearer <token>`. Tokens expire after an hour; use `/api/account/refresh` or sign in again. If you clear your user profile's Data Protection keys, existing tokens stop working.

**429 Too Many Requests.** The per-client limits are 10 account requests and 30 checkouts per minute. Raise them with `RateLimiting__AccountPermitLimit` and `RateLimiting__CheckoutPermitLimit` for load or smoke testing.

**Port already in use.** Change `applicationUrl` in `Properties/launchSettings.json` (and `VITE_API_BASE_URL`), or `server.port` in `vite.config.ts` (and `Cors:AllowedOrigins`).

**`dotnet restore` fails or can't find a package version.** Run `dotnet --version` and confirm it's 10.x. If a pinned version isn't available on your feed, update it with, for example, `dotnet add package Swashbuckle.AspNetCore` in `Storefront.Api`.

**`npm install` fails.** Check `node -v` (22.12+ or 20.19+). Delete `node_modules` and retry with `npm ci`. Behind a proxy, set `npm config set proxy …`.

**Fonts look different.** The UI loads Bricolage Grotesque and Public Sans from Google Fonts and falls back to system fonts when they're blocked.

---

## 9. Verification status

**Current version (live availability pass).** Verified on 23 October 2026 in a Linux workspace (same tools as below). These are test results, not uptime evidence: nothing has been deployed. Details are in `Documentation/Availability.md` and `Documentation/Testing.md`.

| Check | Result |
|---|---|
| Backend `dotnet build -c Release` (0 warnings), `dotnet test -c Release` | **Passed: 255 of 255** (unit 132, database 28, API 95), 0 skipped. Coverage 96.1% of lines (1257/1308) and 88.7% of branches (515/580), measured before the last added test |
| Frontend `npm run typecheck`, `npm run build`, `npm run test:coverage` | **Passed: 260 of 260** (unit 181, component 79), 0 skipped. Coverage 96.3% of lines (1044/1084) and 90.06% of branches (879/976); thresholds met |
| Browser `npm run test:e2e` | **Passed: 18 of 18**, including the outage, offline and 320 px/axe availability checks |
| `tools/smoke_test.py --mock` | **Passed: 70 of 70**, 0 skipped |
| Deliberate breakages | Disabling the reconnect reload and offline-checkout guard made 3 frontend tests fail; restoring the old single-step checkout save made 4 backend tests fail (a committed order was reported as "not saved"); turning off the error-response log line made 8 fail |
| Production-mode manual run | JSON log lines with `CorrelationId`; SIGTERM logged stopping and stopped, and the port closed |
| Owner's Windows machine for this version | **Passed** (23 October 2026, reported by the owner): `dotnet test -c Release`, `npm run test:coverage`, `npm run test:e2e`, after two test-only fixes the first run exposed: a culture-dependent JSON helper and a too-short component test timeout (see `Documentation/Testing.md`) |

**Previous version (testing and QA pass).**

Verified on 23 October 2026 in a Linux workspace (.NET SDK 10.0.401, Node 22.22.0, Chromium 141 with Playwright 1.64.0). Details, coverage and exclusions are in `Documentation/Testing.md`.

| Check | Result |
|---|---|
| Backend `dotnet build -c Release`, `dotnet test -c Release` | **Passed: 190 of 190** (unit 104, database 28, API 58), 0 skipped. Coverage 96.3% of lines (1030/1069) and 88.1% of branches (430/488), up from 95.9% and 87.2% |
| Frontend `npm run typecheck`, `npm run build`, `npm run test:coverage` | **Passed: 230 of 230** (unit 164, component 66), 0 skipped. Coverage 96.8% of lines (975/1007) and 90.2% of branches (823/912), up from 92.55% and 81.65%; thresholds met |
| Browser `npm run test:e2e` | **Passed: 16 of 16**: 8 shopper journeys against the real API and a fresh database, layout at 320–1440 px, axe WCAG 2.2 A/AA scan (0 violations), keyboard paths, reduced motion |
| Deliberate breakages | Frontend 21 of 21 caught; the backend's new safeguards and the browser idempotency journey each fail when broken |
| Defects found and fixed | Controller errors were labelled `application/json` instead of `application/problem+json`; the idempotency race-recovery path had never run under test; the sign-in form overflowed a 320 px screen |
| `tools/smoke_test.py --mock` | **Passed: 59 of 59** against the rebuilt API (Swagger, error codes and content types included) |
| Owner's Windows machine for this version | **Passed** (23 October 2026, reported by the owner): `dotnet tool restore`, `dotnet test -c Release`, `npm ci`, `npm test`, `npm run test:coverage`, `npx playwright install chromium`, `npm run test:e2e` |
| Manual test plan | **Not yet carried out**; checklist in `Documentation/Testing/ManualTestPlan.md` |

**Previous version (frontend accounts, product details, responsive and error-handling pass).** Verified on 23 October 2026 in a Linux workspace (.NET SDK 10.0.401, Node 22.22.0, headless Chromium) against the real API backed by `tools/fakestore_mock.py`:

| Check | Result |
|---|---|
| Frontend `npm run typecheck`, `npm run build` | **Passed**: strict `tsc -b`, production bundle 287 KB JS (87 KB gzip). No lint tool is configured in this repo. |
| Frontend `npm test` | **Passed: 98 of 98** (was 56). New tests cover sign-in, registration errors, session restore and expiry, token renewal, order history, signed-in and expired-session checkout, uncertain outcomes, 429/413/422, product details. 11 of 13 deliberate breakages were caught; the other 2 are redundant safeguards (see `Documentation/Frontend.md`). |
| Backend `dotnet build -c Release`, `dotnet test -c Release` | **Passed: 161 of 161** (adds: checkout with an invalid token returns 401 and saves nothing; mutation-checked) |
| `tools/smoke_test.py --mock` | **Passed: 59 of 59** (adds: token refresh, invalid token on checkout) |
| Browser flows at 1280, 768 and 375 px | **Passed 16 of 16 checks at each size**: product details and shared links, register, signed-in checkout, order history, session restored after reload, interrupted checkout recovered without a duplicate, sign-out, duplicate email, wrong password, rejected saved session. No console errors other than the requests the tests failed on purpose. |
| Layout at 320, 375, 768, 1024, 1440 px | **Passed**: no horizontal overflow on catalogue, cart or checkout |
| Keyboard | **Passed**: tab order from the top of the page, focus moved into and back out of every dialog, Tab trapped, Escape closes, visible focus rings, reduced motion honoured |
| Owner's Windows machine and live FakeStoreAPI for this version | **Not yet run** (see the previous version's results below) |

**Previous version (database, accounts, idempotency).** Verified on 22 October 2026 in a Linux workspace with .NET SDK 10.0.401 (runtime 10.0.12), Node 22.22.0, and NuGet packages restored on the project owner's Windows machine:

| Check | Result |
|---|---|
| `dotnet restore`, `dotnet build -c Release` | **Passed**: 0 warnings, 0 errors (`TreatWarningsAsErrors` on) |
| `dotnet ef migrations add`, `dotnet ef database update` on an empty folder | **Passed**: creates `App_Data/storefront.db` with `Orders`, `OrderLines` and the Identity tables |
| `dotnet test -c Release` (real xUnit) | **Passed: 160 of 160**, 0 skipped. Includes persistence, concurrency, constraint, authorization, restart and error-path tests. Key tests were mutation-checked (each fails when its safeguard is removed). |
| Persistence across a real restart | **Passed**: an order placed by a signed-in user was still listed after stopping and restarting `dotnet run` |
| `tools/smoke_test.py --mock` | **Passed: 57 of 57**, including Swagger, idempotency, accounts and protected orders |
| Frontend `tsc -b`, `npm run build`, `npm test` | **Passed**: 56 Vitest tests |
| Browser (headless Chromium, real API) | **Passed**: browse, cart, refresh, checkout (the CORS preflight for `Idempotency-Key` included), 409 re-price, outage and retry, keyboard focus |
| Owner's Windows machine (.NET SDK 10.0.402, runtime 10.0.13) | **Passed**: `dotnet restore`, `dotnet tool restore`, `dotnet build -c Release`, `dotnet test -c Release` (**160 of 160**), and `dotnet run` created `App_Data\storefront.db` and applied the migration on first start |
| `tools/smoke_test.py` against the **live FakeStoreAPI** (Windows, raised rate limits) | **Passed: 51 of 51**, 1 skipped (upstream failure modes need `--mock`). Includes 200 checkouts repriced against live catalogue prices, idempotency, accounts and protected orders |

The audit of the previous version is in `Documentation/Audits/AUDIT.md`.

### Known limitations and risks

- **Pricing rules fallback.** If `/api/store/config` fails at page load, the cart uses built-in defaults. If the server is configured differently, every checkout returns 409 until the page is reloaded.
- **Catalogue cache.** Checkout prices are authoritative as of the last fetch (5 minutes by default). During an outage, browsing shows the last good copy (up to 60 minutes old, marked as such) but checkout fails closed (502) rather than pricing from it. The saved copy is in memory and lost on restart.
- Upstream GETs are retried (bounded, with backoff) but there is no circuit breaker. See `Documentation/Availability.md` for the full list of availability limitations.
- No high-availability or uptime claim: one process, one SQLite file, no deployment or monitoring evidence.
- The cart is per browser and not saved to the account, so it doesn't follow a shopper to another device.
- Signing out forgets the tokens in the browser, but Identity's bearer tokens can't be revoked server-side; an access token stays valid until it expires (1 hour).
- The refresh token is in sessionStorage, which script on the page could read if an XSS bug were introduced; see `Documentation/Frontend.md` for the trade-off.
- No password reset or account deletion in the UI (no email sender is configured).
- No email sending, so accounts aren't email-confirmed and password-reset emails aren't delivered.
- SQLite suits one server; see `Documentation/Backend.md` for the production checklist (database, Data Protection keys, forwarded headers, hosts).
- Only two-decimal currencies are supported. Catalogue search and filters are server-side, so they need the API to be running.
