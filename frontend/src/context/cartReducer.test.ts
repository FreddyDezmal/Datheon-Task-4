import { describe, expect, it } from 'vitest';
import type { CartProductInput, CartState } from '../types/cart';
import { countUnits } from '../utils/pricing';
import { cartReducer, initialCartState, type CartAction } from './cartReducer';

const shirt: CartProductInput = { id: 2, title: 'T-Shirt', image: 'shirt.jpg', priceMinor: 2230 };
const drive: CartProductInput = { id: 9, title: 'Hard Drive', image: 'drive.jpg', priceMinor: 6400 };
const MAX = 99;

const run = (actions: CartAction[], state: CartState = initialCartState) => actions.reduce(cartReducer, state);
const add = (product: CartProductInput, quantity?: number): CartAction =>
  quantity === undefined ? { type: 'add', product, maxQuantity: MAX } : { type: 'add', product, quantity, maxQuantity: MAX };

describe('cartReducer', () => {
  it('adds a product as a new line with quantity 1', () => {
    const state = run([add(shirt)]);
    expect(state.items).toEqual([{ productId: 2, title: 'T-Shirt', image: 'shirt.jpg', unitPriceMinor: 2230, quantity: 1 }]);
  });

  it('increments the existing line when the same product is added again', () => {
    const state = run([add(shirt), add(shirt), add(drive), add(shirt)]);
    expect(state.items).toHaveLength(2);
    expect(state.items[0]?.quantity).toBe(3);
    expect(countUnits(state.items)).toBe(4); // the badge count
  });

  it('caps quantities at the configured maximum', () => {
    const state = run([add(shirt, 98), add(shirt, 5)]);
    expect(state.items[0]?.quantity).toBe(MAX);
  });

  it('increments and decrements with updateQuantity', () => {
    let state = run([add(shirt)]);
    state = cartReducer(state, { type: 'updateQuantity', productId: 2, quantity: 3, maxQuantity: MAX });
    expect(state.items[0]?.quantity).toBe(3);
    state = cartReducer(state, { type: 'updateQuantity', productId: 2, quantity: 2, maxQuantity: MAX });
    expect(state.items[0]?.quantity).toBe(2);
  });

  it.each([0, -1, 1.5, Number.NaN])('ignores invalid quantity %s (decrement below 1 is prevented)', (quantity) => {
    const before = run([add(shirt)]);
    const after = cartReducer(before, { type: 'updateQuantity', productId: 2, quantity, maxQuantity: MAX });
    expect(after).toBe(before);
    expect(after.items[0]?.quantity).toBe(1);
  });

  it('removes a line', () => {
    const state = run([add(shirt), add(drive), { type: 'remove', productId: 2 }]);
    expect(state.items.map((i) => i.productId)).toEqual([9]);
    expect(countUnits(state.items)).toBe(1);
  });

  it('removes several lines at once', () => {
    const state = run([add(shirt), add(drive), { type: 'removeMany', productIds: [2, 9] }]);
    expect(state.items).toEqual([]);
  });

  it('clears the cart', () => {
    expect(run([add(shirt), add(drive), { type: 'clear' }]).items).toEqual([]);
  });

  it('syncs unit prices from a server quote without touching quantities', () => {
    const state = run([
      add(shirt, 2),
      { type: 'syncPrices', lines: [{ productId: 2, unitPriceMinor: 2500, title: 'T-Shirt v2' }] },
    ]);
    expect(state.items[0]).toMatchObject({ unitPriceMinor: 2500, quantity: 2, title: 'T-Shirt v2' });
  });

  it('never mutates the previous state', () => {
    const before = run([add(shirt)]);
    const snapshot = structuredClone(before);
    run([add(shirt), { type: 'updateQuantity', productId: 2, quantity: 5, maxQuantity: MAX }, { type: 'remove', productId: 2 }], before);
    expect(before).toEqual(snapshot);
  });

  it('returns the same object for no-op actions', () => {
    const state = run([add(shirt)]);
    expect(cartReducer(state, { type: 'remove', productId: 404 })).toBe(state);
    expect(cartReducer(initialCartState, { type: 'clear' })).toBe(initialCartState);
  });
});
