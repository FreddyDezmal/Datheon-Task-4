import { beforeEach, describe, expect, it } from 'vitest';
import { CART_STORAGE_KEY, loadCart, saveCart } from './cartStorage';

const validItem = { productId: 2, title: 'T-Shirt', image: 'x.jpg', unitPriceMinor: 2230, quantity: 2 };
const store = (value: unknown) => window.localStorage.setItem(CART_STORAGE_KEY, typeof value === 'string' ? value : JSON.stringify(value));

describe('cartStorage', () => {
  beforeEach(() => window.localStorage.clear());

  it('round-trips a saved cart', () => {
    saveCart([validItem]);
    expect(loadCart()).toEqual({ items: [validItem], status: 'restored' });
  });

  it('reports an empty cart when nothing is stored', () => {
    expect(loadCart()).toEqual({ items: [], status: 'empty' });
  });

  it('removes the key when the cart is emptied', () => {
    saveCart([validItem]);
    saveCart([]);
    expect(window.localStorage.getItem(CART_STORAGE_KEY)).toBeNull();
  });

  it.each([
    ['corrupt JSON', '{not json'],
    ['a bare array', [validItem]],
    ['an unknown version', { version: 99, items: [validItem] }],
    ['a legacy unversioned cart', { items: [validItem] }],
    ['items that are not an array', { version: 1, items: 'nope' }],
    ['null', 'null'],
  ])('discards %s without throwing, and without writing to storage', (_label, value) => {
    store(value);
    const before = window.localStorage.getItem(CART_STORAGE_KEY);
    expect(loadCart()).toEqual({ items: [], status: 'discarded' });
    // Pure: a second call (as React StrictMode makes) sees the same data and gives the same result.
    expect(window.localStorage.getItem(CART_STORAGE_KEY)).toBe(before);
    expect(loadCart()).toEqual({ items: [], status: 'discarded' });
  });

  it('drops invalid lines and keeps valid ones', () => {
    store({
      version: 1,
      items: [
        validItem,
        { ...validItem, productId: -1 },
        { ...validItem, productId: 3, title: '' },
        { ...validItem, productId: 4, unitPriceMinor: 12.5 },
        { ...validItem, productId: 5, quantity: 'two' },
        null,
      ],
    });
    expect(loadCart()).toEqual({ items: [validItem], status: 'repaired' });
  });

  it('repairs out-of-range quantities and merges duplicate lines', () => {
    store({
      version: 1,
      items: [
        { ...validItem, quantity: 0 },
        { ...validItem, productId: 9, quantity: 500 },
        { ...validItem, quantity: 3.7 },
      ],
    });
    const result = loadCart(99);
    expect(result.status).toBe('repaired');
    expect(result.items).toEqual([
      { ...validItem, quantity: 4 }, // 1 (repaired from 0) + 3 (truncated from 3.7)
      { ...validItem, productId: 9, quantity: 99 },
    ]);
  });

  it('survives storage that throws', () => {
    const broken = {
      getItem: () => {
        throw new Error('SecurityError');
      },
      setItem: () => {
        throw new Error('QuotaExceeded');
      },
      removeItem: () => {},
    } as unknown as Storage;
    expect(loadCart(99, broken)).toEqual({ items: [], status: 'empty' });
    expect(() => saveCart([validItem], broken)).not.toThrow();
  });
});
