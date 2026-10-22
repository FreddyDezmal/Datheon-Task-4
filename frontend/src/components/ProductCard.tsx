import { memo, useEffect, useRef, useState } from 'react';
import { useCart } from '../context/CartContext';
import { usePricingRules } from '../context/StoreConfigContext';
import { useToast } from '../context/ToastContext';
import type { Product } from '../types/product';
import { formatMoney } from '../utils/money';
import { CheckIcon, PlusIcon, StarIcon } from './Icons';

interface ProductCardProps {
  product: Product;
}

const FEEDBACK_MS = 1400;

export const ProductCard = memo(function ProductCard({ product }: ProductCardProps) {
  const { addToCart, getQuantity } = useCart();
  const { currency, locale } = usePricingRules();
  const { notify } = useToast();
  const [justAdded, setJustAdded] = useState(false);
  const [imageFailed, setImageFailed] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const inCart = getQuantity(product.id);

  useEffect(() => () => clearTimeout(timer.current), []);

  const handleAdd = () => {
    const result = addToCart({ id: product.id, title: product.title, image: product.image, priceMinor: product.priceMinor });
    if (result === 'limit-reached') {
      notify(`You already have the maximum quantity of ${product.title} in your cart.`, 'error', 'cart-add');
      return;
    }
    notify(result === 'added' ? `Added ${product.title} to your cart.` : `Added another ${product.title}.`, 'success', 'cart-add');
    setJustAdded(true);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setJustAdded(false), FEEDBACK_MS);
  };

  return (
    <article className="product-card" data-category={product.categoryGroup ?? product.category} aria-labelledby={`product-${product.id}-title`}>
      <div className="product-card__media">
        {imageFailed || !product.image ? (
          <div className="product-card__image-fallback" aria-hidden="true">
            {product.categoryName}
          </div>
        ) : (
          <img
            className="product-card__image"
            src={product.image}
            alt=""
            loading="lazy"
            decoding="async"
            onError={() => setImageFailed(true)}
          />
        )}
        {product.badge && <span className={`badge badge--${product.badge.code}`}>{product.badge.label}</span>}
      </div>

      <div className="product-card__body">
        <p className="product-card__category">{product.categoryName}</p>
        <h3 className="product-card__title" id={`product-${product.id}-title`} title={product.title}>
          {product.title}
        </h3>
        {product.rating && (
          <p className="product-card__rating">
            <StarIcon width={14} height={14} className="product-card__star" />
            <span className="visually-hidden">Rated </span>
            {product.rating.rate.toFixed(1)}
            <span className="visually-hidden"> out of 5,</span>
            <span className="product-card__reviews">
              {' '}
              ({product.rating.count.toLocaleString(locale)}
              <span className="visually-hidden"> reviews</span>)
            </span>
          </p>
        )}
      </div>

      <div className="product-card__footer">
        <p className="product-card__price">{formatMoney(product.priceMinor, currency, locale)}</p>
        <button
          type="button"
          className={`button button--primary product-card__add${justAdded ? ' is-added' : ''}`}
          onClick={handleAdd}
          aria-label={`Add ${product.title} to cart`}
        >
          {justAdded ? <CheckIcon width={18} height={18} /> : <PlusIcon width={18} height={18} />}
          <span>{justAdded ? 'Added' : 'Add to cart'}</span>
        </button>
      </div>
      {inCart > 0 && <p className="product-card__in-cart">{inCart} in your cart</p>}
    </article>
  );
});

export function ProductCardSkeleton() {
  return (
    <div className="product-card product-card--skeleton" aria-hidden="true">
      <div className="product-card__media skeleton" />
      <div className="product-card__body">
        <div className="skeleton skeleton--line skeleton--short" />
        <div className="skeleton skeleton--line" />
        <div className="skeleton skeleton--line skeleton--medium" />
      </div>
      <div className="product-card__footer">
        <div className="skeleton skeleton--price" />
        <div className="skeleton skeleton--button" />
      </div>
    </div>
  );
}
