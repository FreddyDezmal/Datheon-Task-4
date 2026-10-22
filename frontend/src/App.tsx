import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { CartDrawer } from './components/CartDrawer';
import { CheckoutDialog } from './components/CheckoutDialog';
import { Navbar } from './components/Navbar';
import { SEARCH_DEBOUNCE_MS } from './config/env';
import { CartProvider, useCart } from './context/CartContext';
import { StoreConfigProvider } from './context/StoreConfigContext';
import { ToastProvider, useToast } from './context/ToastContext';
import { useCatalogQuery } from './hooks/useCatalogQuery';
import { useCategories } from './hooks/useCategories';
import { useDebouncedValue } from './hooks/useDebouncedValue';
import { CatalogPage } from './pages/CatalogPage';
import type { PricingRules } from './types/store';

export function AppProviders({ children, pricingRules }: { children: ReactNode; pricingRules?: PricingRules }) {
  return (
    <StoreConfigProvider {...(pricingRules ? { initialRules: pricingRules } : {})}>
      <ToastProvider>
        <CartProvider>{children}</CartProvider>
      </ToastProvider>
    </StoreConfigProvider>
  );
}

type Overlay = 'none' | 'cart' | 'checkout';

export function Storefront() {
  const { query, setSearch, setCategory, setSort, reset } = useCatalogQuery();
  const debouncedSearch = useDebouncedValue(query.search, SEARCH_DEBOUNCE_MS);
  const fetchQuery = useMemo(
    () => ({ ...query, search: debouncedSearch }),
    // query.search is deliberately left out: only the debounced value should trigger a fetch.
    [debouncedSearch, query.category, query.sort],
  );
  const categories = useCategories();
  const { totals, items, hydrationStatus } = useCart();
  const { notify } = useToast();
  const [overlay, setOverlay] = useState<Overlay>('none');

  const hydrationNoticeShown = useRef(false);
  useEffect(() => {
    if (hydrationNoticeShown.current) return;
    hydrationNoticeShown.current = true;
    if (hydrationStatus === 'discarded') notify('Your saved cart could not be read, so it was reset.', 'info');
    if (hydrationStatus === 'repaired') notify('Some saved cart items were invalid and have been removed.', 'info');
  }, [hydrationStatus, notify]);

  const closeOverlay = useCallback(() => setOverlay('none'), []);
  const openCheckout = useCallback(() => {
    if (items.length === 0) {
      notify('Your cart is empty. Add a product before checking out.', 'error');
      return;
    }
    setOverlay('checkout');
  }, [items.length, notify]);

  const backgroundInert = overlay !== 'none';

  return (
    <>
      <a className="skip-link" href="#main">
        Skip to products
      </a>
      <div className="app-shell" inert={backgroundInert}>
        <Navbar
          search={query.search}
          onSearchChange={setSearch}
          categories={categories}
          selectedCategory={query.category}
          onCategoryChange={setCategory}
          cartUnitCount={totals.unitCount}
          onCartOpen={() => setOverlay('cart')}
          onBrandClick={reset}
        />
        <main id="main" className="page">
          <CatalogPage query={fetchQuery} categories={categories} onSortChange={setSort} onResetFilters={reset} />
        </main>
        <footer className="site-footer">
          <p>Greenhaus is a demo storefront. Products come from FakeStoreAPI; no real orders are placed.</p>
        </footer>
      </div>

      <CartDrawer isOpen={overlay === 'cart'} onClose={closeOverlay} onCheckout={openCheckout} />
      <CheckoutDialog isOpen={overlay === 'checkout'} onClose={closeOverlay} />
    </>
  );
}

export default function App() {
  return (
    <AppProviders>
      <Storefront />
    </AppProviders>
  );
}
