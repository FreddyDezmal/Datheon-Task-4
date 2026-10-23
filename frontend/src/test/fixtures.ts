import { act } from '@testing-library/react';
import { vi } from 'vitest';
import type { Category, Product } from '../types/product';
import type { PricingRules } from '../types/store';

export const pricingRules: PricingRules = {
  currency: 'USD',
  locale: 'en-US',
  taxRateBasisPoints: 800,
  flatShippingFeeMinor: 599,
  freeShippingThresholdMinor: 5000,
  maxQuantityPerLine: 99,
  maxLineItems: 50,
};

const product = (p: Partial<Product> & Pick<Product, 'id' | 'title' | 'priceMinor' | 'category' | 'categoryName'>): Product => ({
  description: `${p.title} description`,
  price: p.priceMinor / 100,
  categoryGroup: null,
  image: `https://example.test/${p.id}.jpg`,
  rating: { rate: 4, count: 50 },
  badge: null,
  ...p,
});

export const products: Product[] = [
  product({ id: 2, title: 'Mens Casual Slim Fit T-Shirt', priceMinor: 2230, category: 'mens-clothing', categoryName: "Men's Clothing", categoryGroup: 'clothing', rating: { rate: 4.1, count: 259 } }),
  product({ id: 5, title: 'Gold Dragon Bracelet', priceMinor: 69500, category: 'jewelry', categoryName: 'Jewelry', badge: { code: 'top-rated', label: 'Top Rated' } }),
  product({ id: 9, title: 'WD 2TB Portable Hard Drive', priceMinor: 6400, category: 'electronics', categoryName: 'Electronics' }),
  product({ id: 19, title: "Women's Moisture Tee", priceMinor: 795, category: 'womens-clothing', categoryName: "Women's Clothing", categoryGroup: 'clothing', badge: { code: 'best-value', label: 'Best Value' } }),
];

export const categories: Category[] = [
  { slug: 'electronics', name: 'Electronics', productCount: 1, parentSlug: null, isGroup: false },
  { slug: 'jewelry', name: 'Jewelry', productCount: 1, parentSlug: null, isGroup: false },
  { slug: 'clothing', name: 'Clothing', productCount: 2, parentSlug: null, isGroup: true },
  { slug: 'mens-clothing', name: "Men's Clothing", productCount: 1, parentSlug: 'clothing', isGroup: false },
  { slug: 'womens-clothing', name: "Women's Clothing", productCount: 1, parentSlug: 'clothing', isGroup: false },
];

export const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

export type Handler = (url: URL, init: RequestInit | undefined) => Response | Promise<Response>;

/**
 * Installs a fake fetch that serves a tiny in-memory version of the API.
 * Filtering here is intentionally simple: the real filtering is tested on the backend;
 * the frontend tests check that the right query reaches the API and that results render.
 * `routes` adds handlers by exact path (e.g. '/api/orders'); a route wins over the defaults.
 */
export function mockApi(overrides: { checkout?: Handler; products?: Handler; routes?: Record<string, Handler> } = {}) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    const route = overrides.routes?.[url.pathname];
    if (route) return route(url, init);

    const productMatch = /^\/api\/products\/(\d+)$/.exec(url.pathname);
    if (productMatch) {
      const found = products.find((p) => p.id === Number(productMatch[1]));
      return found ? jsonResponse(found) : jsonResponse({ title: 'Product not found.', status: 404, code: 'NOT_FOUND' }, 404);
    }
    if (url.pathname === '/api/products') {
      if (overrides.products) return overrides.products(url, init);
      const search = url.searchParams.get('search')?.toLowerCase() ?? '';
      const category = url.searchParams.get('category');
      const items = products.filter(
        (p) =>
          p.title.toLowerCase().includes(search) &&
          (!category || p.category === category || p.categoryGroup === category),
      );
      return jsonResponse({ items, count: items.length });
    }
    if (url.pathname === '/api/categories') return jsonResponse(categories);
    if (url.pathname === '/api/store/config') return jsonResponse(pricingRules);
    if (url.pathname === '/api/checkout' && overrides.checkout) return overrides.checkout(url, init);
    return jsonResponse({ title: 'Not found', status: 404, code: 'NOT_FOUND' }, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

export const requestedUrls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.map(([input]) => new URL(String(input)));

/* ---------- Accounts ---------- */

export const problem = (status: number, code: string, extra: Record<string, unknown> = {}) =>
  jsonResponse({ type: 'about:blank', title: extra.title ?? 'Request failed.', status, code, traceId: 't', ...extra }, status);

export const unauthorized = (detail?: string) =>
  problem(401, 'UNAUTHORIZED', { title: 'Unauthorized', ...(detail ? { detail } : {}) });

let tokenCounter = 0;
/** Identity's token response: opaque strings, expiresIn in seconds. */
export const issueTokens = (expiresIn = 3600) => {
  tokenCounter += 1;
  return { tokenType: 'Bearer', accessToken: `access-${tokenCounter}`, expiresIn, refreshToken: `refresh-${tokenCounter}` };
};

export const authHeader = (init: RequestInit | undefined) => new Headers(init?.headers).get('Authorization');
export const bodyOf = (init: RequestInit | undefined) => JSON.parse(String(init?.body ?? 'null')) as Record<string, unknown>;

/** Saves a session in this tab, as the app does after signing in, so the next render restores it. */
export const seedSession = (email = 'ada@example.com', refreshToken = 'refresh-seeded') =>
  window.sessionStorage.setItem('greenhaus.session', JSON.stringify({ refreshToken, email }));

/** Routes for a working account: refresh and account info succeed. */
export const accountRoutes = (email = 'ada@example.com'): Record<string, Handler> => ({
  '/api/account/refresh': () => jsonResponse(issueTokens()),
  '/api/account/login': () => jsonResponse(issueTokens()),
  '/api/account/register': () => new Response(null, { status: 200 }),
  '/api/account/manage/info': (_url, init) =>
    authHeader(init) ? jsonResponse({ email, isEmailConfirmed: false }) : unauthorized(),
});

export const orderSummary = {
  orderReference: 'DEMO-20261022-AAAAAA',
  createdAt: '2026-10-22T10:00:00Z',
  itemCount: 2,
  total: 54.16,
  currency: 'USD',
};

export const orderDetails = {
  orderReference: 'DEMO-20261022-AAAAAA',
  createdAt: '2026-10-22T10:00:00Z',
  customerName: 'Ada Lovelace',
  customerEmail: 'ada@example.com',
  lines: [{ productId: 2, title: 'Mens Casual Slim Fit T-Shirt', unitPrice: 22.3, unitPriceMinor: 2230, quantity: 2, lineTotal: 44.6 }],
  totals: { subtotal: 44.6, tax: 3.57, shipping: 5.99, total: 54.16, currency: 'USD' },
};

export const callsTo = (fetchMock: ReturnType<typeof mockApi>, path: string) =>
  fetchMock.mock.calls.filter(([input]) => new URL(String(input)).pathname === path);

/**
 * Lets every pending fake request, promise and zero-delay timer run to completion. Use it before
 * asserting that something did NOT happen (e.g. no retry loop): with the fake API nothing is slow,
 * so a loop would show up within these turns. Not a timed sleep.
 */
export async function settle(turns = 10) {
  for (let i = 0; i < turns; i += 1) {
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 0)));
  }
}
