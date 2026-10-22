import type { CatalogQuery, Category, ProductList } from '../types/product';
import type { PricingRules } from '../types/store';
import { apiRequest } from './apiClient';
import { parseCategories, parseProductList, parseStoreConfig } from './responseParsers';

export function buildProductQueryString(query: CatalogQuery): string {
  const params = new URLSearchParams();
  const search = query.search.trim();
  if (search) params.set('search', search);
  if (query.category && query.category !== 'all') params.set('category', query.category);
  if (query.sort !== 'featured') params.set('sort', query.sort);
  const qs = params.toString();
  return qs ? `?${qs}` : '';
}

export function fetchProducts(query: CatalogQuery, signal?: AbortSignal): Promise<ProductList> {
  return apiRequest(`/api/products${buildProductQueryString(query)}`, parseProductList, signal ? { signal } : {});
}

export function fetchCategories(signal?: AbortSignal): Promise<Category[]> {
  return apiRequest('/api/categories', parseCategories, signal ? { signal } : {});
}

export function fetchStoreConfig(signal?: AbortSignal): Promise<PricingRules> {
  return apiRequest('/api/store/config', parseStoreConfig, signal ? { signal } : {});
}
