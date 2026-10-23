import { ProductCard, ProductCardSkeleton } from '../components/ProductCard';
import { SortSelect } from '../components/SortSelect';
import { ErrorReference } from '../components/ErrorReference';
import { StatusMessage } from '../components/StatusMessage';
import { useProducts } from '../hooks/useProducts';
import type { ApiError } from '../services/apiClient';
import type { CatalogQuery, Category, Product, SortOption } from '../types/product';

interface CatalogPageProps {
  /** Query used for fetching (search already debounced). */
  query: CatalogQuery;
  categories: readonly Category[];
  onSortChange: (sort: SortOption) => void;
  onResetFilters: () => void;
  onOpenProduct: (product: Product) => void;
}

const SKELETON_COUNT = 8;

/** What to tell the shopper when the product list can't be loaded at all, by cause. */
function outageAdvice(error: ApiError): string {
  switch (error.code) {
    case 'NETWORK_ERROR':
      return 'Your cart is saved on this device. The products will load again once the connection is back.';
    case 'TIMEOUT':
    case 'UPSTREAM_TIMEOUT':
      return 'This is usually temporary. Try again in a moment.';
    case 'UPSTREAM_UNAVAILABLE':
    case 'UPSTREAM_MALFORMED':
      return "Our product supplier isn't responding right now. Your cart is saved, so nothing is lost.";
    default:
      return error.isRetryable ? 'This is usually temporary. Your cart is saved.' : '';
  }
}

export function CatalogPage({ query, categories, onSortChange, onResetFilters, onOpenProduct }: CatalogPageProps) {
  const { status, products, error, stale, retry } = useProducts(query);

  const categoryName =
    query.category === 'all' ? 'All products' : (categories.find((c) => c.slug === query.category)?.name ?? 'Products');
  const isInitialLoad = status === 'loading' && products.length === 0;
  const hasFilters = query.search.trim() !== '' || query.category !== 'all';

  let content;
  if (isInitialLoad) {
    content = (
      <div className="product-grid" aria-hidden="true">
        {Array.from({ length: SKELETON_COUNT }, (_, i) => (
          <ProductCardSkeleton key={i} />
        ))}
      </div>
    );
  } else if (status === 'error' && products.length === 0) {
    content = (
      <StatusMessage
        tone="error"
        title="We couldn't load the catalogue"
        action={
          <button type="button" className="button button--primary" onClick={retry}>
            Try again
          </button>
        }
      >
        <p>{error.message}</p>
        {outageAdvice(error) && <p>{outageAdvice(error)}</p>}
        <ErrorReference error={error} />
      </StatusMessage>
    );
  } else if (products.length === 0 && status === 'success') {
    content = (
      <StatusMessage
        title="No products match"
        action={
          hasFilters ? (
            <button type="button" className="button button--secondary" onClick={onResetFilters}>
              Clear search and filters
            </button>
          ) : undefined
        }
      >
        <p>
          {query.search.trim()
            ? `Nothing in ${categoryName.toLowerCase()} matches “${query.search.trim()}”. Try a shorter search or another category.`
            : 'There are no products in this category yet.'}
        </p>
      </StatusMessage>
    );
  } else {
    content = (
      <>
        {stale && status !== 'error' && (
          <div className="alert alert--info" role="status">
            <p>
              We couldn't refresh the product list, so you're seeing a recently saved copy. Prices and availability are
              checked again when you place an order.
            </p>
            <button type="button" className="button button--secondary" onClick={retry} disabled={status === 'loading'}>
              Refresh
            </button>
          </div>
        )}
        {status === 'error' && (
          <div className="alert alert--error" role="alert">
            <p>{error.message} Showing the last results that loaded.</p>
            <button type="button" className="button button--secondary" onClick={retry}>
              Try again
            </button>
          </div>
        )}
        <div className={`product-grid${status === 'loading' ? ' is-refreshing' : ''}`}>
          {products.map((product) => (
            <ProductCard key={product.id} product={product} onOpenDetails={onOpenProduct} />
          ))}
        </div>
      </>
    );
  }

  return (
    <section className="catalog" aria-labelledby="catalog-heading" aria-busy={status === 'loading'}>
      <div className="catalog__toolbar">
        <div>
          <h1 id="catalog-heading" className="catalog__heading">
            {categoryName}
          </h1>
          <p className="catalog__count" aria-live="polite">
            {isInitialLoad
              ? 'Loading products…'
              : status === 'error' && products.length === 0
                ? ''
                : `${products.length} ${products.length === 1 ? 'product' : 'products'}${query.search.trim() ? ` for “${query.search.trim()}”` : ''}`}
          </p>
        </div>
        <SortSelect value={query.sort} onChange={onSortChange} />
      </div>
      {content}
    </section>
  );
}
