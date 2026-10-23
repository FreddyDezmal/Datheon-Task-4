import type { ReactNode } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DEFAULT_PRICING_RULES } from '../config/env';
import { jsonResponse, mockApi, pricingRules } from '../test/fixtures';
import { CART_STORAGE_KEY } from '../utils/cartStorage';
import { CartProvider, useCart } from './CartContext';
import { StoreConfigProvider, usePricingRules } from './StoreConfigContext';

describe('StoreConfigProvider', () => {
  it('uses the pricing rules the API publishes, so cart estimates match checkout', async () => {
    const serverRules = { ...pricingRules, taxRateBasisPoints: 1500, flatShippingFeeMinor: 1000 };
    mockApi({ routes: { '/api/store/config': () => jsonResponse(serverRules) } });

    const { result } = renderHook(() => usePricingRules(), { wrapper: StoreConfigProvider });

    expect(result.current).toEqual(DEFAULT_PRICING_RULES); // until the API answers
    await waitFor(() => expect(result.current.taxRateBasisPoints).toBe(1500));
  });

  it('keeps the built-in defaults when the API is unreachable or sends bad data', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));
    const offline = renderHook(() => usePricingRules(), { wrapper: StoreConfigProvider });
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(offline.result.current).toEqual(DEFAULT_PRICING_RULES);

    const fetchMock = mockApi({ routes: { '/api/store/config': () => jsonResponse({ currency: 'USD', taxRateBasisPoints: 'eight' }) } });
    const malformed = renderHook(() => usePricingRules(), { wrapper: StoreConfigProvider });
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await act(async () => {}); // let the rejected parse settle
    expect(malformed.result.current).toEqual(DEFAULT_PRICING_RULES);
  });
});

describe('CartProvider across tabs', () => {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <StoreConfigProvider initialRules={pricingRules}>
      <CartProvider>{children}</CartProvider>
    </StoreConfigProvider>
  );

  it('picks up a cart changed in another tab, and ignores unrelated storage events', () => {
    const { result } = renderHook(() => useCart(), { wrapper });
    expect(result.current.items).toEqual([]);

    const otherTab = JSON.stringify({
      version: 1,
      items: [{ productId: 2, title: 'Shirt', image: '', unitPriceMinor: 2230, quantity: 3 }],
    });
    act(() => {
      window.localStorage.setItem(CART_STORAGE_KEY, otherTab);
      window.dispatchEvent(new StorageEvent('storage', { key: 'something-else' }));
    });
    expect(result.current.items).toEqual([]);

    act(() => window.dispatchEvent(new StorageEvent('storage', { key: CART_STORAGE_KEY })));
    expect(result.current.totals.unitCount).toBe(3);
    expect(result.current.totals.subtotalMinor).toBe(6690);
  });

  it('fails loudly when used outside its provider', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => renderHook(() => useCart())).toThrow('useCart must be used inside <CartProvider>.');
  });
});
