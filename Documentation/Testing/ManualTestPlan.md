# Manual test plan: Greenhaus storefront

A checklist a tester can follow without reading the code. It covers what the automated suites can't judge well:
- how the store feels on a real phone
- what a screen reader announces
- zoom and high-contrast modes
- slow networks
- the live FakeStoreAPI

> **Status:** not yet carried out by a person. The "Actual result" and "Pass/Fail" columns are blank on purpose; fill them in during a session. Don't mark anything as passed without doing the steps.

---

## 1. Before you start

| Item | Detail |
|---|---|
| Build under test | Git commit: `__________`  Date: `__________`  Tester: `__________` |
| Devices | A desktop or laptop browser (Chrome or Edge, plus Firefox if available). One real phone (Android Chrome or iPhone Safari). |
| Assistive tech | NVDA (Windows, free) or Narrator; VoiceOver on iPhone or Mac if available |
| Start the API | In `backend/Storefront.Api`: `dotnet run --launch-profile http` (uses the live FakeStoreAPI) |
| Start the store | In `frontend`: `npm run dev`, then open http://localhost:5173 |
| Phone access | The store only runs on the computer by default. For the phone sessions, see §5. |
| Fresh state | DevTools → Application → Storage → **Clear site data** before each section, unless the step says otherwise |
| Test data | Use made-up details only: name `Test Shopper`, email `tester+<date>@example.test`, password `Str0ng!Passw0rd`. Never use a real email address or password. No payment is ever taken. |
| Reset the database | Stop the API and delete `backend/Storefront.Api/App_Data/storefront.db`. It is recreated on the next start. |

**How to record a result.** In "Actual result", write what you saw if it differs from the expected result; otherwise write "as expected". Set Pass/Fail to **P** or **F**. For every **F**, add a defect in §4 and put its ID in the Notes column.

---

## 2. Shopper journeys

| ID | Steps | Expected result | Actual result | Pass/Fail | Notes |
|---|---|---|---|---|---|
| J1 | Open the store. Wait for products. | 20 products with photos, prices and ratings. No error message. | | | |
| J2 | Select **Jewelry**, then **Clothing**, then type `jacket` in Search. | The list narrows each time and the heading changes. The address bar shows `?category=…&q=jacket`. | | | |
| J3 | Click a product title. Copy the address. Open it in a new tab. | The product details open in both tabs, with the description and price. | | | |
| J4 | Close the details (✕ or Escape). | Back on the same filtered list, scroll position kept. | | | |
| J5 | Add 2 different products, then open **Cart**. Change quantities with − and +, then remove one item. | Subtotal, tax, shipping and total update immediately. "−" at 1 explains how to remove instead. | | | |
| J6 | Refresh the page. | The cart is still there. | | | |
| J7 | Select **Sign in** → **Create an account**. Type a weak password (`short`). | The checklist shows which rules are missing, and the account is not created. | | | |
| J8 | Create the account with the test email and password. | "Your account" with "No orders yet". The header shows **Account**. | | | |
| J9 | Check out. | The email is pre-filled, and the form says the order will be saved to your account. | | | |
| J10 | Enter a name and place the order. | Confirmation with a `DEMO-…` reference, totals, and "saved to your account". The cart is empty. | | | |
| J11 | Open **Account**, then select the order. | The order is listed with the same reference; its items and totals match the confirmation. | | | |
| J12 | Refresh the page. | You are still signed in. | | | |
| J13 | Close the tab, open a new one at the store. | You are signed out (the session belongs to the tab). The cart is still there. | | | |
| J14 | Sign in again, then **Sign out**. | "You have signed out." The header shows **Sign in**. | | | |
| J15 | As a guest: add an item and check out with a different name and email. | The confirmation says it was a guest order. It does not appear in anyone's account. | | | |
| J16 | Try to sign in with a wrong password 5 times. | Each time: "don't match an account". After the 5th: "locked for a few minutes". | | | |

## 3. Failure handling

| ID | Steps | Expected result | Actual result | Pass/Fail | Notes |
|---|---|---|---|---|---|
| F1 | Stop the API. Refresh the store. | "We couldn't load the catalogue" with **Try again**. Start the API, then select **Try again**: the products load. | | | |
| F2 | Add an item, open checkout, fill in the form, stop the API, then place the order. | "We couldn't confirm whether your order went through." The cart and form are kept. Start the API and select **Try again**: one confirmation. Your account shows **one** order. | | | |
| F3 | DevTools → Network → **Slow 3G**. Place an order and click **Place demo order** several times quickly. | The button is disabled after the first click, with a "Placing order…" spinner. Only one order is created. | | | |
| F4 | DevTools → Application → Local storage: change a cart item's `unitPriceMinor` to `1`, refresh, then check out. | "Some prices changed…" with the new total. The button reads **Confirm new total and place order**. | | | |
| F5 | Sign in. DevTools → Application → Session storage: change `refreshToken` in `greenhaus.session` to `x`, then refresh. | "Your session has expired" notice; signed out. No sign-in window pops up and the page doesn't reload repeatedly. | | | |
| F6 | Turn off Wi-Fi with the store open, then search. | The previous results stay, with a message and **Try again**. | | | |

## 4. Accessibility (needs a person)

The automated suite runs an axe scan and keyboard checks (`npm run test:e2e`). These need human judgement:

| ID | Steps | Expected result | Actual result | Pass/Fail | Notes |
|---|---|---|---|---|---|
| A1 | Keyboard only (no mouse): find a product, add it to the cart, check out, sign in and see the order. | Everything is reachable with Tab, Shift+Tab, Enter, Space and Escape. The focus ring is always visible. Focus never gets lost behind an open dialog. | | | |
| A2 | Screen reader (NVDA or VoiceOver): browse, add to cart, open the cart. | Products are announced with name, price and rating. "Added … to your cart" is announced. The cart count is in the cart button's name. | | | |
| A3 | Screen reader: submit checkout with empty fields. | Each error is read with its field ("Full name, invalid entry, Enter your name"). | | | |
| A4 | Screen reader: trigger a price change (F4). | The new total message is announced without having to search for it. | | | |
| A5 | Browser zoom 200%, then 400% (Ctrl/Cmd + +). | No sideways scrolling at 400% (it reflows like a phone layout). Nothing is cut off or overlapping. | | | |
| A6 | Windows Settings → Accessibility → **Contrast themes** on. | Buttons, focus rings, inputs and the selected category are still visible. | | | |
| A7 | OS setting "Reduce motion" on. | Drawers and dialogs appear without sliding. Loading placeholders don't shimmer. | | | |
| A8 | Phone: VoiceOver or TalkBack, add an item and open checkout. | The checkout opens as a sheet; swiping moves through its fields in order, and doesn't reach the page behind it. | | | |

## 5. Real devices and screen sizes

The browser suite checks 320, 375, 768, 1024 and 1440 px in Chromium. On real hardware:

| ID | Steps | Expected result | Actual result | Pass/Fail | Notes |
|---|---|---|---|---|---|
| D1 | Phone, portrait: run J1–J10. | All buttons are easy to tap; nothing sits under the on-screen keyboard when typing in checkout; the address bar showing or hiding doesn't hide buttons. | | | |
| D2 | Phone, landscape: open checkout. | The form scrolls inside the sheet; **Place demo order** can be reached. | | | |
| D3 | Tablet or a 768 px-wide browser window: run J5 and J9. | Cart drawer and checkout use the space sensibly; no tiny text. | | | |
| D4 | Safari (iPhone or Mac), if available: run J1, J5 and J10. | Same behaviour as Chrome. | | | |
| D5 | Firefox: run J1, J8 and J10. | Same behaviour as Chrome. | | | |

**Reaching the store from a phone.** By default the API answers only `localhost`, and Vite serves only this computer.
1. Run `npm run dev -- --host` and note the Network address it prints (for example `http://192.168.1.20:5173`).
2. Before starting the API, add that origin to `Cors__AllowedOrigins__0`, and the IP to `AllowedHosts`.
3. Point the frontend at the API by its IP (`VITE_API_BASE_URL`).

Only do this on a trusted network, and undo it afterwards.

---

## 6. Defects found

| ID | Test ID | Summary | Steps to reproduce | Expected | Actual | Severity (High/Med/Low) | Device / browser | Screenshot or recording |
|---|---|---|---|---|---|---|---|---|
| | | | | | | | | |

**Severity guide.**
- **High:** an order is lost, duplicated or wrongly priced; someone else's data is shown; the journey can't be finished.
- **Medium:** a journey works but is confusing or inaccessible to some users.
- **Low:** cosmetic.

## 7. Sign-off

| Section | Run by | Date | Passed | Failed | Not run | Comments |
|---|---|---|---|---|---|---|
| 2. Journeys | | | | | | |
| 3. Failures | | | | | | |
| 4. Accessibility | | | | | | |
| 5. Devices | | | | | | |
