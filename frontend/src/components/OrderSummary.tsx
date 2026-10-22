import { usePricingRules } from '../context/StoreConfigContext';
import type { CartTotals } from '../types/cart';
import { formatMoney } from '../utils/money';

/** Subtotal / tax / shipping / total, plus progress towards free shipping. */
export function OrderSummary({ totals }: { totals: CartTotals }) {
  const { currency, locale, taxRateBasisPoints, freeShippingThresholdMinor } = usePricingRules();
  const money = (minor: number) => formatMoney(minor, currency, locale);
  const taxPercent = (taxRateBasisPoints / 100).toLocaleString(locale, { maximumFractionDigits: 2 });
  const progress = Math.min(totals.subtotalMinor / freeShippingThresholdMinor, 1);

  return (
    <div className="order-summary">
      {totals.amountToFreeShippingMinor > 0 ? (
        <div className="shipping-progress">
          <p>
            Add <strong>{money(totals.amountToFreeShippingMinor)}</strong> more for free shipping.
          </p>
          <div
            className="shipping-progress__bar"
            role="progressbar"
            aria-label="Progress towards free shipping"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={Math.round(progress * 100)}
          >
            <span style={{ width: `${progress * 100}%` }} />
          </div>
        </div>
      ) : totals.subtotalMinor > 0 ? (
        <p className="shipping-progress shipping-progress--done">Your order ships free.</p>
      ) : null}

      <dl className="order-summary__rows">
        <div>
          <dt>Subtotal</dt>
          <dd data-testid="summary-subtotal">{money(totals.subtotalMinor)}</dd>
        </div>
        <div>
          <dt>Tax ({taxPercent}%)</dt>
          <dd data-testid="summary-tax">{money(totals.taxMinor)}</dd>
        </div>
        <div>
          <dt>Shipping</dt>
          <dd data-testid="summary-shipping">{totals.shippingMinor === 0 ? (totals.subtotalMinor > 0 ? 'Free' : money(0)) : money(totals.shippingMinor)}</dd>
        </div>
        <div className="order-summary__total">
          <dt>Total</dt>
          <dd data-testid="summary-total">{money(totals.totalMinor)}</dd>
        </div>
      </dl>
    </div>
  );
}
