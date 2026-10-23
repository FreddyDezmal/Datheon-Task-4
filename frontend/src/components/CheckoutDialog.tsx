import { useEffect, useId, useRef, useState } from 'react';
import { useCart } from '../context/CartContext';
import { useSession } from '../context/SessionContext';
import { usePricingRules } from '../context/StoreConfigContext';
import { useModalBehavior } from '../hooks/useModalBehavior';
import { ApiError, isBrowserOffline } from '../services/apiClient';
import { createIdempotencyKey, submitCheckout, type CheckoutResult } from '../services/checkoutService';
import { isUncertainOutcome } from '../utils/checkoutOutcome';
import { clearPendingCheckoutKey, loadPendingCheckoutKey, savePendingCheckoutKey } from '../utils/pendingCheckout';
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
  /** Opens the sign-in dialog; checkout reopens afterwards with the details still filled in. */
  onSignIn: () => void;
}

export type CheckoutProblem = (
  | { kind: 'price-changed'; message: string }
  | { kind: 'unavailable'; message: string; productIds: number[] }
  | { kind: 'retryable'; message: string }
  /** The request may have reached the server: only a retry with the same idempotency key is safe. */
  | { kind: 'uncertain'; message: string }
  | { kind: 'session-expired'; message: string }
  | { kind: 'other'; message: string }
) & {
  /** The server's correlation ID for a 5xx failure, shown so the shopper can quote it. */
  reference?: string;
};

type Step =
  | { name: 'details' }
  | { name: 'submitting' }
  | { name: 'confirmed'; confirmation: CheckoutConfirmation; replayed: boolean; savedToAccount: boolean };



/**
 * Checkout flow: details → submitting → confirmed.
 * The cart is only cleared after the server confirms the order; every failure keeps it intact.
 */
export function CheckoutDialog({ isOpen, onClose, onSignIn }: CheckoutDialogProps) {
  const cart = useCart();
  const session = useSession();
  const { currency, locale } = usePricingRules();
  const panelRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const nameInputRef = useRef<HTMLInputElement>(null);

  const [step, setStep] = useState<Step>({ name: 'details' });
  const [customer, setCustomer] = useState<CustomerDetails>({ name: '', email: '' });
  const [fieldErrors, setFieldErrors] = useState<CustomerErrors>({});
  const [problem, setProblem] = useState<CheckoutProblem | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  // Guards against a second submit landing before React re-renders the disabled button.
  const inFlightRef = useRef(false);
  // One key per distinct order: a retry of the same request reuses it, and a change gets a new one,
  // unless an earlier attempt's outcome is unknown. Then the key is pinned (and kept across reloads)
  // until the server confirms an order or says the key belongs to a different one: the server
  // reconciles by key, so reusing it can never create a second order, while a new key could.
  const idempotencyRef = useRef<{ body: string; key: string; pinned: boolean } | null>(null);
  if (idempotencyRef.current === null) {
    const pending = loadPendingCheckoutKey();
    if (pending) idempotencyRef.current = { body: '', key: pending, pinned: true };
  }
  const pinKey = () => {
    if (!idempotencyRef.current) return;
    idempotencyRef.current.pinned = true;
    savePendingCheckoutKey(idempotencyRef.current.key);
  };
  const releaseKey = () => {
    idempotencyRef.current = null;
    clearPendingCheckoutKey();
  };

  // Start fresh each time the dialog opens, but keep typed details for convenience.
  useEffect(() => {
    if (isOpen) {
      setStep({ name: 'details' });
      setFieldErrors({});
      // Keep a session-expired notice if the shopper went to sign in and came back; clear the rest.
      setProblem((current) => (current?.kind === 'session-expired' && session.status !== 'signed-in' ? current : null));
    } else {
      abortRef.current?.abort();
    }
    // Only react to opening and closing, not to session changes while open.
  }, [isOpen]);

  // Signed-in shoppers start with their account email; anything they type takes precedence.
  useEffect(() => {
    if (isOpen && session.status === 'signed-in' && session.email) {
      setCustomer((current) => (current.email.trim() === '' ? { ...current, email: session.email ?? '' } : current));
    }
  }, [isOpen, session.status, session.email]);

  const isSubmitting = step.name === 'submitting';
  const close = () => {
    if (!isSubmitting) onClose();
  };
  useModalBehavior(isOpen, close, panelRef, nameInputRef);

  const submit = async () => {
    if (inFlightRef.current) return;
    const errors = validateCustomer(customer);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;
    if (cart.items.length === 0) {
      setProblem({ kind: 'other', message: 'Your cart is empty. Add a product before checking out.' });
      return;
    }
    // Without a connection the request can't arrive, so don't send it: the outcome stays certain.
    if (isBrowserOffline()) {
      setProblem({
        kind: 'retryable',
        message: "You're offline, so your order hasn't been sent. Your cart is saved. Place the order once you're back online.",
      });
      return;
    }

    setProblem(null);
    setStep({ name: 'submitting' });
    inFlightRef.current = true;
    const controller = new AbortController();
    abortRef.current = controller;

    const request = {
      customer: { name: customer.name.trim(), email: customer.email.trim() },
      items: cart.items.map((i) => ({ productId: i.productId, quantity: i.quantity })),
      expectedTotal: minorToMajor(cart.totals.totalMinor),
    };
    const body = JSON.stringify(request);
    if (!idempotencyRef.current) {
      idempotencyRef.current = { body, key: createIdempotencyKey(), pinned: false };
    } else if (idempotencyRef.current.body !== body && !idempotencyRef.current.pinned) {
      idempotencyRef.current = { body, key: createIdempotencyKey(), pinned: false };
    } else {
      idempotencyRef.current.body = body;
    }

    const idempotencyKey = idempotencyRef.current.key;
    const send = (accessToken?: string) =>
      submitCheckout(request, { signal: controller.signal, idempotencyKey, ...(accessToken ? { accessToken } : {}) });

    try {
      // Signed in: the order is saved to the account. An expired token is refreshed and the same
      // request (same idempotency key) is sent once more; the API rejects a stale token before saving.
      const asAccount = session.status !== 'anonymous';
      const result: CheckoutResult = asAccount ? await session.withAccessToken((token) => send(token)) : await send();
      releaseKey();
      cart.clearCart();
      setStep({ name: 'confirmed', confirmation: result.confirmation, replayed: result.replayed, savedToAccount: asAccount });
    } catch (error) {
      if (controller.signal.aborted) {
        pinKey(); // abandoned mid-request: it may have reached the server
        return;
      }
      setStep({ name: 'details' });
      handleFailure(error);
    } finally {
      inFlightRef.current = false;
    }
  };

  const handleFailure = (error: unknown) => {
    if (error instanceof ApiError && error.reference && error.status >= 500) {
      const reference = error.reference;
      reportProblem(error, (p) => setProblem({ ...p, reference }));
      return;
    }
    reportProblem(error, setProblem);
  };

  const reportProblem = (error: unknown, show: (problem: CheckoutProblem) => void) => {
    if (!(error instanceof ApiError)) {
      show({ kind: 'retryable', message: 'Something went wrong. Your cart is saved, so you can try again.' });
      return;
    }

    if (isUncertainOutcome(error)) {
      pinKey();
      show({
        kind: 'uncertain',
        message:
          "We couldn't confirm whether your order went through. Your cart is saved. Select Try again: if the order was placed, you'll see it instead of a second order being created.",
      });
      return;
    }

    switch (error.code) {
      case 'SESSION_EXPIRED':
        show({
          kind: 'session-expired',
          message: "Your session expired, so this order hasn't been placed. Sign in again to save it to your account, or place it as a guest.",
        });
        return;
      case 'IDEMPOTENCY_KEY_REUSED':
        // The earlier attempt with this key reached the store with different details or as a
        // different shopper. Start a new key, but tell the shopper first so they don't order twice.
        releaseKey();
        show({
          kind: 'other',
          message:
            session.status === 'signed-in'
              ? 'An earlier attempt at this order may already have been placed. Check Your account → Orders before placing it again.'
              : 'An earlier attempt at this order may already have been placed. Check before placing it again.',
        });
        return;
      case 'PRICE_CHANGED': {
        try {
          const quote = parseOrderQuote(error.problem?.quote);
          cart.syncPrices(quote.lines);
          const total = formatMoney(Math.round(quote.totals.total * 100), currency, locale);
          show({
            kind: 'price-changed',
            message: `Some prices changed since you added these items. Your new total is ${total}. Place the order again to confirm.`,
          });
        } catch {
          show({ kind: 'retryable', message: 'Prices changed, but the updated quote could not be read. Try again.' });
        }
        return;
      }
      case 'PRODUCTS_UNAVAILABLE': {
        const ids = Array.isArray(error.problem?.unavailableProductIds)
          ? (error.problem.unavailableProductIds as unknown[]).filter((v): v is number => typeof v === 'number')
          : [];
        const names = cart.items.filter((i) => ids.includes(i.productId)).map((i) => i.title);
        show({
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
        show({ kind: 'other', message: other?.[1][0] ?? 'Check the highlighted fields and try again.' });
        return;
      }
      default:
        show(
          error.isRetryable
            ? { kind: 'retryable', message: `${error.message} Your cart is saved.` }
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
          <OrderConfirmation
            confirmation={step.confirmation}
            replayed={step.replayed}
            savedToAccount={step.savedToAccount}
            locale={locale}
            onDone={onClose}
          />
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
            accountEmail={session.status === 'signed-in' ? session.email : null}
            onSignIn={onSignIn}
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
