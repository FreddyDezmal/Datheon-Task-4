import type { AccountInfo, AuthTokens, OrderDetails, OrderSummary } from '../types/account';
import type { CheckoutConfirmation, OrderLine, OrderQuote, OrderTotals } from '../types/checkout';
import type { Category, Product, ProductList } from '../types/product';
import type { PricingRules } from '../types/store';

/*
 * Small hand-written validators for API responses. The backend is ours, but the browser
 * should still never render or calculate with data of the wrong shape.
 */

class ShapeError extends Error {}

type Obj = Record<string, unknown>;

function obj(value: unknown, what: string): Obj {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ShapeError(`${what} is not an object`);
  return value as Obj;
}
function str(o: Obj, key: string): string {
  const v = o[key];
  if (typeof v !== 'string') throw new ShapeError(`${key} is not a string`);
  return v;
}
function nullableStr(o: Obj, key: string): string | null {
  const v = o[key];
  if (v === null || v === undefined) return null;
  if (typeof v !== 'string') throw new ShapeError(`${key} is not a string`);
  return v;
}
function num(o: Obj, key: string): number {
  const v = o[key];
  if (typeof v !== 'number' || !Number.isFinite(v)) throw new ShapeError(`${key} is not a number`);
  return v;
}
function int(o: Obj, key: string): number {
  const v = num(o, key);
  if (!Number.isInteger(v)) throw new ShapeError(`${key} is not an integer`);
  return v;
}
function arr(o: Obj, key: string): unknown[] {
  const v = o[key];
  if (!Array.isArray(v)) throw new ShapeError(`${key} is not an array`);
  return v;
}

export function parseProduct(value: unknown): Product {
  const o = obj(value, 'product');
  const rating = o.rating == null ? null : obj(o.rating, 'rating');
  const badge = o.badge == null ? null : obj(o.badge, 'badge');
  return {
    id: int(o, 'id'),
    title: str(o, 'title'),
    description: str(o, 'description'),
    price: num(o, 'price'),
    priceMinor: int(o, 'priceMinor'),
    category: str(o, 'category'),
    categoryName: str(o, 'categoryName'),
    categoryGroup: nullableStr(o, 'categoryGroup'),
    image: str(o, 'image'),
    rating: rating ? { rate: num(rating, 'rate'), count: int(rating, 'count') } : null,
    badge: badge ? { code: str(badge, 'code'), label: str(badge, 'label') } : null,
  };
}

/** Drops individual malformed products rather than failing the whole page. */
export function parseProductList(value: unknown): ProductList {
  const o = obj(value, 'product list');
  const items: Product[] = [];
  for (const item of arr(o, 'items')) {
    try {
      items.push(parseProduct(item));
    } catch {
      // Skip a bad entry; the rest of the catalogue still renders.
    }
  }
  return { items, count: items.length };
}

export function parseCategories(value: unknown): Category[] {
  if (!Array.isArray(value)) throw new ShapeError('categories is not an array');
  return value.map((item) => {
    const o = obj(item, 'category');
    const isGroup = o.isGroup;
    if (typeof isGroup !== 'boolean') throw new ShapeError('isGroup is not a boolean');
    return {
      slug: str(o, 'slug'),
      name: str(o, 'name'),
      productCount: int(o, 'productCount'),
      parentSlug: nullableStr(o, 'parentSlug'),
      isGroup,
    };
  });
}

export function parseStoreConfig(value: unknown): PricingRules {
  const o = obj(value, 'store config');
  return {
    currency: str(o, 'currency'),
    locale: str(o, 'locale'),
    taxRateBasisPoints: int(o, 'taxRateBasisPoints'),
    flatShippingFeeMinor: int(o, 'flatShippingFeeMinor'),
    freeShippingThresholdMinor: int(o, 'freeShippingThresholdMinor'),
    maxQuantityPerLine: int(o, 'maxQuantityPerLine'),
    maxLineItems: int(o, 'maxLineItems'),
  };
}

function parseOrderLine(value: unknown): OrderLine {
  const o = obj(value, 'order line');
  return {
    productId: int(o, 'productId'),
    title: str(o, 'title'),
    unitPrice: num(o, 'unitPrice'),
    unitPriceMinor: int(o, 'unitPriceMinor'),
    quantity: int(o, 'quantity'),
    lineTotal: num(o, 'lineTotal'),
  };
}

function parseTotals(value: unknown): OrderTotals {
  const o = obj(value, 'totals');
  return {
    subtotal: num(o, 'subtotal'),
    tax: num(o, 'tax'),
    shipping: num(o, 'shipping'),
    total: num(o, 'total'),
    currency: str(o, 'currency'),
  };
}

export function parseOrderQuote(value: unknown): OrderQuote {
  const o = obj(value, 'quote');
  return { lines: arr(o, 'lines').map(parseOrderLine), totals: parseTotals(o.totals) };
}

export function parseCheckoutConfirmation(value: unknown): CheckoutConfirmation {
  const o = obj(value, 'confirmation');
  const isSimulated = o.isSimulated;
  if (typeof isSimulated !== 'boolean') throw new ShapeError('isSimulated is not a boolean');
  return {
    orderReference: str(o, 'orderReference'),
    createdAt: str(o, 'createdAt'),
    customerName: str(o, 'customerName'),
    customerEmail: str(o, 'customerEmail'),
    lines: arr(o, 'lines').map(parseOrderLine),
    totals: parseTotals(o.totals),
    isSimulated,
    notice: str(o, 'notice'),
  };
}

export function parseAuthTokens(value: unknown): AuthTokens {
  const o = obj(value, 'tokens');
  const accessToken = str(o, 'accessToken');
  const refreshToken = str(o, 'refreshToken');
  const expiresIn = num(o, 'expiresIn');
  if (!accessToken || !refreshToken || expiresIn <= 0) throw new ShapeError('tokens are incomplete');
  return { accessToken, refreshToken, expiresIn };
}

export function parseAccountInfo(value: unknown): AccountInfo {
  return { email: str(obj(value, 'account'), 'email') };
}

export function parseOrderSummaries(value: unknown): OrderSummary[] {
  if (!Array.isArray(value)) throw new ShapeError('orders is not an array');
  return value.map((item) => {
    const o = obj(item, 'order');
    return {
      orderReference: str(o, 'orderReference'),
      createdAt: str(o, 'createdAt'),
      itemCount: int(o, 'itemCount'),
      total: num(o, 'total'),
      currency: str(o, 'currency'),
    };
  });
}

export function parseOrderDetails(value: unknown): OrderDetails {
  const o = obj(value, 'order');
  return {
    orderReference: str(o, 'orderReference'),
    createdAt: str(o, 'createdAt'),
    customerName: str(o, 'customerName'),
    customerEmail: str(o, 'customerEmail'),
    lines: arr(o, 'lines').map(parseOrderLine),
    totals: parseTotals(o.totals),
  };
}
