import { expect, type APIRequestContext, type Page } from '@playwright/test';
import { API_URL, MOCK_URL } from './ports';

/** Test-only accounts: unique per run, on the throwaway e2e database. Never real customer data. */
export const uniqueEmail = (label: string) => `e2e-${label}-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.test`;
export const PASSWORD = 'Str0ng!Passw0rd';

/** Products from tools/fakestore_mock.py. */
export const PRODUCTS = {
  backpack: { id: 1, title: 'Fjallraven - Foldsack No. 1 Backpack, Fits 15 Laptops', priceMinor: 10995 },
  shirt: { id: 2, title: 'Mens Casual Premium Slim Fit T-Shirts', priceMinor: 2230 },
  jacket: { id: 3, title: 'Mens Cotton Jacket', priceMinor: 5599 },
} as const;

/** Switches the FakeStore stand-in between healthy and failing ("ok", "500", "malformed"). */
export async function setUpstream(request: APIRequestContext, mode: 'ok' | '500' | 'malformed') {
  const response = await request.get(`${MOCK_URL}/__mode/${mode}`);
  expect(response.ok()).toBe(true);
}

/** Opens the store with empty browser storage. */
export async function openFreshStore(page: Page) {
  await page.goto('/');
  await page.evaluate(() => {
    localStorage.clear();
    sessionStorage.clear();
  });
  await page.reload();
  await expect(page.getByRole('article').first()).toBeVisible();
}

/** Puts a cart in localStorage as the app saves it, e.g. one priced in an earlier session. */
export async function seedCart(page: Page, items: { productId: number; title: string; unitPriceMinor: number; quantity: number }[]) {
  await page.evaluate(
    (lines) => localStorage.setItem('greenhaus.cart', JSON.stringify({ version: 1, items: lines.map((l) => ({ image: '', ...l })) })),
    items,
  );
  await page.reload();
  await expect(page.getByRole('article').first()).toBeVisible();
}

export const cartButton = (page: Page) => page.getByRole('button', { name: /^Open cart/ });
export const accountButton = (page: Page) => page.getByTestId('account-button');

export async function addToCart(page: Page, title: string) {
  await page.getByRole('button', { name: `Add ${title} to cart` }).click();
}

export async function openCheckout(page: Page) {
  await cartButton(page).click();
  const cart = page.getByRole('dialog', { name: /Your cart/ });
  await cart.getByRole('button', { name: 'Check out' }).click();
  return page.getByRole('dialog', { name: 'Check out' });
}

export async function register(page: Page, email: string) {
  await accountButton(page).click();
  await page.getByRole('dialog', { name: 'Sign in' }).getByRole('button', { name: 'Create an account' }).click();
  // The same dialog is now titled "Create an account".
  const dialog = page.getByRole('dialog', { name: 'Create an account' });
  await dialog.getByLabel('Email').fill(email);
  await dialog.getByLabel('Password', { exact: true }).fill(PASSWORD);
  await dialog.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('dialog', { name: 'Your account' })).toBeVisible();
  await expect(accountButton(page)).toHaveAccessibleName(`Your account, signed in as ${email}`);
}

/** Signs in through the API (not the UI) and returns the account's orders: an independent check. */
export async function ordersFromApi(request: APIRequestContext, email: string) {
  const login = await request.post(`${API_URL}/api/account/login`, { data: { email, password: PASSWORD } });
  expect(login.ok()).toBe(true);
  const { accessToken } = (await login.json()) as { accessToken: string };
  const orders = await request.get(`${API_URL}/api/orders`, { headers: { Authorization: `Bearer ${accessToken}` } });
  expect(orders.ok()).toBe(true);
  return (await orders.json()) as { orderReference: string; total: number; itemCount: number }[];
}

/** Collects console errors and failed requests the test did not cause on purpose. */
export function watchForErrors(page: Page) {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`));
  page.on('console', (message) => {
    // "Failed to load resource" is the browser logging an HTTP error status; those are asserted
    // on directly by the tests that cause them. Web fonts may be blocked on offline machines.
    if (message.type() === 'error' && !/Failed to load resource|fonts\.(googleapis|gstatic)/.test(message.text())) {
      errors.push(`console: ${message.text()}`);
    }
  });
  return errors;
}
