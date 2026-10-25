import { expect, test } from '@playwright/test';
import { PRODUCTS, accountButton, addToCart, openCheckout, openFreshStore, register, setUpstream, uniqueEmail, watchForErrors } from './support/shop';

/*
 * The production bundle under the static site's security headers (Security-Audit.md, SA-12).
 * Only meaningful against `vite preview` (E2E_PRODUCTION_BUILD=1), which sends the same headers as
 * the Render Static Site; the dev server sends none, so these tests are skipped there.
 */

test.skip(process.env.E2E_PRODUCTION_BUILD !== '1', 'needs the production build: set E2E_PRODUCTION_BUILD=1');

test('the site is served with its security headers', async ({ page }) => {
  const response = await page.goto('/');
  const headers = response!.headers();

  expect(headers['content-security-policy']).toContain("script-src 'self'");
  expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");
  expect(headers['content-security-policy']).toContain('connect-src http://localhost:5081');
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['referrer-policy']).toBe('strict-origin-when-cross-origin');
});

test('a whole shopper journey triggers no Content-Security-Policy violation', async ({ page, request }) => {
  await setUpstream(request, 'ok');
  const errors = watchForErrors(page);
  await page.addInitScript(() => {
    const seen: string[] = [];
    (window as unknown as { __csp: string[] }).__csp = seen;
    document.addEventListener('securitypolicyviolation', (e) => seen.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await openFreshStore(page);

  // Browse, open details, register, check out signed in, read the order back.
  await page.getByRole('button', { name: 'Jewelry', exact: true }).click();
  await page.getByRole('button', { name: 'All products' }).click();
  await page.getByRole('link', { name: PRODUCTS.jacket.title }).click();
  await page.getByRole('button', { name: 'Close product details' }).click();
  const email = uniqueEmail('csp');
  await register(page, email);
  await page.keyboard.press('Escape');
  await addToCart(page, PRODUCTS.shirt.title);
  const checkout = await openCheckout(page);
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();
  await expect(page.getByRole('dialog', { name: 'Order placed' })).toBeVisible();
  await page.getByRole('button', { name: 'Continue shopping' }).click();
  await accountButton(page).click();
  await expect(page.getByRole('dialog', { name: 'Your account' }).getByRole('button', { name: /DEMO-/ })).toBeVisible();

  const violations = await page.evaluate(() => (window as unknown as { __csp: string[] }).__csp);
  // Web fonts are allowed by the policy; on machines without internet they simply fail to load.
  expect(violations.filter((v) => !/fonts\.(googleapis|gstatic)/.test(v))).toEqual([]);
  expect(errors).toEqual([]);
});

test('the site refuses to be framed by another page', async ({ page }) => {
  await page.setContent('<iframe id="shop" src="http://localhost:5174/"></iframe>');
  const frame = page.frame({ url: /localhost:5174/ });
  // Blocked by frame-ancestors / X-Frame-Options: the frame never renders the shop.
  await page.waitForTimeout(1500);
  const rendered = frame ? await frame.locator('h1').count().catch(() => 0) : 0;
  expect(rendered).toBe(0);
});
