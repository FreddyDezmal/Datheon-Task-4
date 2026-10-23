import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearPendingCheckoutKey, loadPendingCheckoutKey, PENDING_CHECKOUT_KEY, savePendingCheckoutKey } from './pendingCheckout';

afterEach(() => vi.restoreAllMocks());

describe('pending checkout key', () => {
  it('round-trips a key and clears it', () => {
    savePendingCheckoutKey('0b9f3c1e-6a2d-4f7e-9c1b-2d3e4f5a6b7c');
    expect(loadPendingCheckoutKey()).toBe('0b9f3c1e-6a2d-4f7e-9c1b-2d3e4f5a6b7c');

    clearPendingCheckoutKey();
    expect(loadPendingCheckoutKey()).toBeNull();
  });

  it('ignores a stored value that is not a valid Idempotency-Key', () => {
    window.sessionStorage.setItem(PENDING_CHECKOUT_KEY, 'short');
    expect(loadPendingCheckoutKey()).toBeNull();

    window.sessionStorage.setItem(PENDING_CHECKOUT_KEY, 'has spaces in it');
    expect(loadPendingCheckoutKey()).toBeNull();
  });

  it('keeps working when storage is blocked', () => {
    vi.spyOn(window, 'sessionStorage', 'get').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError');
    });

    expect(() => savePendingCheckoutKey('0b9f3c1e-6a2d-4f7e')).not.toThrow();
    expect(loadPendingCheckoutKey()).toBeNull();
    expect(() => clearPendingCheckoutKey()).not.toThrow();
  });
});
