# Frontend design notes

How the React frontend talks to the API, handles sign-in and checkout outcomes, and adapts to screen sizes. Each section ends with the reason for the choice, so it can be explained in a code review.

The stack is React 19 with TypeScript, Vite and plain CSS (design tokens in `styles/tokens.css`). There's no router, state library or UI kit: the app has one page, and overlays (cart, checkout, account, product details) cover everything else.

---

## 1. API layer

All requests go through `services/apiClient.ts` (`apiRequest`). Components never call `fetch` directly.

| Concern | How it's handled |
|---|---|
| Base URL | `VITE_API_BASE_URL`. If unset, `npm run dev` uses `http://localhost:5080`, and a production build uses the site's own origin (`/api/...` behind a reverse proxy). A production bundle therefore never points at a developer's localhost. `vite build` refuses an empty, relative or non-local `http://` value, and on Render a missing one (`src/config/apiBaseUrl.ts`). Hosting on Render: `Deployment.md`, section 10. |
| Timeouts and cancellation | 15 s timeout. A caller's `AbortSignal` is forwarded, so superseded catalogue searches and closed dialogs cancel their requests. |
| Errors | Every failure becomes an `ApiError` with `status`, `code` (from the API's ProblemDetails), the `problem` body and `retryAfterSeconds`. Components decide what to do by `code`, not by parsing messages. |
| Messages | API problem titles are written for shoppers and used as they are. 429 says how long to wait (from `Retry-After`), 413 says the request was too large, and a 5xx without a body gets a generic sentence. HTML error pages and stack traces are never shown. |
| Response validation | Each service passes a parser (`services/responseParsers.ts`) that checks the shape. A 200 with the wrong shape becomes `INVALID_RESPONSE` rather than broken UI. |
| Headers | `onResponse` lets a service read a response header; checkout uses it for `Idempotent-Replayed`. |

Services: `productService` (catalogue, categories, one product, store config), `checkoutService`, and `accountService` (register, login, refresh, account info, orders).

**Why:** one place for timeouts, error mapping and validation means every screen fails the same way. The ProblemDetails `code` is the contract between backend and frontend.

## 2. Contracts verified against the backend

These were checked against the running API, not just the source:

| Endpoint | Shape used by the frontend |
|---|---|
| `POST /api/account/register` | 200 with an empty body. 400 `VALIDATION_FAILED` with Identity error keys such as `DuplicateEmail`, `InvalidEmail`, `PasswordTooShort` and `PasswordRequiresUpper`, which are mapped to the email or password field |
| `POST /api/account/login` | `{ tokenType, accessToken, expiresIn, refreshToken }`. 401 with `detail` `"Failed"`; 429 with `Retry-After` after repeated failures (shown as "Wait 15 minutes…") |
| `POST /api/account/refresh` | Same token shape, with a **new** refresh token: each one works once, so the app always keeps the latest. 401 when it's rejected |
| `POST /api/account/logout` | `{ refreshToken }` → 204. Sent on sign-out so the session ends on the server |
| `GET /api/account/manage/info` | `{ email, isEmailConfirmed }` |
| `GET /api/orders`, `GET /api/orders/{reference}` | `OrderSummaryDto[]` and `OrderDto`. 401 without a token; 404 for someone else's order |
| `POST /api/checkout` | Bearer token optional. Sends `Idempotency-Key`; reads `Idempotent-Replayed`. CORS allows `Authorization` and `Idempotency-Key` and exposes `Idempotent-Replayed` and `Retry-After` |
| `GET /api/products/{id}` | `ProductDto`; 404 `NOT_FOUND` |

One backend gap turned up and was fixed: checkout with an **invalid or expired** token used to succeed as a guest order. It now returns 401 before saving (see `Documentation/Backend.md`).

## 3. Sign-in session (`context/SessionContext.tsx`)

| Piece | Where it lives | Why |
|---|---|---|
| Access token (1 hour) | Memory only | It's the token that authorizes requests; keeping it out of storage limits what a stolen storage snapshot gives away. |
| Refresh token and email | This tab's `sessionStorage` (`greenhaus.session`) | The session survives a page refresh but ends when the tab closes, and isn't shared with other tabs. |
| Cart | `localStorage` (unchanged) | The cart belongs to the browser for guests and signed-in shoppers alike. It is never sent to the account, so there's no second source of truth to reconcile. |

How requests are authorized: `withAccessToken(call)` runs `call` with a valid token.
- If the token expires within 60 s, it's refreshed first.
- If the API still answers 401, the token is refreshed **once** and the request is repeated **once**.
- If the refresh is rejected, the shopper is signed out locally, a notice says the session expired, and the call fails with `SESSION_EXPIRED`.
- Nothing redirects or opens a sign-in screen automatically, so an expired session can't cause a loop. Tests check the request counts.

Other details:
- Concurrent refreshes share one request.
- A refresh that finishes after a sign-out is ignored (a generation counter).
- If the API is unreachable while a session is being restored, the shopper stays signed in, and the next request retries the refresh.
- A Strict Mode double effect sends one refresh, not two.
- Sign-out clears the browser's tokens and calls `/api/account/logout`, which ends the session on the server: a copied access or refresh token stops working too. The tab signs out at once even if that call fails.
- Because each refresh token works once, a duplicated browser tab (which copies `sessionStorage`) shares the session with the original. When one of them refreshes, the other's token is used up, and that session ends for both, with the usual "session expired" notice.

**Trade-off.** `sessionStorage` is readable by any script on the page, so an XSS bug could take the refresh token. The app renders no untrusted HTML and React escapes text by default. The alternative is Identity's cookie mode (httpOnly cookies the page can't read). That would need anti-forgery tokens on every POST and a same-site deployment, a bigger change than this task called for.

## 4. Checkout outcomes (`components/CheckoutDialog.tsx`)

The backend is authoritative for prices and totals. The client sends product IDs, quantities and the total the shopper saw (`expectedTotal`), never unit prices.

| Outcome | Classified as | What happens |
|---|---|---|
| 200 | confirmed | Cart cleared. The confirmation uses only the server's figures and says whether the order is in the account or was a guest order. |
| 200 with `Idempotent-Replayed: true` | confirmed (replay) | As above, plus "Your earlier attempt had already placed this order, so it was not placed a second time." |
| Network error, timeout, 500, unreadable 200 | **uncertain** | "We couldn't confirm whether your order went through." The cart and details are kept. **Try again** resends with the **same** idempotency key. Nothing is resent automatically. |
| 409 `PRICE_CHANGED` | review | The cart is re-priced from the server's quote and the new total shown; the button becomes **Confirm new total and place order**. |
| 422 `PRODUCTS_UNAVAILABLE` | review | The items are named, with **Remove unavailable items**. |
| 422 `IDEMPOTENCY_KEY_REUSED` | possible earlier order | The shopper is warned to check before ordering again; the next attempt uses a new key. |
| 401 (token) | renew | The token is refreshed and the same request (same key) sent once more. The API rejects a stale token before saving, so this can't duplicate an order. |
| `SESSION_EXPIRED` | stop | "Your session expired, so this order hasn't been placed", with **Sign in again** or **Place order as a guest**. It never silently becomes a guest order. |
| 429, 503 `DATABASE_BUSY`, 502/504 upstream | retryable | The message (with the wait time for 429); the cart is kept. |
| 400, 413 | fix | Field errors under the inputs, or a plain message. |

Idempotency keys: one key per distinct order body. A retry of the same body reuses it, and a change to the cart or details creates a new one, **except after an uncertain outcome**. Then the key is pinned (in this tab's sessionStorage, `greenhaus.pendingCheckout`, so it survives a reload) and reused for every attempt until the server confirms an order or answers 422 `IDEMPOTENCY_KEY_REUSED`. With a new key, a changed order after a lost response could have placed a second one; with the pinned key, the server returns the saved order or refuses with 422. The key isn't tied to the sign-in state on purpose. If a guest attempt might have succeeded and the shopper then signs in, reusing the key makes the API answer 422 instead of quietly creating a second order.

Double submits: the submit button and fieldset are disabled while a request is in flight, and an in-flight ref guards the gap before React re-renders. (The ref is a backstop: tests can't trigger it on its own because the disabled button already stops the second submit.)

## 5. Product details

Product titles are real links (`?product=5`, keeping any search and filter parameters), so they can be opened in a new tab or shared. A plain click opens the details dialog in place, using the data already loaded, with no extra request. A shared link fetches `GET /api/products/{id}`. A missing product shows "This product is no longer in the catalogue", and other failures offer **Try again**. Closing removes the parameter. The image link duplicates the title link for mouse and touch users, and is hidden from keyboard and screen readers so they don't meet the same link twice.

## 6. Responsive layout and accessibility

Checked with a headless browser at 320, 375, 768, 1024 and 1440 px (screenshots plus automatic overflow checks):

- **Fixed issues:**
  - The checkout summary's line prices were pushed out of view by long titles at every width. Each line is now a grid: the title truncates, quantity and price always show.
  - "Added to cart" toasts covered the cart's and checkout's buttons on phones. They're now cleared when those open, and toasts let taps pass through to what's underneath (only their close button is interactive). The sign-in toast was removed because the dialog already shows the signed-in state.
  - On phones, a checkout error appeared below the fold. The message and its buttons now scroll into view.
  - The category row called `scrollIntoView` on load, which in Chrome moved the starting point for Tab: a keyboard user's first Tab skipped the skip link, search, account and cart. It now scrolls the chip row directly.
  - Product titles were `h3` directly under the page's `h1`; they're now `h2`.
- **Mobile behaviour:** dialogs become bottom sheets sized with `dvh` (not `vh`, which includes the browser toolbar) and pad for the safe area. The header collapses to icon buttons with accessible names. Buttons and steppers are at least 40 px. Category chips are 32–36 px and inline text links are smaller, all above the 24 px minimum in WCAG 2.2.
- **Kept from before:** focus moves into each dialog and back to its trigger, Tab is trapped, Escape closes, the page behind is `inert`, and `prefers-reduced-motion` turns transitions off.
- **Forms:** fields have labels, `autocomplete` (`name`, `email`, `username`, `current-password`, `new-password`), `inputMode` and `aria-describedby` errors. There's a show/hide password toggle (`aria-pressed`) and a live password checklist marked with symbols as well as colour.

## 7. Tests

**Current test strategy, commands, counts and coverage: see [`Testing.md`](Testing.md).** What follows records the state when this frontend round was finished.

`npm test` ran 98 Vitest + Testing Library tests (230 after the testing round) against a fake `fetch` (`src/test/fixtures.ts`). The fake returns the real API's response shapes, recorded from the running backend. These mocks exist only in tests; the app has no mock mode.

Mutation check: 13 safeguards were broken one at a time.
- **Caught (11):** renew-and-retry on 401, ending a rejected session, no guest fallback, the bearer header, the uncertain-outcome message, replay detection, key rotation, `Retry-After`, keeping the access token out of storage, URL cleanup, duplicate-email mapping.
- **Redundant (2):** the Strict Mode restore guard (also covered by the shared refresh; breaking both fails a test) and the double-submit ref (covered by the disabled button).

Browser checks against the real API (scripts outside the repo, run with headless Chromium) covered:
- product links and reloads
- registering and signing in
- signed-in checkout
- order history
- restoring the session after a reload
- an interrupted checkout recovered without a duplicate
- sign-out
- error messages
- keyboard paths

## 8. Not done

- No password reset, email confirmation or account deletion in the UI (the backend has no email sender).
- The cart isn't saved to the account, so it doesn't follow a shopper across devices.
- The browser's Back button doesn't close an open dialog. The product URL is updated with `replaceState`, so it can be shared, but it doesn't add a history entry.
- No ESLint or Prettier is configured in this repository; `tsc` in strict mode is the static check.
