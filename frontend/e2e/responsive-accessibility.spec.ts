import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { PRODUCTS, accountButton, addToCart, cartButton, openFreshStore, setUpstream } from './support/shop';

/*
 * Layout and accessibility checks. The axe scan catches machine-detectable WCAG problems (missing
 * names, contrast, ARIA misuse); it is not proof of accessibility. Screen-reader and zoom checks
 * are in Documentation/Testing/ManualTestPlan.md.
 */

const WIDTHS = [
  { width: 320, height: 640 },
  { width: 375, height: 812 },
  { width: 768, height: 1024 },
  { width: 1024, height: 768 },
  { width: 1440, height: 900 },
];

/** Things that commonly break at small widths: sideways scrolling and controls pushed off-screen. */
async function layoutProblems(page: Page) {
  // Measure the settled layout, not a drawer or dialog halfway through sliding in.
  // Endless animations (the loading shimmer, spinners) are ignored.
  await page.waitForFunction(() =>
    document.getAnimations().every((a) => a.playState !== 'running' || a.effect?.getTiming().iterations === Infinity),
  );
  return page.evaluate(() => {
    const viewport = document.documentElement.clientWidth;
    const problems: string[] = [];
    if (document.documentElement.scrollWidth > viewport) {
      problems.push(`page scrolls sideways: ${document.documentElement.scrollWidth}px content in ${viewport}px`);
    }
    // Any visible button, link or field that extends past the right edge (the category chip row
    // scrolls horizontally by design, so it's left out).
    for (const el of document.querySelectorAll<HTMLElement>('button, a[href], input, select')) {
      if (el.closest('[inert], .category-nav__track')) continue;
      const box = el.getBoundingClientRect();
      if (box.width === 0 || getComputedStyle(el).visibility === 'hidden') continue;
      if (box.right > viewport + 1 || box.left < -1) {
        problems.push(`${el.tagName.toLowerCase()} "${(el.getAttribute('aria-label') ?? el.textContent ?? '').trim().slice(0, 40)}" is outside the viewport`);
      }
    }
    return problems;
  });
}

async function scan(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    // Decorative product images use alt="" on purpose: the product title is the accessible name.
    .exclude('.product-card__media')
    .analyze();
  return results.violations.map((v) => `${v.id} (${v.impact}): ${v.help} — ${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(', ')}`);
}

test.beforeEach(async ({ request }) => {
  await setUpstream(request, 'ok');
});

for (const size of WIDTHS) {
  test(`layout at ${size.width}px: catalogue, details, cart, checkout and sign-in fit the screen`, async ({ page }) => {
    await page.setViewportSize(size);
    await openFreshStore(page);
    expect(await layoutProblems(page), 'catalogue').toEqual([]);

    await page.getByRole('link', { name: PRODUCTS.backpack.title }).click();
    await expect(page.getByRole('dialog', { name: PRODUCTS.backpack.title })).toBeVisible();
    expect(await layoutProblems(page), 'product details').toEqual([]);
    await page.keyboard.press('Escape');

    await addToCart(page, PRODUCTS.backpack.title);
    await addToCart(page, PRODUCTS.shirt.title);
    await cartButton(page).click();
    const cart = page.getByRole('dialog', { name: /Your cart/ });
    await expect(cart.getByTestId('summary-total')).toBeVisible();
    expect(await layoutProblems(page), 'cart').toEqual([]);
    await expect(cart.getByRole('button', { name: 'Check out' })).toBeInViewport();

    await cart.getByRole('button', { name: 'Check out' }).click();
    const checkout = page.getByRole('dialog', { name: 'Check out' });
    await expect(checkout).toBeVisible();
    expect(await layoutProblems(page), 'checkout').toEqual([]);
    // Every line price is visible, however long the product title.
    for (const price of ['$109.95', '$22.30']) {
      await checkout.getByText(price, { exact: true }).first().scrollIntoViewIfNeeded();
      await expect(checkout.getByText(price, { exact: true }).first()).toBeInViewport();
    }
    // The submit button can be reached by scrolling inside the dialog.
    const submit = checkout.getByRole('button', { name: 'Place demo order' });
    await submit.scrollIntoViewIfNeeded();
    await expect(submit).toBeInViewport();
    await checkout.getByRole('button', { name: 'Close checkout' }).click();

    await accountButton(page).click();
    await expect(page.getByRole('dialog', { name: 'Sign in' })).toBeVisible();
    expect(await layoutProblems(page), 'sign in').toEqual([]);
  });
}

test('automated accessibility scan (axe, WCAG 2.2 A/AA rules) of every main view', async ({ page }) => {
  await openFreshStore(page);
  const found: Record<string, string[]> = {};

  found.catalogue = await scan(page);

  await page.getByRole('link', { name: PRODUCTS.shirt.title }).click();
  await expect(page.getByRole('dialog', { name: PRODUCTS.shirt.title })).toBeVisible();
  found.productDetails = await scan(page);
  await page.keyboard.press('Escape');

  await addToCart(page, PRODUCTS.shirt.title);
  await cartButton(page).click();
  await expect(page.getByRole('dialog', { name: /Your cart/ })).toBeVisible();
  found.cart = await scan(page);

  await page.getByRole('button', { name: 'Check out' }).click();
  const checkout = page.getByRole('dialog', { name: 'Check out' });
  await checkout.getByRole('button', { name: 'Place demo order' }).click(); // empty form: shows field errors
  await expect(checkout.getByText('Enter your name.')).toBeVisible();
  found.checkoutWithErrors = await scan(page);
  await checkout.getByRole('button', { name: 'Close checkout' }).click();

  await accountButton(page).click();
  await page.getByRole('dialog', { name: 'Sign in' }).getByRole('button', { name: 'Create an account' }).click();
  const register = page.getByRole('dialog', { name: 'Create an account' });
  await register.getByLabel('Password', { exact: true }).fill('short');
  found.register = await scan(page);

  expect(found).toEqual({ catalogue: [], productDetails: [], cart: [], checkoutWithErrors: [], register: [] });
});

test('keyboard: skip link first, dialogs trap focus, Escape closes and focus returns', async ({ page }) => {
  await openFreshStore(page);

  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'Skip to products' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('#main')).toBeFocused();

  // Open the cart from the keyboard; focus moves in and stays in while tabbing.
  await cartButton(page).focus();
  await page.keyboard.press('Enter');
  const cart = page.getByRole('dialog', { name: /Your cart/ });
  await expect(cart.getByRole('button', { name: 'Close cart' })).toBeFocused();
  for (let i = 0; i < 5; i += 1) {
    await page.keyboard.press('Tab');
    expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
  }
  await page.keyboard.press('Escape');
  await expect(cart).toBeHidden();
  await expect(cartButton(page)).toBeFocused();

  // The sign-in form announces errors and ties them to their fields.
  await accountButton(page).focus();
  await page.keyboard.press('Enter');
  const signIn = page.getByRole('dialog', { name: 'Sign in' });
  await expect(signIn.getByLabel('Email')).toBeFocused();
  await page.keyboard.press('Enter'); // submit the empty form
  await expect(signIn.getByLabel('Email')).toHaveAttribute('aria-invalid', 'true');
  await expect(signIn.getByLabel('Email')).toHaveAccessibleDescription('Enter your email address.');
  await page.keyboard.press('Escape');
  await expect(accountButton(page)).toBeFocused();
});

test('reduced motion turns transitions off', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openFreshStore(page);
  await cartButton(page).click();

  const duration = await page.getByRole('dialog', { name: /Your cart/ }).evaluate((el) => getComputedStyle(el).transitionDuration);
  expect(duration.split(',').every((d) => parseFloat(d) === 0)).toBe(true);
});

test('outage and offline notices fit a 320px screen and pass the axe scan', async ({ page, context, request }) => {
  await page.setViewportSize({ width: 320, height: 640 });
  await openFreshStore(page);
  await addToCart(page, PRODUCTS.shirt.title);
  const found: Record<string, string[]> = {};

  // FakeStoreAPI down: the catalogue is served from the API's saved copy with a notice.
  await setUpstream(request, '500');
  await page.reload();
  await expect(page.getByText('recently saved copy')).toBeVisible();
  found.staleCatalogue = [...(await layoutProblems(page)), ...(await scan(page))];

  // Checkout during the outage: a definite failure with a reference.
  await cartButton(page).click();
  await page.getByRole('button', { name: 'Check out' }).click();
  const checkout = page.getByRole('dialog', { name: 'Check out' });
  await checkout.getByLabel('Full name').fill('Ada Lovelace');
  await checkout.getByLabel('Email').fill('ada@example.test');
  await checkout.getByRole('button', { name: 'Place demo order' }).click();
  await expect(checkout.getByText(/Reference:/)).toBeVisible();
  found.checkoutOutage = [...(await layoutProblems(page)), ...(await scan(page))];
  await checkout.getByRole('button', { name: 'Close checkout' }).click();
  await setUpstream(request, 'ok');

  await context.setOffline(true);
  await expect(page.getByText("You're offline.")).toBeVisible();
  found.offline = [...(await layoutProblems(page)), ...(await scan(page))];
  await context.setOffline(false);

  expect(found).toEqual({ staleCatalogue: [], checkoutOutage: [], offline: [] });
});
