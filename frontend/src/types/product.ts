export interface ProductRating {
  rate: number;
  count: number;
}

export interface ProductBadge {
  code: 'top-rated' | 'popular' | 'best-value' | (string & {});
  label: string;
}

export interface Product {
  id: number;
  title: string;
  description: string;
  /** Unit price in major units, for display only. */
  price: number;
  /** Unit price in integer minor units (cents). Use this for arithmetic. */
  priceMinor: number;
  category: string;
  categoryName: string;
  categoryGroup: string | null;
  image: string;
  rating: ProductRating | null;
  badge: ProductBadge | null;
}

export interface ProductList {
  items: Product[];
  count: number;
}

export interface Category {
  slug: string;
  name: string;
  productCount: number;
  parentSlug: string | null;
  isGroup: boolean;
}

export type SortOption = 'featured' | 'price-asc' | 'price-desc' | 'rating';

export interface CatalogQuery {
  search: string;
  category: string;
  sort: SortOption;
}
