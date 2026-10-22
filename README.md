# Greenhaus: full-stack e-commerce catalogue and cart

A demo storefront with a React + TypeScript frontend and an ASP.NET Core Web API backend-for-frontend over [FakeStoreAPI](https://fakestoreapi.com). It covers catalogue search, filtering and sorting, a persistent slide-out cart, exact money arithmetic, and a simulated checkout that the server re-prices and re-validates.

> Checkout is **simulated**. No payment is taken, no card details are requested, and no order is stored.

---

## 1. Architecture

```
 Browser (React, :5173)                  ASP.NET Core API (:5080)                 FakeStoreAPI
┌─────────────────────────────┐        ┌───────────────────────────────┐        ┌──────────────┐
│ CatalogPage ── useProducts ─┼─GET───▶│ ProductsController            │        │              │
│ Navbar / CategoryNav        │        │   └ ProductCatalogService ────┼─GET───▶│ /products    │
│ CartContext (useReducer)    │        │       ├ CategoryNormalizer    │        │ /products/id │
│   └ localStorage (v1)       │        │       ├ ProductBadgeRules     │        └──────────────┘
│ CartDrawer / CheckoutDialog ┼─POST──▶│ CheckoutController            │            ▲
│ utils/pricing (int cents)   │        │   └ CheckoutService           │            │ typed HttpClient
│                             │◀───────┤       ├ CheckoutRequestValidator          │ (IHttpClientFactory,
│ StoreConfigContext ─────────┼─GET───▶│       ├ PricingCalculator (decimal)       │  timeout, in-memory
└─────────────────────────────┘        │       └ catalogue (authoritative prices)  │  catalogue cache)
                                       │ GlobalExceptionHandler → ProblemDetails │
                                       └───────────────────────────────┘
```

**Backend responsibilities.** Fetch and validate upstream data; normalise categories (`jewelery` → `jewelry`, men's and women's clothing grouped under `clothing`); compute badges; apply search, category, price range and sort; publish the pricing rules; and at checkout validate the request, look up authoritative prices, calculate totals with `decimal`, and return a confirmation or a structured error.

**Frontend responsibilities.** Render the catalogue and keep filters in the URL; manage the cart in one reducer; persist it to localStorage; show estimated totals using the same rules as the server; run the checkout flow and recover from every failure without losing the cart.

### Key decisions

| Decision | Choice | Why |
|---|---|---|
| Cart ownership | **Client-managed cart in localStorage; server validates at checkout** (option 1 in the brief) | No accounts or database are needed, refresh-survival is free, and the security boundary stays where it matters: the server never trusts client prices. `GET /api/cart` is therefore not implemented. |
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
│   └── Audits/                      # AUDIT.md (audit report), storefront-audit.patch, originals/ (pre-fix backups)
├── .gitignore
├── tools/
│   ├── fakestore_mock.py            # optional offline stand-in for FakeStoreAPI, with failure modes
│   └── smoke_test.py                # end-to-end API checks (endpoints, repricing, validation, CORS, Swagger, outages)
├── backend/
│   ├── Storefront.slnx
│   ├── Storefront.Api/
│   │   ├── Storefront.Api.csproj
│   │   ├── Storefront.Api.http      # example requests
│   │   ├── Program.cs               # DI, options validation, HttpClient, CORS, ProblemDetails, Swagger
│   │   ├── appsettings.json / appsettings.Development.json
│   │   ├── Properties/launchSettings.json
│   │   ├── Configuration/           # FakeStoreOptions, PricingOptions, CorsSettings
│   │   ├── Controllers/             # Products, Categories, Checkout, Store (thin)
│   │   ├── DTOs/                    # request/response contracts
│   │   ├── Exceptions/              # Upstream, validation, unavailable, price-changed
│   │   ├── Middleware/              # GlobalExceptionHandler → ProblemDetails
│   │   ├── Models/                  # upstream shape, normalised catalogue, price breakdown
│   │   └── Services/                # FakeStoreClient, catalogue, categories, badges, pricing,
│   │                                #   checkout validation, checkout, order references
│   └── Storefront.Api.Tests/        # xUnit
│       ├── Catalogue/  Checkout/  Configuration/  FakeStore/  Pricing/  TestSupport/
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

# Frontend
cd ../frontend
npm install
cp .env.example .env.local     # optional, the defaults already point at http://localhost:5080
```

**Packages.** Backend: `Swashbuckle.AspNetCore` 10.x (Swagger UI; v10 is the line that supports ASP.NET Core 10, v9 supports 8.0/9.0 only). Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`. Frontend runtime: `react`, `react-dom` only. Frontend dev: `vite`, `@vitejs/plugin-react`, `typescript`, `vitest`, `jsdom`, `@testing-library/*`, `@types/*`. React Router isn't used because the app is a single page and its view state (search, category, sort) lives in the URL query string.

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
dotnet test -c Release --no-build --logger "console;verbosity=normal"

# Frontend
cd storefront/frontend
npm ci
npm run typecheck      # strict tsc -b
npm test               # Vitest + Testing Library (jsdom)
npm run build          # type-check + production bundle
```

### End-to-end API smoke test

`tools/smoke_test.py` checks a running API: every endpoint, Swagger, 200 random checkouts repriced against the documented policy, validation errors, request-size limits, CORS (including on error responses), and, with `--mock`, upstream 500, timeout and malformed-data handling.

```bash
# Offline, with failure modes (three terminals):
python3 tools/fakestore_mock.py 9100
cd backend/Storefront.Api && FakeStore__BaseUrl=http://localhost:9100/ FakeStore__CatalogueCacheSeconds=0 dotnet run --launch-profile http
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

**CORS in other environments.** `Cors:AllowedOrigins` has no built-in default in code, so configured origins replace the development ones rather than adding to them. In production, set your real origin(s), for example `Cors__AllowedOrigins__0=https://shop.example.com`. Development (`appsettings.Development.json`) also allows `http://127.0.0.1:5173`. Swagger is not mapped outside Development.

**Frontend** (`frontend/.env.example`). Only `VITE_*` variables reach the browser, and everything in them is public, so never put secrets there.

```
VITE_API_BASE_URL=http://localhost:5080
VITE_DEFAULT_CURRENCY=USD
VITE_DEFAULT_LOCALE=en-US
```

No secrets are needed anywhere: FakeStoreAPI is public and there is no payment provider.

---

## 6. API reference with examples

All errors use `application/problem+json` with `status`, `title`, `code`, `traceId` and, where relevant, `detail`, `errors` or extra fields.

| Method and path | Purpose | Success | Errors |
|---|---|---|---|
| `GET /api/products` | List with `search`, `category` (slug, group or `all`), `sort` (`featured`, `price-asc`, `price-desc`, `rating`), `minPrice`, `maxPrice` | 200 | 400 `VALIDATION_FAILED`, 502 `UPSTREAM_UNAVAILABLE` / `UPSTREAM_MALFORMED`, 504 `UPSTREAM_TIMEOUT` |
| `GET /api/products/{id}` | One product | 200 | 400, 404 `NOT_FOUND`, 502, 504 |
| `GET /api/categories` | Normalised categories with counts and groups | 200 | 502, 504 |
| `GET /api/store/config` | Currency and pricing rules | 200 | none |
| `POST /api/checkout` | Simulated checkout. Body: `customer`, `items[]` (`productId`, `quantity`), `expectedTotal` (required) | 200 | 400 `VALIDATION_FAILED`, 409 `PRICE_CHANGED`, 413 `REQUEST_TOO_LARGE` (body over 64 KiB), 415, 422 `PRODUCTS_UNAVAILABLE`, 502, 504 |

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
  "notice": "This is a simulated checkout. No payment was taken and the order has not been stored."
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
| Checkout network error, 5xx or timeout | The cart is kept and a **Try again** button resubmits. |
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

**Port already in use.** Change `applicationUrl` in `Properties/launchSettings.json` (and `VITE_API_BASE_URL`), or `server.port` in `vite.config.ts` (and `Cors:AllowedOrigins`).

**`dotnet restore` fails or can't find a package version.** Run `dotnet --version` and confirm it's 10.x. If a pinned version isn't available on your feed, update it with, for example, `dotnet add package Swashbuckle.AspNetCore` in `Storefront.Api`.

**`npm install` fails.** Check `node -v` (22.12+ or 20.19+). Delete `node_modules` and retry with `npm ci`. Behind a proxy, set `npm config set proxy …`.

**Fonts look different.** The UI loads Bricolage Grotesque and Public Sans from Google Fonts and falls back to system fonts when they're blocked.

---

## 9. Verification status and acceptance review

Last audited 22 October 2026 with .NET SDK 10.0.401 (runtime 10.0.12), Node 22.22.0 and npm 10.9.4, on Linux. The audit environment could not reach nuget.org or fakestoreapi.com (the outbound proxy refused both), which blocks some checks. Those are marked **Blocked**, not passed.

| Check | Result |
|---|---|
| `dotnet restore` | **Blocked**: `NU1301 Unable to load the service index for source https://api.nuget.org/v3/index.json` (proxy returned 403). |
| `dotnet build`, `dotnet test` (real xUnit) | **Blocked** by the restore failure, so no xUnit test was discovered or executed. The production source and test source do compile on SDK 10.0.401 (with a stand-in for the three Swashbuckle calls and for the xUnit API), and the 106 test cases pass on that stand-in. That is not a substitute for `dotnet test`. |
| Package versions | Verified to exist as upstream release tags: Swashbuckle `v10.3.1`, xunit `v2-2.9.3`, xunit.runner.visualstudio `3.1.0`, vstest `v17.13.0`. Not verified on nuget.org. |
| Swagger UI and OpenAPI JSON | **Blocked** (needs the package). `tools/smoke_test.py` checks both once the API runs in Development. |
| Live FakeStoreAPI | **Not tested** (host unreachable). All integration checks used `tools/fakestore_mock.py`. |
| API against the mock | **Passed**: `tools/smoke_test.py --mock`: 45 passed, 0 failed, 1 skipped (Swagger, for the reason above). |
| Client and server pricing agree | **Passed**: the frontend's `utils/pricing.ts` and the API agreed to the cent on 500 random carts, crossing the free-shipping threshold. |
| Frontend `npm ci`, `tsc -b`, `npm run build`, `npm test` | **Passed**: 55 Vitest tests, run from a clean extract of the zip. |
| Browser (headless Chromium; dev server under StrictMode, and the production build) | **Passed**: refresh persistence, corrupt and repaired saved carts, 409 re-pricing and confirm, outage during checkout (cart kept, retry succeeds), badge counts units, empty search state, Tab trap, Escape and focus return. |

To finish the blocked checks on a machine with network access:

```bash
cd backend && dotnet restore && dotnet build -c Release && dotnet test -c Release --no-build
cd Storefront.Api && dotnet run --launch-profile http        # then open http://localhost:5080/swagger
python3 ../../tools/smoke_test.py --api http://localhost:5080  # live FakeStoreAPI; expect 0 failed, Swagger checked
```

| Acceptance criterion | Status |
|---|---|
| Catalogue loads from FakeStoreAPI through the backend | Passed against the mock; live API not tested |
| Search, category filters and sorting work together | Passed (smoke test, Vitest; xUnit cases written but blocked) |
| Cart drawer opens and closes correctly | Passed (Vitest, browser) |
| Quantities and badge counts stay consistent | Passed (Vitest, browser) |
| Removing or updating items immediately updates totals | Passed (Vitest) |
| Tax and shipping are correct at and around the threshold | Passed (Vitest, smoke test, 500-cart cross-check) |
| Cart survives a refresh | Passed (Vitest, browser) |
| Invalid stored state and upstream errors are handled | Passed (Vitest, browser, smoke test) |
| Checkout verifies prices and quantities on the backend | Passed against the mock (smoke test) |
| Success confirms the order and clears the cart | Passed (Vitest, browser) |
| Failure preserves the cart and allows recovery | Passed (Vitest, browser) |
| Responsive and keyboard-accessible | Passed for keyboard (browser); layout checked by screenshot at 390 and 1366 px only |
| Core calculation and cart behaviour have automated tests | Frontend passed; backend tests written but **blocked** from running |
| Starts by following these instructions | Frontend passed from a clean extract; backend **blocked** at `dotnet restore` |

### Known limitations and risks

- **Duplicate orders on retry.** There's no idempotency key. If the response is lost after the server accepted an order (for example, a client timeout), **Try again** places a second order. That's harmless while checkout is simulated, but an `Idempotency-Key` is needed before real orders.
- **Pricing rules fallback.** If `/api/store/config` fails at page load, the cart uses built-in defaults. If the server is configured differently, every checkout returns 409 until the page is reloaded.
- **Catalogue cache.** Prices are authoritative as of the last fetch (5 minutes by default). When the cache expires during an outage, the store fails closed (502) rather than serving stale prices. Concurrent cold-cache requests each call FakeStoreAPI (no single-flight).
- No retry or circuit breaker on upstream calls (`Microsoft.Extensions.Http.Resilience` would be the next step), and no rate limiting, HTTPS redirection or HSTS. All are needed before production.
- Orders aren't persisted, and references (`DEMO-yyyyMMdd-XXXXXX`) aren't guaranteed unique.
- Only two-decimal currencies are supported. Catalogue search and filters are server-side, so they need the API to be running.
