import { useEffect, useId, useRef, useState } from 'react';
import { useCart } from '../context/CartContext';
import { usePricingRules } from '../context/StoreConfigContext';
import { useToast } from '../context/ToastContext';
import { useModalBehavior } from '../hooks/useModalBehavior';
import { ApiError, isAbortError } from '../services/apiClient';
import { fetchProduct } from '../services/productService';
import type { Product } from '../types/product';
import { formatMoney } from '../utils/money';
import { CloseIcon, PlusIcon, StarIcon } from './Icons';

interface ProductDetailsDialogProps {
  /** The product to show, or null when closed. */
  productId: number | null;
  /** Already-loaded catalogue data, shown immediately; otherwise the product is fetched. */
  initialProduct: Product | null;
  onClose: () => void;
  onViewCart: () => void;
}

type Load = { status: 'loading' } | { status: 'success'; product: Product } | { status: 'error'; error: ApiError };

export function ProductDetailsDialog({ productId, initialProduct, onClose, onViewCart }: ProductDetailsDialogProps) {
  const isOpen = productId !== null;
  const { addToCart, getQuantity } = useCart();
  const { currency, locale } = usePricingRules();
  const { notify } = useToast();
  const panelRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const titleId = useId();
  const [load, setLoad] = useState<Load>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [imageFailed, setImageFailed] = useState(false);

  useModalBehavior(isOpen, onClose, panelRef, closeRef);

  useEffect(() => {
    if (productId === null) return;
    setImageFailed(false);
    if (initialProduct && initialProduct.id === productId && attempt === 0) {
      setLoad({ status: 'success', product: initialProduct });
      return;
    }
    const controller = new AbortController();
    setLoad({ status: 'loading' });
    fetchProduct(productId, controller.signal)
      .then((product) => setLoad({ status: 'success', product }))
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setLoad({
          status: 'error',
          error: error instanceof ApiError ? error : new ApiError('Something went wrong loading this product.', 0, 'UNKNOWN'),
        });
      });
    return () => controller.abort();
  }, [productId, initialProduct, attempt]);

  const product = load.status === 'success' ? load.product : null;
  const inCart = product ? getQuantity(product.id) : 0;

  const handleAdd = () => {
    if (!product) return;
    const result = addToCart({ id: product.id, title: product.title, image: product.image, priceMinor: product.priceMinor });
    notify(
      result === 'limit-reached'
        ? `You already have the maximum quantity of ${product.title} in your cart.`
        : `Added ${product.title} to your cart.`,
      result === 'limit-reached' ? 'error' : 'success',
      'cart-add',
    );
  };

  return (
    <div className={`dialog${isOpen ? ' is-open' : ''}`} inert={!isOpen}>
      <div className="dialog__backdrop" onClick={onClose} aria-hidden="true" />
      <div
        ref={panelRef}
        className="dialog__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-hidden={!isOpen}
        aria-busy={load.status === 'loading'}
        tabIndex={-1}
      >
        <header className="dialog__header">
          <h2 id={titleId} className="dialog__title dialog__title--small">
            {product ? product.title : load.status === 'error' ? 'Product unavailable' : 'Loading product…'}
          </h2>
          <button ref={closeRef} type="button" className="icon-button" onClick={onClose} aria-label="Close product details">
            <CloseIcon />
          </button>
        </header>

        <div className="dialog__body">
          {load.status === 'loading' && (
            <div className="product-details product-details--loading" aria-hidden="true">
              <div className="product-details__media skeleton" />
              <div className="product-details__info">
                <div className="skeleton skeleton--line skeleton--short" />
                <div className="skeleton skeleton--line" />
                <div className="skeleton skeleton--line skeleton--medium" />
              </div>
            </div>
          )}

          {load.status === 'error' && (
            <div className="alert alert--error" role="alert">
              <p>
                {load.error.status === 404
                  ? 'This product is no longer in the catalogue.'
                  : `${load.error.message} The product details couldn't be loaded.`}
              </p>
              {load.error.status !== 404 && (
                <button type="button" className="button button--secondary" onClick={() => setAttempt((n) => n + 1)}>
                  Try again
                </button>
              )}
            </div>
          )}

          {product && (
            <div className="product-details" data-category={product.categoryGroup ?? product.category}>
              <div className="product-details__media">
                {imageFailed || !product.image ? (
                  <div className="product-card__image-fallback">{product.categoryName}</div>
                ) : (
                  <img src={product.image} alt={product.title} decoding="async" onError={() => setImageFailed(true)} />
                )}
              </div>
              <div className="product-details__info">
                <p className="product-card__category">{product.categoryName}</p>
                {product.badge && <p className={`badge badge--inline badge--${product.badge.code}`}>{product.badge.label}</p>}
                {product.rating && (
                  <p className="product-card__rating">
                    <StarIcon width={14} height={14} className="product-card__star" />
                    <span className="visually-hidden">Rated </span>
                    {product.rating.rate.toFixed(1)}
                    <span className="visually-hidden"> out of 5,</span>
                    <span className="product-card__reviews">
                      {' '}
                      ({product.rating.count.toLocaleString(locale)} reviews)
                    </span>
                  </p>
                )}
                <p className="product-details__price">{formatMoney(product.priceMinor, currency, locale)}</p>
                <p className="product-details__description">{product.description}</p>
                <div className="product-details__actions">
                  <button type="button" className="button button--primary button--large" onClick={handleAdd}>
                    <PlusIcon width={18} height={18} /> Add to cart
                  </button>
                  {inCart > 0 && (
                    <button type="button" className="button button--ghost button--large" onClick={onViewCart}>
                      View cart ({inCart} in cart)
                    </button>
                  )}
                </div>
                <p className="product-details__note">The price is confirmed by the store when you check out.</p>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
