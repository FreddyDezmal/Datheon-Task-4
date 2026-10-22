import { useCart } from '../context/CartContext';
import { usePricingRules } from '../context/StoreConfigContext';
import type { CartItem } from '../types/cart';
import { formatMoney } from '../utils/money';
import { calculateLineTotal } from '../utils/pricing';
import { QuantityStepper } from './QuantityStepper';

export function CartLineItem({ item }: { item: CartItem }) {
  const { updateQuantity, removeFromCart } = useCart();
  const { currency, locale, maxQuantityPerLine } = usePricingRules();

  return (
    <li className="cart-line">
      <div className="cart-line__media">
        {item.image ? <img src={item.image} alt="" loading="lazy" /> : null}
      </div>
      <div className="cart-line__details">
        <p className="cart-line__title">{item.title}</p>
        <p className="cart-line__unit">{formatMoney(item.unitPriceMinor, currency, locale)} each</p>
        <QuantityStepper
          quantity={item.quantity}
          max={maxQuantityPerLine}
          itemName={item.title}
          onChange={(quantity) => updateQuantity(item.productId, quantity)}
        />
      </div>
      <div className="cart-line__aside">
        <p className="cart-line__total">
          <span className="visually-hidden">Line total </span>
          {formatMoney(calculateLineTotal(item), currency, locale)}
        </p>
        <button
          type="button"
          className="link-button"
          onClick={() => removeFromCart(item.productId)}
          aria-label={`Remove ${item.title} from cart`}
        >
          Remove
        </button>
      </div>
    </li>
  );
}
