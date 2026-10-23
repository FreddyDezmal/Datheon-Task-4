import { describe, expect, it } from 'vitest';
import {
  parseAccountInfo,
  parseAuthTokens,
  parseCategories,
  parseCheckoutConfirmation,
  parseOrderDetails,
  parseOrderQuote,
  parseOrderSummaries,
  parseProduct,
  parseProductList,
  parseStoreConfig,
} from './responseParsers';

const product = {
  id: 2,
  title: 'Mens Casual Slim Fit T-Shirt',
  description: 'Cotton.',
  price: 22.3,
  priceMinor: 2230,
  category: 'mens-clothing',
  categoryName: "Men's Clothing",
  categoryGroup: 'clothing',
  image: 'https://example.test/2.jpg',
  rating: { rate: 4.1, count: 259 },
  badge: { code: 'best-value', label: 'Best Value' },
};

const line = { productId: 2, title: 'Shirt', unitPrice: 22.3, unitPriceMinor: 2230, quantity: 1, lineTotal: 22.3 };
const totals = { subtotal: 22.3, tax: 1.78, shipping: 5.99, total: 30.07, currency: 'USD' };

describe('parseProduct', () => {
  it('accepts the API shape', () => {
    expect(parseProduct(product)).toEqual(product);
  });

  it('treats a missing rating, badge and group as null', () => {
    const parsed = parseProduct({ ...product, rating: null, badge: undefined, categoryGroup: null });
    expect(parsed.rating).toBeNull();
    expect(parsed.badge).toBeNull();
    expect(parsed.categoryGroup).toBeNull();
  });

  it.each([
    ['a missing title', { ...product, title: undefined }],
    ['a price in cents that is not an integer', { ...product, priceMinor: 22.3 }],
    ['a non-finite price', { ...product, price: Number.POSITIVE_INFINITY }],
    ['a price given as a string', { ...product, price: '22.30' }],
    ['a rating without a count', { ...product, rating: { rate: 4 } }],
    ['an array instead of an object', [product]],
    ['null', null],
  ])('rejects %s', (_case, value) => {
    expect(() => parseProduct(value)).toThrow();
  });
});

describe('parseProductList', () => {
  it('drops malformed products and recounts, so one bad entry does not blank the page', () => {
    const list = parseProductList({ items: [product, { ...product, id: 'x' }, { ...product, id: 3 }], count: 3 });
    expect(list.items.map((p) => p.id)).toEqual([2, 3]);
    expect(list.count).toBe(2);
  });

  it('rejects a response without an items array', () => {
    expect(() => parseProductList({ count: 0 })).toThrow();
    expect(() => parseProductList([product])).toThrow();
  });
});

describe('parseCategories', () => {
  it('requires isGroup to be a real boolean', () => {
    const category = { slug: 'clothing', name: 'Clothing', productCount: 2, parentSlug: null, isGroup: true };
    expect(parseCategories([category])).toEqual([category]);
    expect(() => parseCategories([{ ...category, isGroup: 'true' }])).toThrow();
    expect(() => parseCategories({ items: [] })).toThrow();
  });
});

describe('parseStoreConfig', () => {
  const config = {
    currency: 'USD',
    locale: 'en-US',
    taxRateBasisPoints: 800,
    flatShippingFeeMinor: 599,
    freeShippingThresholdMinor: 5000,
    maxQuantityPerLine: 99,
    maxLineItems: 50,
  };

  it('accepts integer pricing rules and rejects fractional ones', () => {
    expect(parseStoreConfig(config)).toEqual(config);
    expect(() => parseStoreConfig({ ...config, taxRateBasisPoints: 8.5 })).toThrow();
    expect(() => parseStoreConfig({ ...config, currency: undefined })).toThrow();
  });
});

describe('checkout and order parsers', () => {
  const confirmation = {
    orderReference: 'DEMO-20261022-AAAAAA',
    createdAt: '2026-10-22T10:00:00Z',
    customerName: 'Ada',
    customerEmail: 'ada@example.com',
    lines: [line],
    totals,
    isSimulated: true,
    notice: 'Demo.',
  };

  it('parses a confirmation and rejects a non-boolean isSimulated', () => {
    expect(parseCheckoutConfirmation(confirmation)).toEqual(confirmation);
    expect(() => parseCheckoutConfirmation({ ...confirmation, isSimulated: 'yes' })).toThrow();
  });

  it('rejects a confirmation whose line quantity is fractional', () => {
    expect(() => parseCheckoutConfirmation({ ...confirmation, lines: [{ ...line, quantity: 1.5 }] })).toThrow();
  });

  it('parses a 409 quote and rejects one without totals', () => {
    expect(parseOrderQuote({ lines: [line], totals })).toEqual({ lines: [line], totals });
    expect(() => parseOrderQuote({ lines: [line] })).toThrow();
  });

  it('parses order history and order details', () => {
    const summary = { orderReference: 'DEMO-1', createdAt: '2026-10-22T10:00:00Z', itemCount: 2, total: 54.16, currency: 'USD' };
    expect(parseOrderSummaries([summary])).toEqual([summary]);
    expect(parseOrderSummaries([])).toEqual([]);
    expect(() => parseOrderSummaries([{ ...summary, itemCount: '2' }])).toThrow();

    const details = { orderReference: 'DEMO-1', createdAt: 'x', customerName: 'Ada', customerEmail: 'a@b.co', lines: [line], totals };
    expect(parseOrderDetails(details)).toEqual(details);
    expect(() => parseOrderDetails({ ...details, totals: null })).toThrow();
  });
});

describe('account parsers', () => {
  const tokens = { tokenType: 'Bearer', accessToken: 'a', expiresIn: 3600, refreshToken: 'r' };

  it('keeps only what the app needs from a token response', () => {
    expect(parseAuthTokens(tokens)).toEqual({ accessToken: 'a', refreshToken: 'r', expiresIn: 3600 });
  });

  it.each([
    ['an empty access token', { ...tokens, accessToken: '' }],
    ['an empty refresh token', { ...tokens, refreshToken: '' }],
    ['a zero lifetime', { ...tokens, expiresIn: 0 }],
    ['a negative lifetime', { ...tokens, expiresIn: -1 }],
    ['a missing refresh token', { tokenType: 'Bearer', accessToken: 'a', expiresIn: 3600 }],
  ])('rejects %s rather than storing an unusable session', (_case, value) => {
    expect(() => parseAuthTokens(value)).toThrow();
  });

  it('reads the account email', () => {
    expect(parseAccountInfo({ email: 'ada@example.com', isEmailConfirmed: false })).toEqual({ email: 'ada@example.com' });
    expect(() => parseAccountInfo({})).toThrow();
  });
});
