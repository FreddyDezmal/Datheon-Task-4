import { useCallback, useEffect, useState } from 'react';
import { ApiError, isAbortError } from '../services/apiClient';
import { fetchProducts } from '../services/productService';
import type { CatalogQuery, Product } from '../types/product';

export type ProductsState =
  | { status: 'loading'; products: Product[]; error: null }
  | { status: 'success'; products: Product[]; error: null }
  | { status: 'error'; products: Product[]; error: ApiError };

/**
 * Fetches products for a query. Superseded requests are aborted so a slow response can't
 * overwrite a newer one. Previous results stay visible while a new query loads.
 */
export function useProducts(query: CatalogQuery) {
  const [state, setState] = useState<ProductsState>({ status: 'loading', products: [], error: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setState((s) => ({ status: 'loading', products: s.products, error: null }));

    fetchProducts(query, controller.signal)
      .then((list) => setState({ status: 'success', products: list.items, error: null }))
      .catch((error: unknown) => {
        if (isAbortError(error) || controller.signal.aborted) return;
        const apiError =
          error instanceof ApiError ? error : new ApiError('Something went wrong loading products.', 0, 'UNKNOWN');
        setState((s) => ({ status: 'error', products: s.products, error: apiError }));
      });

    return () => controller.abort();
  }, [query, attempt]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);
  return { ...state, retry };
}
