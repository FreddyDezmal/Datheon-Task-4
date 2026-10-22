import { useEffect, useId, useRef, useState } from 'react';
import { useCart } from '../context/CartContext';
import { usePricingRules } from '../context/StoreConfigContext';
import { useModalBehavior } from '../hooks/useModalBehavior';
import { ApiError } from '../services/apiClient';
import { submitCheckout } from '../services/checkoutService';
import { parseOrderQuote } from '../services/responseParsers';
import type { CheckoutConfirmation, CustomerDetails } from '../types/checkout';
import { validateCustomer, type CustomerErrors } from '../utils/checkoutValidation';
import { formatMoney, minorToMajor } from '../utils/money';
import { CheckoutForm } from './CheckoutForm';
import { CloseIcon } from './Icons';
import { OrderConfirmation } from './OrderConfirmation';

interface CheckoutDialogProps {
  isOpen: boolean;
  onClose: () => void;
}

export type CheckoutProblem =
  | { kind: 'price-changed'; message: string }
  | { kind: 'unavailable'; message: string; productIds: number[] }
  | { kind: 'retryable'; message: string }
  | { kind: 'other'; message: string };

type Step = { name: 'details' } | { name: 'submitting' } | { name: 'confirmed'; confirmation: CheckoutConfirmation };

/**
 * Checkout flow: details → submitting → confirmed.
 * The cart is only cleared after the server confirms the order; every failure keeps it intact.
 */
export function CheckoutDialog({ isOpen, onClose }: CheckoutDialogProps) {
  const cart = useCart();
  const { currency, locale } = usePricingRules();
  const panelRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const nameInputRef = useRef<HTMLInputElement>(null);

  const [step, setStep] = useState<Step>({ name: 'details' });
  const [customer, setCustomer] = useState<CustomerDetails>({ name: '', email: '' });
  const [fieldErrors, setFieldErrors] = useState<CustomerErrors>({});
  const [problem, setProblem] = useState<CheckoutProblem | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Start fresh each time the dialog opens, but keep typed details for convenience.
  useEffect(() => {
    if (isOpen) {
      setStep({ name: 'details' });
      setProblem(null);
      setFieldErrors({});
    } else {
      abortRef.current?.abort();
    }
  }, [isOpen]);

  const isSubmitting = step.name === 'submitting';
  const close = () => {
    if (!isSubmitting) onClose();
  };
  useModalBehavior(isOpen, close, panelRef, nameInputRef);

  const submit = async () => {
    const errors = validateCustomer(customer);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;
    if (cart.items.length === 0) {
      setProblem({ kind: 'other', message: 'Your cart is empty. Add a product before checking out.' });
      return;
    }

    setProblem(null);
    setStep({ name: 'submitting' });
    const controller = new AbortController();
    abortRef.current = controller;

    try {
      const confirmation = await submitCheckout(
        {
          customer: { name: customer.name.trim(), email: customer.email.trim() },
          items: cart.items.map((i) => ({ productId: i.productId, quantity: i.quantity })),
          expectedTotal: minorToMajor(cart.totals.totalMinor),
        },
        controller.signal,
      );
      cart.clearCart();
      setStep({ name: 'confirmed', confirmation });
    } catch (error) {
      if (controller.signal.aborted) return;
      setStep({ name: 'details' });
      handleFailure(error);
    }
  };

  const handleFailure = (error: unknown) => {
    if (!(error instanceof ApiError)) {
      setProblem({ kind: 'retryable', message: 'Something went wrong. Your cart is saved, so you can try again.' });
      return;
    }

    switch (error.code) {
      case 'PRICE_CHANGED': {
        try {
          const quote = parseOrderQuote(error.problem?.quote);
          cart.syncPrices(quote.lines);
          const total = formatMoney(Math.round(quote.totals.total * 100), currency, locale);
          setProblem({
            kind: 'price-changed',
            message: `Some prices changed since you added these items. Your new total is ${total}. Place the order again to confirm.`,
          });
        } catch {
          setProblem({ kind: 'retryable', message: 'Prices changed, but the updated quote could not be read. Try again.' });
        }
        return;
      }
      case 'PRODUCTS_UNAVAILABLE': {
        const ids = Array.isArray(error.problem?.unavailableProductIds)
          ? (error.problem.unavailableProductIds as unknown[]).filter((v): v is number => typeof v === 'number')
          : [];
        const names = cart.items.filter((i) => ids.includes(i.productId)).map((i) => i.title);
        setProblem({
          kind: 'unavailable',
          productIds: ids,
          message: names.length
            ? `These items are no longer available: ${names.join(', ')}.`
            : 'Some items in your cart are no longer available.',
        });
        return;
      }
      case 'VALIDATION_FAILED': {
        const errors = error.problem?.errors ?? {};
        const mapped: CustomerErrors = {};
        if (errors['customer.name']?.[0]) mapped.name = errors['customer.name'][0];
        if (errors['customer.email']?.[0]) mapped.email = errors['customer.email'][0];
        setFieldErrors(mapped);
        const other = Object.entries(errors).find(([key]) => !key.startsWith('customer.'));
        setProblem({ kind: 'other', message: other?.[1][0] ?? 'Check the highlighted fields and try again.' });
        return;
      }
      default:
        setProblem(
          error.isRetryable
            ? { kind: 'retryable', message: `${error.message} Your cart is saved, so you can try again.` }
            : { kind: 'other', message: error.message },
        );
    }
  };

  const removeUnavailable = () => {
    if (problem?.kind !== 'unavailable') return;
    cart.removeProducts(problem.productIds);
    setProblem(null);
  };

  return (
    <div className={`dialog${isOpen ? ' is-open' : ''}`} inert={!isOpen}>
      <div className="dialog__backdrop" onClick={close} aria-hidden="true" />
      <div
        ref={panelRef}
        className="dialog__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-hidden={!isOpen}
        aria-busy={isSubmitting}
        tabIndex={-1}
      >
        <header className="dialog__header">
          <h2 id={titleId} className="dialog__title">
            {step.name === 'confirmed' ? 'Order placed' : 'Check out'}
          </h2>
          <button type="button" className="icon-button" onClick={close} aria-label="Close checkout" disabled={isSubmitting}>
            <CloseIcon />
          </button>
        </header>

        {step.name === 'confirmed' ? (
          <OrderConfirmation confirmation={step.confirmation} locale={locale} onDone={onClose} />
        ) : (
          <CheckoutForm
            nameInputRef={nameInputRef}
            customer={customer}
            onCustomerChange={(next) => {
              setCustomer(next);
              // Clear a field's error as soon as it becomes valid.
              const errors = validateCustomer(next);
              setFieldErrors((current) => ({
                ...(current.name && errors.name ? { name: errors.name } : {}),
                ...(current.email && errors.email ? { email: errors.email } : {}),
              }));
            }}
            fieldErrors={fieldErrors}
            problem={problem}
            isSubmitting={isSubmitting}
            onSubmit={submit}
            onRemoveUnavailable={removeUnavailable}
            onCancel={close}
          />
        )}
      </div>
    </div>
  );
}
