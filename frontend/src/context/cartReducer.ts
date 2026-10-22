import type { CartItem, CartProductInput, CartState } from '../types/cart';

/*
 * Pure cart reducer. Invariants it maintains:
 *  - at most one line per product;
 *  - every retained quantity is an integer in [1, maxQuantity];
 *  - state is never mutated; unchanged actions return the same object (no re-render).
 *
 * Decrement policy: quantities never go below 1 through updateQuantity. Removing a line is
 * an explicit action (the Remove button), so a mis-tap on "−" can't silently delete an item.
 */

export type CartAction =
  | { type: 'add'; product: CartProductInput; quantity?: number; maxQuantity: number }
  | { type: 'remove'; productId: number }
  | { type: 'removeMany'; productIds: readonly number[] }
  | { type: 'updateQuantity'; productId: number; quantity: number; maxQuantity: number }
  | { type: 'clear' }
  | { type: 'syncPrices'; lines: readonly { productId: number; unitPriceMinor: number; title: string }[] }
  | { type: 'replace'; items: CartItem[] };

export const initialCartState: CartState = { items: [] };

export function cartReducer(state: CartState, action: CartAction): CartState {
  switch (action.type) {
    case 'add': {
      const amount = action.quantity ?? 1;
      if (!Number.isInteger(amount) || amount < 1) return state;

      const existing = state.items.find((i) => i.productId === action.product.id);
      if (existing) {
        const quantity = Math.min(existing.quantity + amount, action.maxQuantity);
        if (quantity === existing.quantity) return state;
        return {
          items: state.items.map((i) => (i.productId === existing.productId ? { ...i, quantity } : i)),
        };
      }

      const item: CartItem = {
        productId: action.product.id,
        title: action.product.title,
        image: action.product.image,
        unitPriceMinor: action.product.priceMinor,
        quantity: Math.min(amount, action.maxQuantity),
      };
      return { items: [...state.items, item] };
    }

    case 'remove': {
      if (!state.items.some((i) => i.productId === action.productId)) return state;
      return { items: state.items.filter((i) => i.productId !== action.productId) };
    }

    case 'removeMany': {
      const ids = new Set(action.productIds);
      const items = state.items.filter((i) => !ids.has(i.productId));
      return items.length === state.items.length ? state : { items };
    }

    case 'updateQuantity': {
      const { productId, quantity, maxQuantity } = action;
      if (!Number.isInteger(quantity) || quantity < 1) return state;
      const clamped = Math.min(quantity, maxQuantity);
      const existing = state.items.find((i) => i.productId === productId);
      if (!existing || existing.quantity === clamped) return state;
      return { items: state.items.map((i) => (i.productId === productId ? { ...i, quantity: clamped } : i)) };
    }

    case 'clear':
      return state.items.length === 0 ? state : initialCartState;

    case 'syncPrices': {
      const byId = new Map(action.lines.map((l) => [l.productId, l]));
      let changed = false;
      const items = state.items.map((item) => {
        const line = byId.get(item.productId);
        if (!line || (line.unitPriceMinor === item.unitPriceMinor && line.title === item.title)) return item;
        changed = true;
        return { ...item, unitPriceMinor: line.unitPriceMinor, title: line.title };
      });
      return changed ? { items } : state;
    }

    case 'replace':
      return { items: action.items };
  }
}
