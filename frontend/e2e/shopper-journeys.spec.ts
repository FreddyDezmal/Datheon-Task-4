import { expect, test } from '@playwright/test';
import {
  PRODUCTS,
  accountButton,
  addToCart,
  cartButton,
  openCheckout,
  openFreshStore,
  ordersFromApi,
  register,
  seedCart,
  setUpstream,
  uniqueEmail,
  watchForErrors,
} from './support/shop';

/*
 * Shopper journeys against the real API and database (see playwright.config.ts).
 * Nothing here is mocked in the browser except where a test says so, e.g. dropping a response.
 */

let unexpectedErrors: string[] = [];

test.beforeEach(async ({ page, request }) => {
  await setUpstream(request, 'ok');
  unexpectedErrors = watchForErrors(page);
  await openFreshStore(page);
});

test.afterEach(() => {
  expect(unexpectedErrors, 'unexpected console errors').toEqual([]);
});

test('browse, filter, search and open product details, then return to the catalogue', async ({ page }) => {
  await expect(page.getByRole('heading', { level: 1, name: 'All products' })).toBeVisible();
  await expect(page.getByText('20 products')).toBeVisible();

  await page.getByRole('button', { name: 'Jewelry', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Jewelry' })).toBeVisible();
  await expect(page.getByText('4 products')).toBeVisible();
  await expect(page).toHaveURL(/category=jewelry/);

  await page.getByRole('button', { name: 'All products' }).click();
  await page.getByRole('searchbox', { name: 'Search products' }).fill('jacket');
  await expect(page.getByText(/products? for “jacket”/)).toBeVisible();
  const titles = await page.getByRole('article').getByRole('heading').allTextContents();
  expect(titles.length).toBeGreaterThan(0);
  for (const title of titles) expect(title.toLowerCase()).toContain('jacket');

  await page.getByRole('link', { name: PRODUCTS.jacket.title }).click();
  const details = page.getByRole('dialog', { name: PRODUCTS.jacket.title });
  await expect(details).toBeVisible();
  await expect(details.getByText('$55.99')).toBeVisible();
  await expect(page).toHaveURL(/product=3/);

  // A shared link reopens the same product after a reload.
  await page.reload();
  await expect(page.getByRole('dialog', { name: PRODUCTS.jacket.title })).toBeVisible();

  await page.getByRole('button', { name: 'Close product details' }).click();
  await expect(page.getByRole('dialog', { name: PRODUCTS.jacket.title })).toBeHidden();
  await expect(page).not.toHaveURL(/product=/);
  await expect(page).toHaveURL(/q=jacket/);
});

test('add items, change quantities and see totals update; the cart survives a reload', async ({ page }) => {
  await addToCart(page, PRODUCTS.shirt.title);
  await addToCart(page, PRODUCTS.shirt.title);
  await addToCart(page, PRODUCTS.jacket.title);
  await expect(page.getByTestId('cart-badge')).toHaveText('3');

  await cartButton(page).click();
  const cart = page.getByRole('dialog', { name: /Your cart/ });
  await expect(cart.getByTestId('summary-subtotal')).toHaveText('$100.59'); // 2 × 22.30 + 55.99
  await expect(cart.getByTestId('summary-shipping')).toHaveText('Free');

  await cart.getByRole('button', { name: `Decrease quantity of ${PRODUCTS.shirt.title}` }).click();
  await expect(cart.getByTestId('summary-subtotal')).toHaveText('$78.29');
  await cart.getByRole('button', { name: `Remove ${PRODUCTS.jacket.title} from cart` }).click();
  await expect(cart.getByTestId('summary-subtotal')).toHaveText('$22.30');
  await expect(cart.getByTestId('summary-shipping')).toHaveText('$5.99');
  await expect(cart.getByTestId('summary-total')).toHaveText('$30.07');

  await page.reload();
  await expect(page.getByTestId('cart-badge')).toHaveText('1');
});

test('register, check out signed in, find the order in the account, then sign out', async ({ page, request }) => {
  const email = uniqueEmail('journey');
  await register(page, email);
  await expect(page.getByText('No orders yet')).toBeVisible();
  await page.keyboard.press('Escape');

  await addToCart(page, PRODUCTS.shirt.title);
  const checkout = await openCheckout(page);
  await expect(checkout.getByTestId('checkout-account')).toContainText(`Signed in as ${email}`);
  await expect(checkout.getByLabel('Email')).toHaveValue(email);
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();

  const confirmation = page.getByRole('dialog', { name: 'Order placed' });
  await expect(confirmation.getByTestId('confirmation-total')).toHaveText('$30.07');
  await expect(confirmation.getByTestId('confirmation-where')).toContainText('saved to your account');
  const reference = (await confirmation.getByTestId('order-reference').textContent())?.trim() ?? '';
  expect(reference).toMatch(/^DEMO-\d{8}-[A-Z0-9]{6}$/);
  await expect(page.getByTestId('cart-badge')).toHaveCount(0);
  await confirmation.getByRole('button', { name: 'Continue shopping' }).click();

  await accountButton(page).click();
  const account = page.getByRole('dialog', { name: 'Your account' });
  const row = account.getByRole('button', { name: new RegExp(reference) });
  await row.click();
  await expect(row).toHaveAttribute('aria-expanded', 'true');
  await expect(account.getByRole('table', { name: `Items in order ${reference}` })).toContainText(PRODUCTS.shirt.title);

  // The same order, read back through the API with a fresh sign-in.
  expect((await ordersFromApi(request, email)).map((o) => o.orderReference)).toEqual([reference]);

  await account.getByRole('button', { name: 'Sign out' }).click();
  await expect(accountButton(page)).toHaveAccessibleName('Sign in');
  expect(await page.evaluate(() => sessionStorage.getItem('greenhaus.session'))).toBeNull();
  await page.reload();
  await expect(accountButton(page)).toHaveAccessibleName('Sign in');
});

test('a price change is shown and must be confirmed before the order is placed', async ({ page }) => {
  // A cart saved when the shirt was cheaper.
  await seedCart(page, [{ productId: PRODUCTS.shirt.id, title: PRODUCTS.shirt.title, unitPriceMinor: 1999, quantity: 1 }]);
  const checkout = await openCheckout(page);
  await expect(checkout.getByTestId('summary-total')).toHaveText('$27.58');
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByLabel('Email').fill('ada@example.test');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();

  await expect(checkout.getByRole('alert')).toContainText('Your new total is $30.07');
  await expect(checkout.getByTestId('summary-total')).toHaveText('$30.07');
  await expect(page.getByRole('dialog', { name: 'Order placed' })).toHaveCount(0);

  await checkout.getByRole('button', { name: 'Confirm new total and place order' }).click();
  await expect(page.getByRole('dialog', { name: 'Order placed' }).getByTestId('confirmation-total')).toHaveText('$30.07');
});

test('unavailable products are named and can be removed before checking out', async ({ page }) => {
  await seedCart(page, [
    { productId: PRODUCTS.shirt.id, title: PRODUCTS.shirt.title, unitPriceMinor: PRODUCTS.shirt.priceMinor, quantity: 1 },
    { productId: 999, title: 'Discontinued Lamp', unitPriceMinor: 1500, quantity: 1 },
  ]);
  const checkout = await openCheckout(page);
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByLabel('Email').fill('ada@example.test');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();

  await expect(checkout.getByRole('alert')).toContainText('These items are no longer available: Discontinued Lamp.');
  await checkout.getByRole('button', { name: 'Remove unavailable items' }).click();
  await expect(page.getByTestId('cart-badge')).toHaveText('1');

  await checkout.getByRole('button', { name: 'Place demo order' }).click();
  await expect(page.getByRole('dialog', { name: 'Order placed' })).toBeVisible();
});

test('an interrupted checkout is recovered with Try again, without a duplicate order', async ({ page, request }) => {
  const email = uniqueEmail('interrupted');
  await register(page, email);
  await page.keyboard.press('Escape');
  await addToCart(page, PRODUCTS.jacket.title);

  // The first attempt reaches the API and is saved, but the connection drops before the browser
  // gets the response: exactly the case where a naive retry would place a second order.
  let dropped = 0;
  await page.route('**/api/checkout', async (route) => {
    if (route.request().method() === 'POST' && dropped === 0) {
      dropped += 1;
      await route.fetch(); // delivered to the server
      await route.abort('connectionreset'); // response lost
      return;
    }
    await route.continue();
  });

  const checkout = await openCheckout(page);
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();

  await expect(checkout.getByRole('alert')).toContainText("couldn't confirm whether your order went through");
  await expect(page.getByTestId('cart-badge')).toHaveText('1');
  expect(await ordersFromApi(request, email)).toHaveLength(1); // it really was saved

  await checkout.getByRole('button', { name: 'Try again' }).click();
  const confirmation = page.getByRole('dialog', { name: 'Order placed' });
  await expect(confirmation.getByTestId('confirmation-replayed')).toContainText('not placed a second time');

  const orders = await ordersFromApi(request, email);
  expect(orders).toHaveLength(1);
  await expect(confirmation.getByTestId('order-reference')).toHaveText(orders[0]!.orderReference);
});

test('a signed-in session survives a reload; a session the server rejects ends quietly', async ({ page }) => {
  const email = uniqueEmail('restore');
  await register(page, email);
  await page.keyboard.press('Escape');

  await page.reload();
  await expect(accountButton(page)).toHaveAccessibleName(`Your account, signed in as ${email}`);

  await page.evaluate(() => sessionStorage.setItem('greenhaus.session', JSON.stringify({ refreshToken: 'forged', email: 'x@example.test' })));
  const refreshes: number[] = [];
  page.on('response', (r) => {
    if (r.url().endsWith('/api/account/refresh')) refreshes.push(r.status());
  });
  await page.reload();

  await expect(page.getByText('Your session has expired. Sign in again to see your orders.')).toBeVisible();
  await expect(accountButton(page)).toHaveAccessibleName('Sign in');
  expect(refreshes).toEqual([401]); // one attempt, no retry loop
  await expect(page.getByRole('dialog')).toHaveCount(0); // no automatic redirect to a sign-in screen
});

test('during a catalogue outage browsing shows the saved copy, checkout fails safely and keeps the cart', async ({ page, request }) => {
  await addToCart(page, PRODUCTS.shirt.title);
  await setUpstream(request, '500');

  const checkout = await openCheckout(page);
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByLabel('Email').fill('ada@example.test');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();
  const alert = checkout.getByRole('alert');
  await expect(alert).toContainText('Your cart is saved');
  await expect(alert).toContainText(/Reference: [0-9a-f]{32}/);
  await expect(alert).not.toContainText("couldn't confirm whether your order went through"); // nothing was saved
  await expect(page.getByTestId('cart-badge')).toHaveText('1');
  await checkout.getByRole('button', { name: 'Back to cart' }).click();
  await page.keyboard.press('Escape');

  // The API serves its last good copy while FakeStoreAPI is down, and the page says so.
  await page.reload();
  await expect(page.getByText('20 products')).toBeVisible();
  const notice = page.getByRole('status').filter({ hasText: 'recently saved copy' });
  await expect(notice).toBeVisible();

  await setUpstream(request, 'ok');
  await notice.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByText('recently saved copy')).toHaveCount(0);
  await expect(page.getByText('20 products')).toBeVisible();
  await expect(page.getByTestId('cart-badge')).toHaveText('1');
});

test('offline: the cart is kept, checkout is held without sending anything, and works after reconnecting', async ({
  page,
  context,
}) => {
  await addToCart(page, PRODUCTS.shirt.title);
  const checkoutRequests: string[] = [];
  page.on('request', (req) => {
    if (new URL(req.url()).pathname === '/api/checkout' && req.method() === 'POST') checkoutRequests.push(req.method());
  });

  const checkout = await openCheckout(page);
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByLabel('Email').fill('ada@example.test');

  await context.setOffline(true);
  await expect(page.getByText("You're offline.")).toBeVisible();
  await checkout.getByRole('button', { name: 'Place demo order' }).click();
  await expect(checkout.getByRole('alert')).toContainText("You're offline, so your order hasn't been sent.");
  expect(checkoutRequests).toEqual([]);
  await expect(page.getByTestId('cart-badge')).toHaveText('1');

  await context.setOffline(false);
  await expect(page.getByText("You're offline.")).toHaveCount(0);
  await checkout.getByRole('button', { name: 'Try again' }).click();
  await expect(page.getByRole('heading', { name: 'Order placed' })).toBeVisible();
  expect(checkoutRequests).toEqual(['POST']);
});
