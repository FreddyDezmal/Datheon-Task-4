import { useEffect, useState } from 'react';
import { fetchCategories } from '../services/productService';
import type { Category } from '../types/product';

/** Used when /api/categories can't be reached, so the filters still render. */
export const FALLBACK_CATEGORIES: Category[] = [
  { slug: 'electronics', name: 'Electronics', productCount: 0, parentSlug: null, isGroup: false },
  { slug: 'jewelry', name: 'Jewelry', productCount: 0, parentSlug: null, isGroup: false },
  { slug: 'clothing', name: 'Clothing', productCount: 0, parentSlug: null, isGroup: true },
  { slug: 'mens-clothing', name: "Men's Clothing", productCount: 0, parentSlug: 'clothing', isGroup: false },
  { slug: 'womens-clothing', name: "Women's Clothing", productCount: 0, parentSlug: 'clothing', isGroup: false },
];

export function useCategories(): Category[] {
  const [categories, setCategories] = useState<Category[]>(FALLBACK_CATEGORIES);

  useEffect(() => {
    const controller = new AbortController();
    fetchCategories(controller.signal)
      .then((list) => {
        if (list.length > 0) setCategories(list);
      })
      .catch(() => {
        /* keep fallback */
      });
    return () => controller.abort();
  }, []);

  return categories;
}
