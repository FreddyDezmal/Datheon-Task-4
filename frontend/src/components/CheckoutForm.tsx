import type { FormEvent, RefObject } from 'react';
import { useCart } from '../context/CartContext';
import { usePricingRules } from '../context/StoreConfigContext';
import type { CustomerDetails } from '../types/checkout';
import type { CustomerErrors } from '../utils/checkoutValidation';
import { EMAIL_MAX_LENGTH, NAME_MAX_LENGTH } from '../utils/checkoutValidation';
import { formatMoney } from '../utils/money';
import { calculateLineTotal } from '../utils/pricing';
import type { CheckoutProblem } from './CheckoutDialog';
import { OrderSummary } from './OrderSummary';

interface CheckoutFormProps {
  nameInputRef: RefObject<HTMLInputElement | null>;
  customer: CustomerDetails;
  onCustomerChange: (customer: CustomerDetails) => void;
  fieldErrors: CustomerErrors;
  problem: CheckoutProblem | null;
  isSubmitting: boolean;
  onSubmit: () => void;
  onRemoveUnavailable: () => void;
  onCancel: () => void;
}

export function CheckoutForm({
  nameInputRef,
  customer,
  onCustomerChange,
  fieldErrors,
  problem,
  isSubmitting,
  onSubmit,
  onRemoveUnavailable,
  onCancel,
}: CheckoutFormProps) {
  const { items, totals } = useCart();
  const { currency, locale } = usePricingRules();

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (!isSubmitting) onSubmit();
  };

  const submitLabel = problem?.kind === 'price-changed' ? 'Confirm new total and place order' : 'Place demo order';

  return (
    <form className="checkout-form" onSubmit={handleSubmit} noValidate>
      <div className="checkout-form__columns">
        <fieldset className="checkout-form__fields" disabled={isSubmitting}>
          <legend className="checkout-form__legend">Your details</legend>
          <p className="checkout-form__notice">
            This is a demo store. No payment is taken, and we only ask for a name and email to show a confirmation.
          </p>

          <div className={`field${fieldErrors.name ? ' field--invalid' : ''}`}>
            <label htmlFor="checkout-name">Full name</label>
            <input
              ref={nameInputRef}
              id="checkout-name"
              name="name"
              autoComplete="name"
              maxLength={NAME_MAX_LENGTH}
              value={customer.name}
              onChange={(e) => onCustomerChange({ ...customer, name: e.target.value })}
              aria-invalid={Boolean(fieldErrors.name)}
              aria-describedby={fieldErrors.name ? 'checkout-name-error' : undefined}
              required
            />
            {fieldErrors.name && (
              <p id="checkout-name-error" className="field__error">
                {fieldErrors.name}
              </p>
            )}
          </div>

          <div className={`field${fieldErrors.email ? ' field--invalid' : ''}`}>
            <label htmlFor="checkout-email">Email</label>
            <input
              id="checkout-email"
              name="email"
              type="email"
              inputMode="email"
              autoComplete="email"
              maxLength={EMAIL_MAX_LENGTH}
              value={customer.email}
              onChange={(e) => onCustomerChange({ ...customer, email: e.target.value })}
              aria-invalid={Boolean(fieldErrors.email)}
              aria-describedby={fieldErrors.email ? 'checkout-email-error' : undefined}
              required
            />
            {fieldErrors.email && (
              <p id="checkout-email-error" className="field__error">
                {fieldErrors.email}
              </p>
            )}
          </div>
        </fieldset>

        <section className="checkout-form__summary" aria-label="Order summary">
          <h3 className="checkout-form__summary-title">Order summary</h3>
          <ul className="checkout-items">
            {items.map((item) => (
              <li key={item.productId}>
                <span className="checkout-items__title">
                  {item.title} <span className="checkout-items__qty">× {item.quantity}</span>
                </span>
                <span>{formatMoney(calculateLineTotal(item), currency, locale)}</span>
              </li>
            ))}
          </ul>
          <OrderSummary totals={totals} />
        </section>
      </div>

      {problem && (
        <div className={`alert alert--${problem.kind === 'price-changed' ? 'info' : 'error'}`} role="alert">
          <p>{problem.message}</p>
          {problem.kind === 'unavailable' && (
            <button type="button" className="button button--secondary" onClick={onRemoveUnavailable}>
              Remove unavailable items
            </button>
          )}
        </div>
      )}

      <div className="checkout-form__actions">
        <button type="button" className="button button--ghost" onClick={onCancel} disabled={isSubmitting}>
          Back to cart
        </button>
        <button type="submit" className="button button--primary button--large" disabled={isSubmitting || items.length === 0}>
          {isSubmitting ? (
            <>
              <span className="spinner" aria-hidden="true" /> Placing order…
            </>
          ) : problem?.kind === 'retryable' ? (
            'Try again'
          ) : (
            submitLabel
          )}
        </button>
      </div>
    </form>
  );
}
