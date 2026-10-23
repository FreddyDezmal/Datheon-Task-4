import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AccountDialog } from './components/AccountDialog';
import { CartDrawer } from './components/CartDrawer';
import { CheckoutDialog } from './components/CheckoutDialog';
import { Navbar } from './components/Navbar';
import { ProductDetailsDialog } from './components/ProductDetailsDialog';
import { SEARCH_DEBOUNCE_MS } from './config/env';
import { CartProvider, useCart } from './context/CartContext';
import { SessionProvider } from './context/SessionContext';
import { StoreConfigProvider } from './context/StoreConfigContext';
import { ToastProvider, useToast } from './context/ToastContext';
import { useCatalogQuery } from './hooks/useCatalogQuery';
import { useCategories } from './hooks/useCategories';
import { useDebouncedValue } from './hooks/useDebouncedValue';
import { CatalogPage } from './pages/CatalogPage';
import type { Product } from './types/product';
import type { PricingRules } from './types/store';

export function AppProviders({ children, pricingRules }: { children: ReactNode; pricingRules?: PricingRules }) {
  return (
    <StoreConfigProvider {...(pricingRules ? { initialRules: pricingRules } : {})}>
      <ToastProvider>
        <SessionProvider>
          <CartProvider>{children}</CartProvider>
        </SessionProvider>
      </ToastProvider>
    </StoreConfigProvider>
  );
}

/** Only one overlay is open at a time, so focus trapping and Escape handling never compete. */
type Overlay =
  | { name: 'none' }
  | { name: 'cart' }
  | { name: 'checkout' }
  | { name: 'account'; returnTo: 'checkout' | null }
  | { name: 'product'; id: number; product: Product | null };

const PRODUCT_PARAM = 'product';

function productIdFromUrl(): number | null {
  const value = new URLSearchParams(window.location.search).get(PRODUCT_PARAM);
  if (!value || !/^\d{1,9}$/.test(value)) return null;
  const id = Number(value);
  return id > 0 ? id : null;
}

/** Mirrors the open product into the URL (?product=5) so the view can be shared and survives a refresh. */
function writeProductToUrl(id: number | null) {
  const params = new URLSearchParams(window.location.search);
  if (id === null) params.delete(PRODUCT_PARAM);
  else params.set(PRODUCT_PARAM, String(id));
  const qs = params.toString();
  window.history.replaceState(window.history.state, '', `${window.location.pathname}${qs ? `?${qs}` : ''}${window.location.hash}`);
}

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
  const { notify, dismissKey } = useToast();
  const [overlay, setOverlay] = useState<Overlay>(() => {
    const id = productIdFromUrl();
    return id === null ? { name: 'none' } : { name: 'product', id, product: null };
  });

  const hydrationNoticeShown = useRef(false);
  useEffect(() => {
    if (hydrationNoticeShown.current) return;
    hydrationNoticeShown.current = true;
    if (hydrationStatus === 'discarded') notify('Your saved cart could not be read, so it was reset.', 'info');
    if (hydrationStatus === 'repaired') notify('Some saved cart items were invalid and have been removed.', 'info');
  }, [hydrationStatus, notify]);

  useEffect(() => {
    writeProductToUrl(overlay.name === 'product' ? overlay.id : null);
    // "Added to cart" toasts would sit on top of the cart's own buttons on small screens.
    if (overlay.name === 'cart' || overlay.name === 'checkout') dismissKey('cart-add');
  }, [overlay, dismissKey]);

  const closeOverlay = useCallback(() => setOverlay({ name: 'none' }), []);
  const openCart = useCallback(() => setOverlay({ name: 'cart' }), []);
  const openAccount = useCallback(() => setOverlay({ name: 'account', returnTo: null }), []);
  const openProduct = useCallback((product: Product) => setOverlay({ name: 'product', id: product.id, product }), []);
  const openCheckout = useCallback(() => {
    if (items.length === 0) {
      notify('Your cart is empty. Add a product before checking out.', 'error');
      return;
    }
    setOverlay({ name: 'checkout' });
  }, [items.length, notify]);
  const signInFromCheckout = useCallback(() => setOverlay({ name: 'account', returnTo: 'checkout' }), []);
  const returnToCheckout = useCallback(() => setOverlay({ name: 'checkout' }), []);
  const closeAccount = useCallback(
    () => setOverlay((current) => (current.name === 'account' && current.returnTo === 'checkout' ? { name: 'checkout' } : { name: 'none' })),
    [],
  );

  const backgroundInert = overlay.name !== 'none';

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
          onCartOpen={openCart}
          onAccountOpen={openAccount}
          onBrandClick={reset}
        />
        <main id="main" className="page" tabIndex={-1}>
          <CatalogPage
            query={fetchQuery}
            categories={categories}
            onSortChange={setSort}
            onResetFilters={reset}
            onOpenProduct={openProduct}
          />
        </main>
        <footer className="site-footer">
          <p>Greenhaus is a demo storefront. Products come from FakeStoreAPI; no payment is taken for orders.</p>
        </footer>
      </div>

      <CartDrawer isOpen={overlay.name === 'cart'} onClose={closeOverlay} onCheckout={openCheckout} />
      <CheckoutDialog isOpen={overlay.name === 'checkout'} onClose={closeOverlay} onSignIn={signInFromCheckout} />
      <AccountDialog
        isOpen={overlay.name === 'account'}
        onClose={closeAccount}
        {...(overlay.name === 'account' && overlay.returnTo === 'checkout' ? { onSignedIn: returnToCheckout } : {})}
      />
      <ProductDetailsDialog
        productId={overlay.name === 'product' ? overlay.id : null}
        initialProduct={overlay.name === 'product' ? overlay.product : null}
        onClose={closeOverlay}
        onViewCart={openCart}
      />
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
