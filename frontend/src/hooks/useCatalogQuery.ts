import { useCallback, useEffect, useState } from 'react';
import type { CatalogQuery, SortOption } from '../types/product';

const SORT_OPTIONS: readonly SortOption[] = ['featured', 'price-asc', 'price-desc', 'rating'];
const SLUG_PATTERN = /^[a-z0-9-]{1,50}$/;

function readQueryFromUrl(): CatalogQuery {
  const params = new URLSearchParams(window.location.search);
  const sort = params.get('sort');
  const category = params.get('category');
  return {
    search: (params.get('q') ?? '').slice(0, 100),
    category: category && SLUG_PATTERN.test(category) ? category : 'all',
    sort: SORT_OPTIONS.includes(sort as SortOption) ? (sort as SortOption) : 'featured',
  };
}

function writeQueryToUrl(query: CatalogQuery) {
  const params = new URLSearchParams(window.location.search);
  const set = (key: string, value: string, defaultValue: string) => {
    if (value && value !== defaultValue) params.set(key, value);
    else params.delete(key);
  };
  set('q', query.search, '');
  set('category', query.category, 'all');
  set('sort', query.sort, 'featured');
  const qs = params.toString();
  const url = `${window.location.pathname}${qs ? `?${qs}` : ''}${window.location.hash}`;
  window.history.replaceState(window.history.state, '', url);
}

/** Catalogue search/filter/sort state, mirrored into the URL so views are shareable and survive refresh. */
export function useCatalogQuery() {
  const [query, setQuery] = useState<CatalogQuery>(readQueryFromUrl);

  useEffect(() => {
    writeQueryToUrl(query);
  }, [query]);

  const setSearch = useCallback((search: string) => setQuery((q) => ({ ...q, search })), []);
  const setCategory = useCallback((category: string) => setQuery((q) => ({ ...q, category })), []);
  const setSort = useCallback((sort: SortOption) => setQuery((q) => ({ ...q, sort })), []);
  const reset = useCallback(() => setQuery({ search: '', category: 'all', sort: 'featured' }), []);

  return { query, setSearch, setCategory, setSort, reset };
}

export { SORT_OPTIONS };
