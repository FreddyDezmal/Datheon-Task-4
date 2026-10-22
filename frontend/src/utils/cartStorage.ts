import type { CartItem } from '../types/cart';

export const CART_STORAGE_KEY = 'greenhaus.cart';
export const CART_STORAGE_VERSION = 1;
const DEFAULT_MAX_QUANTITY = 99;

interface StoredCart {
  version: typeof CART_STORAGE_VERSION;
  items: CartItem[];
}

export type CartLoadStatus =
  /** Nothing stored yet. */
  | 'empty'
  /** Stored cart was valid and fully restored. */
  | 'restored'
  /** Some lines were invalid and dropped, or quantities were repaired. */
  | 'repaired'
  /** Stored data was unreadable or from an unknown version and was discarded. */
  | 'discarded';

export interface CartLoadResult {
  items: CartItem[];
  status: CartLoadStatus;
}

function getStorage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null; // Access can throw when storage is disabled.
  }
}

const isPositiveInteger = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v) && v > 0;
const isNonNegativeInteger = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 0;

/** Returns a sanitized line, or null if the value can't be trusted. */
function sanitizeItem(value: unknown, maxQuantity: number): { item: CartItem; repaired: boolean } | null {
  if (typeof value !== 'object' || value === null) return null;
  const raw = value as Record<string, unknown>;

  if (!isPositiveInteger(raw.productId)) return null;
  if (typeof raw.title !== 'string' || raw.title.trim() === '') return null;
  if (!isNonNegativeInteger(raw.unitPriceMinor)) return null;
  if (typeof raw.quantity !== 'number' || !Number.isFinite(raw.quantity)) return null;

  const quantity = Math.min(Math.max(Math.trunc(raw.quantity), 1), maxQuantity);
  return {
    item: {
      productId: raw.productId,
      title: raw.title,
      image: typeof raw.image === 'string' ? raw.image : '',
      unitPriceMinor: raw.unitPriceMinor,
      quantity,
    },
    repaired: quantity !== raw.quantity || typeof raw.image !== 'string',
  };
}

export function loadCart(maxQuantity = DEFAULT_MAX_QUANTITY, storage = getStorage()): CartLoadResult {
  if (!storage) return { items: [], status: 'empty' };

  let raw: string | null;
  try {
    raw = storage.getItem(CART_STORAGE_KEY);
  } catch {
    return { items: [], status: 'empty' };
  }
  if (raw === null) return { items: [], status: 'empty' };

  // Read-only on purpose: this runs inside useReducer's initializer, which must be pure
  // (StrictMode calls it twice). The unreadable value is overwritten by the next saveCart.
  const discard = (): CartLoadResult => ({ items: [], status: 'discarded' });

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return discard();
  }

  if (typeof parsed !== 'object' || parsed === null) return discard();
  const stored = parsed as Partial<StoredCart>;
  if (stored.version !== CART_STORAGE_VERSION || !Array.isArray(stored.items)) return discard();

  const items: CartItem[] = [];
  let repaired = false;
  for (const value of stored.items) {
    const result = sanitizeItem(value, maxQuantity);
    if (!result) {
      repaired = true;
      continue;
    }
    repaired ||= result.repaired;

    const existing = items.find((i) => i.productId === result.item.productId);
    if (existing) {
      // Merge duplicates so the cart's "one line per product" invariant holds.
      existing.quantity = Math.min(existing.quantity + result.item.quantity, maxQuantity);
      repaired = true;
    } else {
      items.push(result.item);
    }
  }

  return { items, status: repaired ? 'repaired' : 'restored' };
}

export function saveCart(items: readonly CartItem[], storage = getStorage()): void {
  if (!storage) return;
  try {
    if (items.length === 0) {
      storage.removeItem(CART_STORAGE_KEY);
      return;
    }
    // Deterministic payload: rewriting an unchanged cart doesn't fire "storage" events in other tabs.
    const payload: StoredCart = { version: CART_STORAGE_VERSION, items: [...items] };
    storage.setItem(CART_STORAGE_KEY, JSON.stringify(payload));
  } catch {
    // Quota exceeded or storage disabled: the cart still works for this session.
  }
}
