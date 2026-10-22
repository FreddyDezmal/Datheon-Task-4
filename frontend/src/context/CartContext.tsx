import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, type ReactNode } from 'react';
import type { CartItem, CartProductInput, CartTotals } from '../types/cart';
import { loadCart, saveCart, CART_STORAGE_KEY, type CartLoadStatus } from '../utils/cartStorage';
import {
  calculateCartTotals,
  calculateShipping,
  calculateSubtotal,
  calculateTax,
  calculateTotal,
} from '../utils/pricing';
import { cartReducer, type CartAction } from './cartReducer';
import { usePricingRules } from './StoreConfigContext';

export type AddToCartResult = 'added' | 'incremented' | 'limit-reached';

export interface CartContextValue {
  items: readonly CartItem[];
  /** Derived from items on every change; never stored separately. */
  totals: CartTotals;
  /** How the cart was restored from storage on startup. */
  hydrationStatus: CartLoadStatus;
  addToCart: (product: CartProductInput, quantity?: number) => AddToCartResult;
  removeFromCart: (productId: number) => void;
  updateQuantity: (productId: number, quantity: number) => void;
  clearCart: () => void;
  removeProducts: (productIds: readonly number[]) => void;
  syncPrices: (lines: readonly { productId: number; unitPriceMinor: number; title: string }[]) => void;
  calculateSubtotal: () => number;
  calculateTax: () => number;
  calculateShipping: () => number;
  calculateTotal: () => number;
  getQuantity: (productId: number) => number;
}

const CartContext = createContext<CartContextValue | null>(null);

export function CartProvider({ children }: { children: ReactNode }) {
  const rules = usePricingRules();
  const maxQuantity = rules.maxQuantityPerLine;

  // Hydrate synchronously so the first render already shows the saved cart (no badge flicker).
  const hydration = useRef<CartLoadStatus>('empty');
  const [state, dispatch] = useReducer(cartReducer, undefined, () => {
    const loaded = loadCart(maxQuantity);
    hydration.current = loaded.status;
    return { items: loaded.items };
  });

  // Keep the latest items for callbacks without re-creating them on each change.
  const itemsRef = useRef(state.items);
  itemsRef.current = state.items;

  useEffect(() => {
    saveCart(state.items);
  }, [state.items]);

  // Keep several open tabs in sync.
  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      if (event.key !== CART_STORAGE_KEY) return;
      dispatch({ type: 'replace', items: loadCart(maxQuantity).items });
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, [maxQuantity]);

  const send = useCallback((action: CartAction) => dispatch(action), []);

  const addToCart = useCallback(
    (product: CartProductInput, quantity = 1): AddToCartResult => {
      const existing = itemsRef.current.find((i) => i.productId === product.id);
      if (existing && existing.quantity >= maxQuantity) return 'limit-reached';
      send({ type: 'add', product, quantity, maxQuantity });
      return existing ? 'incremented' : 'added';
    },
    [maxQuantity, send],
  );

  const totals = useMemo(() => calculateCartTotals(state.items, rules), [state.items, rules]);

  const value = useMemo<CartContextValue>(
    () => ({
      items: state.items,
      totals,
      hydrationStatus: hydration.current,
      addToCart,
      removeFromCart: (productId) => send({ type: 'remove', productId }),
      updateQuantity: (productId, quantity) => send({ type: 'updateQuantity', productId, quantity, maxQuantity }),
      clearCart: () => send({ type: 'clear' }),
      removeProducts: (productIds) => send({ type: 'removeMany', productIds }),
      syncPrices: (lines) => send({ type: 'syncPrices', lines }),
      calculateSubtotal: () => calculateSubtotal(state.items),
      calculateTax: () => calculateTax(calculateSubtotal(state.items), rules),
      calculateShipping: () => calculateShipping(calculateSubtotal(state.items), rules),
      calculateTotal: () => {
        const subtotal = calculateSubtotal(state.items);
        return calculateTotal(subtotal, calculateTax(subtotal, rules), calculateShipping(subtotal, rules));
      },
      getQuantity: (productId) => state.items.find((i) => i.productId === productId)?.quantity ?? 0,
    }),
    [state.items, totals, rules, addToCart, send, maxQuantity],
  );

  return <CartContext.Provider value={value}>{children}</CartContext.Provider>;
}

export function useCart(): CartContextValue {
  const context = useContext(CartContext);
  if (!context) throw new Error('useCart must be used inside <CartProvider>.');
  return context;
}
