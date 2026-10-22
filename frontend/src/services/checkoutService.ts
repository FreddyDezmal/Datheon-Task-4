import type { CheckoutConfirmation, CheckoutRequest } from '../types/checkout';
import { apiRequest } from './apiClient';
import { parseCheckoutConfirmation } from './responseParsers';

export const IDEMPOTENCY_KEY_HEADER = 'Idempotency-Key';

/** A fresh opaque key for one checkout attempt (UUID, which the API accepts). */
export function createIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

interface SubmitOptions {
  signal?: AbortSignal;
  /**
   * Sent as the Idempotency-Key header. Reuse the same key when retrying the same order, so a
   * request that reached the server but whose response was lost doesn't create a second order.
   */
  idempotencyKey?: string;
}

/** Places an order (no payment is taken). Rejects with ApiError (see apiClient) on any failure. */
export function submitCheckout(
  request: CheckoutRequest,
  { signal, idempotencyKey }: SubmitOptions = {},
): Promise<CheckoutConfirmation> {
  return apiRequest('/api/checkout', parseCheckoutConfirmation, {
    method: 'POST',
    body: request,
    ...(signal ? { signal } : {}),
    ...(idempotencyKey ? { headers: { [IDEMPOTENCY_KEY_HEADER]: idempotencyKey } } : {}),
  });
}
