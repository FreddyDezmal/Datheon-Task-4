import type { CatalogQuery, Category, Product, ProductList } from '../types/product';
import type { PricingRules } from '../types/store';
import { apiRequest, CATALOGUE_STALE_HEADER } from './apiClient';
import { parseCategories, parseProduct, parseProductList, parseStoreConfig } from './responseParsers';

export function buildProductQueryString(query: CatalogQuery): string {
  const params = new URLSearchParams();
  const search = query.search.trim();
  if (search) params.set('search', search);
  if (query.category && query.category !== 'all') params.set('category', query.category);
  if (query.sort !== 'featured') params.set('sort', query.sort);
  const qs = params.toString();
  return qs ? `?${qs}` : '';
}

export interface Freshness {
  /** True when the API served its last good copy because FakeStoreAPI couldn't be reached. */
  stale: boolean;
}

export async function fetchProductsWithFreshness(
  query: CatalogQuery,
  signal?: AbortSignal,
): Promise<ProductList & Freshness> {
  let stale = false;
  const list = await apiRequest(`/api/products${buildProductQueryString(query)}`, parseProductList, {
    ...(signal ? { signal } : {}),
    onResponse: (response) => {
      stale = response.headers.get(CATALOGUE_STALE_HEADER) === 'true';
    },
  });
  return { ...list, stale };
}

export function fetchProducts(query: CatalogQuery, signal?: AbortSignal): Promise<ProductList> {
  return apiRequest(`/api/products${buildProductQueryString(query)}`, parseProductList, signal ? { signal } : {});
}

/** 404 NOT_FOUND when the product doesn't exist (or is no longer in the catalogue). */
export function fetchProduct(id: number, signal?: AbortSignal): Promise<Product> {
  return apiRequest(`/api/products/${id}`, parseProduct, signal ? { signal } : {});
}

export function fetchCategories(signal?: AbortSignal): Promise<Category[]> {
  return apiRequest('/api/categories', parseCategories, signal ? { signal } : {});
}

export function fetchStoreConfig(signal?: AbortSignal): Promise<PricingRules> {
  return apiRequest('/api/store/config', parseStoreConfig, signal ? { signal } : {});
}
