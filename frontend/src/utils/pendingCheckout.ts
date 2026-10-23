/**
 * The Idempotency-Key of a checkout whose outcome is unknown (the request may have been saved).
 * Kept in this tab's sessionStorage so the key survives a reload: until the server confirms an order
 * or reports the key as used for a different order, every attempt must reuse it, even if the shopper
 * edits their details or cart meanwhile. A new key there could place a second order.
 */
export const PENDING_CHECKOUT_KEY = 'greenhaus.pendingCheckout';

const KEY_PATTERN = /^[A-Za-z0-9_-]{8,100}$/;

function storage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function loadPendingCheckoutKey(): string | null {
  try {
    const value = storage()?.getItem(PENDING_CHECKOUT_KEY) ?? null;
    return value && KEY_PATTERN.test(value) ? value : null;
  } catch {
    return null;
  }
}

export function savePendingCheckoutKey(key: string): void {
  try {
    storage()?.setItem(PENDING_CHECKOUT_KEY, key);
  } catch {
    // Storage full or blocked: the key still lives in memory for this page.
  }
}

export function clearPendingCheckoutKey(): void {
  try {
    storage()?.removeItem(PENDING_CHECKOUT_KEY);
  } catch {
    // nothing to do
  }
}
