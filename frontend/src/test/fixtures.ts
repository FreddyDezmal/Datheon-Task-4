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

type Handler = (url: URL, init: RequestInit | undefined) => Response | Promise<Response>;

/**
 * Installs a fake fetch that serves a tiny in-memory version of the API.
 * Filtering here is intentionally simple: the real filtering is tested on the backend;
 * the frontend tests check that the right query reaches the API and that results render.
 */
export function mockApi(overrides: { checkout?: Handler; products?: Handler } = {}) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);

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
