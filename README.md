# Greenhaus: full-stack e-commerce catalogue and cart

A demo storefront with a React + TypeScript frontend and an ASP.NET Core Web API backend-for-frontend over [FakeStoreAPI](https://fakestoreapi.com). It covers catalogue search, filtering and sorting, a persistent slide-out cart, exact money arithmetic, and a checkout that the server re-prices, re-validates and saves to a local SQLite database. Customers can create accounts and view their own orders through the API.

> Checkout is a **demo**: no payment is taken and no card details are requested. Orders are saved locally in `backend/Storefront.Api/App_Data/storefront.db`.

**Backend design** (layers, authentication, database, idempotency, error codes): see [`Documentation/Backend.md`](Documentation/Backend.md).

---

## 1. Architecture

```
 Browser (React, :5173)                  ASP.NET Core API (:5080)                    FakeStoreAPI
┌─────────────────────────────┐        ┌──────────────────────────────────┐        ┌──────────────┐
│ CatalogPage ── useProducts ─┼─GET───▶│ ProductsController               │        │ /products    │
│ Navbar / CategoryNav        │        │   └ ProductCatalogService ───────┼─GET───▶│ /products/id │
│ CartContext (useReducer)    │        │       ├ CategoryNormalizer       │        └──────────────┘
│   └ localStorage (v1)       │        │       └ ProductBadgeRules        │
│ CartDrawer / CheckoutDialog ┼─POST──▶│ CheckoutController (guest or     │
│   (Idempotency-Key header)  │        │   signed-in) └ CheckoutService ──┼──┐
│ utils/pricing (int cents)   │◀───────┤       ├ CheckoutRequestValidator │  │     SQLite (EF Core)
│ StoreConfigContext ─────────┼─GET───▶│       └ PricingCalculator        │  ├──▶ App_Data/storefront.db
└─────────────────────────────┘        │ OrdersController [Authorize]     │  │     Orders, OrderLines,
                                       │   └ OrderQueryService ───────────┼──┤     Identity users
 Any API client (Swagger, curl) ──────▶│ /api/account/* (Identity API)    ┼──┘
                                       │ GlobalExceptionHandler → ProblemDetails │
                                       └──────────────────────────────────┘
```

**Backend responsibilities.** Fetch and validate upstream data; normalise categories (`jewelery` → `jewelry`, men's and women's clothing grouped under `clothing`); compute badges; apply search, category, price range and sort; publish the pricing rules. At checkout: validate the request, look up authoritative prices, calculate totals with `decimal`, and save the order and its lines in one transaction. It also manages customer accounts (ASP.NET Core Identity), serves each signed-in customer their own orders, and returns structured errors.

**Frontend responsibilities.** Render the catalogue and keep filters in the URL; manage the cart in one reducer; persist it to localStorage; show estimated totals using the same rules as the server; run the checkout flow and recover from every failure without losing the cart.

### Key decisions

| Decision | Choice | Why |
|---|---|---|
| Cart ownership | **Client-managed cart in localStorage; server validates at checkout** (option 1 in the brief) | Refresh-survival is free, guests need no account, and the security boundary stays where it matters: the server never trusts client prices. `GET /api/cart` is therefore not implemented. |
| Database | **SQLite file + EF Core 10, with migrations** | Runs on any machine with no server to install. Orders snapshot titles and prices, so history survives catalogue changes. The catalogue itself stays upstream (FakeStoreAPI). |
| Accounts | **ASP.NET Core Identity API endpoints, bearer tokens** | Built-in password hashing, lockout and tokens; no custom JWT code. Guest checkout stays open, so the frontend works without signing in. |
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
│   └── Audits/                      # AUDIT.md (audit report) and storefront-audit.patch
├── .gitignore
├── tools/
│   ├── fakestore_mock.py            # optional offline stand-in for FakeStoreAPI, with failure modes
│   └── smoke_test.py                # end-to-end API checks (endpoints, repricing, idempotency, accounts, CORS, Swagger, outages)
├── backend/
│   ├── Storefront.slnx
│   ├── .config/dotnet-tools.json    # pins the dotnet-ef tool (dotnet tool restore)
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
    ├── public/favicon.svg
    └── src/
        ├── main.tsx  App.tsx  App.test.tsx  vite-env.d.ts
        ├── components/   # Navbar, SearchField, CategoryNav, ProductCard, SortSelect, StatusMessage,
        │                 # CartDrawer, CartLineItem, QuantityStepper, OrderSummary,
        │                 # CheckoutDialog (+test), CheckoutForm, OrderConfirmation, Icons
        ├── pages/        # CatalogPage
        ├── context/      # cartReducer (+test), CartContext, StoreConfigContext, ToastContext
        ├── hooks/        # useProducts, useCategories, useCatalogQuery, useDebouncedValue, useModalBehavior
        ├── services/     # apiClient, productService, checkoutService, responseParsers
        ├── types/        # product, cart, checkout, store
        ├── utils/        # pricing (+test), money, cartStorage (+test), checkoutValidation
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

Open **http://localhost:5173**. The health check is at `http://localhost:5080/health`.

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

The frontend checks out as a guest. To try accounts, use Swagger (`/swagger`, the **Account** section) or any HTTP client:

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

```bash
# Backend: needs network access to nuget.org for the first restore
cd storefront/backend
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build --logger "console;verbosity=normal"   # 160 tests; each test uses its own temporary SQLite database

# Frontend
cd storefront/frontend
npm ci
npm run typecheck      # strict tsc -b
npm test               # Vitest + Testing Library (jsdom)
npm run build          # type-check + production bundle
```

### End-to-end API smoke test

`tools/smoke_test.py` checks a running API: every endpoint, Swagger, 200 random checkouts repriced against the documented policy, validation errors, request-size limits, idempotent retries, accounts and protected orders, CORS (including on error responses), and, with `--mock`, upstream 500, timeout and malformed-data handling.

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
  "FakeStore": { "BaseUrl": "https://fakestoreapi.com/", "TimeoutSeconds": 10, "CatalogueCacheSeconds": 300 },
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
- **AllowedHosts** rejects requests for other host names (400). Set your real host name in production, for example `AllowedHosts=shop.example.com`.

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
| `POST /api/checkout` | Places and saves an order (no payment). Public; a bearer token links the order to that account. Body: `customer`, `items[]` (`productId`, `quantity`), `expectedTotal` (required). Optional `Idempotency-Key` header | 200 (`Idempotent-Replayed: true` on a repeat) | 400 `VALIDATION_FAILED`, 409 `PRICE_CHANGED`, 413 `REQUEST_TOO_LARGE` (body over 64 KiB), 415, 422 `PRODUCTS_UNAVAILABLE` / `IDEMPOTENCY_KEY_REUSED`, 429 `TOO_MANY_REQUESTS`, 502, 504 |
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
| API or FakeStoreAPI down on first load | An error panel with the reason and **Try again**. Filters still render (fallback categories). |
| Failure while refining a search | The previous results stay, with an inline alert and **Try again**. |
| Corrupt, unknown-version or partly invalid saved cart | Invalid data is discarded or repaired, a notice is shown, and the app never crashes. |
| Checkout network error, 5xx or timeout | The cart is kept and **Try again** resubmits with the same `Idempotency-Key`, so if the first attempt did reach the server, the original order is returned rather than a second one placed. |
| 409 price change | Cart prices update from the server quote, the new total is shown, and the button becomes **Confirm new total and place order**. |
| 422 unavailable products | The affected items are named, with **Remove unavailable items**. |
| 400 validation | Field errors appear under the inputs. |

Accessibility: semantic landmarks and a skip link; the drawer and dialog use `role="dialog"` and `aria-modal`, move focus in, trap Tab, close on Escape or backdrop click, and return focus to the trigger. The page behind is made `inert` and scroll-locked. Focus rings are visible, and live regions announce toasts, quantities and result counts. `prefers-reduced-motion` disables transitions and the skeleton shimmer.

---

## 8. Troubleshooting

**CORS error in the browser console** ("No 'Access-Control-Allow-Origin' header").
- The frontend must run on exactly `http://localhost:5173` (Vite is set to `strictPort`). If you use another origin, such as `127.0.0.1` or a different port, add it to `Cors:AllowedOrigins`. Development already allows `127.0.0.1:5173`.
- Make sure `VITE_API_BASE_URL` matches the API URL exactly (`http`, not `https`), then restart `npm run dev` after editing `.env.local`.
- A CORS error can also hide a crashed or stopped API. Check that `http://localhost:5080/health` loads.

**"We couldn't load the catalogue" or 502/504 from the API.**
- FakeStoreAPI is occasionally slow or down. Try `curl https://fakestoreapi.com/products`. Raise `FakeStore:TimeoutSeconds` if your network is slow, or run `tools/fakestore_mock.py` and point `FakeStore__BaseUrl` at it.
- Behind a corporate proxy, set `HTTPS_PROXY` for the API process.

**Database errors.**
- `SQLite Error 1: 'no such table: Orders'` (500 `DATABASE_ERROR`): migrations haven't run, which happens outside Development. Run `dotnet ef database update --project Storefront.Api` from `backend/`.
- 503 `DATABASE_BUSY`: another process (often a SQLite viewer holding a write transaction) has the file locked. Close it and retry.
- To start over, stop the API and delete `backend/Storefront.Api/App_Data/storefront.db`.

**401 on `/api/orders`.** Send the `accessToken` from `/api/account/login` as `Authorization: Bearer <token>`. Tokens expire after an hour; use `/api/account/refresh` or sign in again. If you clear your user profile's Data Protection keys, existing tokens stop working.

**429 Too Many Requests.** The per-client limits are 10 account requests and 30 checkouts per minute. Raise them with `RateLimiting__AccountPermitLimit` and `RateLimiting__CheckoutPermitLimit` for load or smoke testing.

**Port already in use.** Change `applicationUrl` in `Properties/launchSettings.json` (and `VITE_API_BASE_URL`), or `server.port` in `vite.config.ts` (and `Cors:AllowedOrigins`).

**`dotnet restore` fails or can't find a package version.** Run `dotnet --version` and confirm it's 10.x. If a pinned version isn't available on your feed, update it with, for example, `dotnet add package Swashbuckle.AspNetCore` in `Storefront.Api`.

**`npm install` fails.** Check `node -v` (22.12+ or 20.19+). Delete `node_modules` and retry with `npm ci`. Behind a proxy, set `npm config set proxy …`.

**Fonts look different.** The UI loads Bricolage Grotesque and Public Sans from Google Fonts and falls back to system fonts when they're blocked.

---

## 9. Verification status

**Current version (database, accounts, idempotency).** Verified on 22 October 2026 in a Linux workspace with .NET SDK 10.0.401 (runtime 10.0.12), Node 22.22.0, and NuGet packages restored on the project owner's Windows machine:

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
- **Catalogue cache.** Prices are authoritative as of the last fetch (5 minutes by default). When the cache expires during an outage, the store fails closed (502) rather than serving stale prices. Concurrent cold-cache requests each call FakeStoreAPI (no single-flight).
- No retry or circuit breaker on upstream calls (`Microsoft.Extensions.Http.Resilience` would be the next step).
- No login screen in the frontend: accounts work through the API (Swagger, curl), and the frontend checks out as a guest.
- No email sending, so accounts aren't email-confirmed and password-reset emails aren't delivered.
- SQLite suits one server; see `Documentation/Backend.md` for the production checklist (database, Data Protection keys, forwarded headers, hosts).
- Only two-decimal currencies are supported. Catalogue search and filters are server-side, so they need the API to be running.
