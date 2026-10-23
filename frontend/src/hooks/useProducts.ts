import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, isAbortError } from '../services/apiClient';
import { fetchProductsWithFreshness } from '../services/productService';
import type { CatalogQuery, Product } from '../types/product';
import { useOnlineStatus } from './useOnlineStatus';

export type ProductsState =
  | { status: 'loading'; products: Product[]; error: null; stale: boolean }
  | { status: 'success'; products: Product[]; error: null; stale: boolean }
  | { status: 'error'; products: Product[]; error: ApiError; stale: boolean };

/**
 * Fetches products for a query. Superseded requests are aborted so a slow response can't
 * overwrite a newer one. Previous results stay visible while a new query loads.
 *
 * `stale` is true when the API answered from its last good copy of the catalogue (FakeStoreAPI was
 * unreachable). When the browser comes back online after a failed or stale load, the list is
 * fetched again exactly once; anything further is up to the shopper's Try again.
 */
export function useProducts(query: CatalogQuery) {
  const [state, setState] = useState<ProductsState>({ status: 'loading', products: [], error: null, stale: false });
  const [attempt, setAttempt] = useState(0);
  const online = useOnlineStatus();

  useEffect(() => {
    const controller = new AbortController();
    setState((s) => ({ status: 'loading', products: s.products, error: null, stale: s.stale }));

    fetchProductsWithFreshness(query, controller.signal)
      .then((list) => setState({ status: 'success', products: list.items, error: null, stale: list.stale }))
      .catch((error: unknown) => {
        if (isAbortError(error) || controller.signal.aborted) return;
        const apiError =
          error instanceof ApiError ? error : new ApiError('Something went wrong loading products.', 0, 'UNKNOWN');
        setState((s) => ({ status: 'error', products: s.products, error: apiError, stale: s.stale }));
      });

    return () => controller.abort();
  }, [query, attempt]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  // One automatic reload per offline → online transition, and only if the last load wasn't good.
  const needsReload = state.status === 'error' || state.stale;
  const wasOnline = useRef(online);
  useEffect(() => {
    const cameBack = online && !wasOnline.current;
    wasOnline.current = online;
    if (cameBack && needsReload) retry();
    // needsReload is read at the moment of reconnecting; changes to it alone must not trigger a reload.
  }, [online]);

  return { ...state, retry };
}
