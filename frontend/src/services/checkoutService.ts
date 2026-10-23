import type { CheckoutConfirmation, CheckoutRequest } from '../types/checkout';
import { bearer } from './accountService';
import { apiRequest } from './apiClient';
import { parseCheckoutConfirmation } from './responseParsers';

export const IDEMPOTENCY_KEY_HEADER = 'Idempotency-Key';
export const IDEMPOTENT_REPLAYED_HEADER = 'Idempotent-Replayed';

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
  /** When signed in: the order is saved to that account. Omit for a guest checkout. */
  accessToken?: string;
}

export interface CheckoutResult {
  confirmation: CheckoutConfirmation;
  /** True when the server returned the order an earlier attempt with the same key had already placed. */
  replayed: boolean;
}

/** Places an order (no payment is taken). Rejects with ApiError (see apiClient) on any failure. */
export async function submitCheckout(
  request: CheckoutRequest,
  { signal, idempotencyKey, accessToken }: SubmitOptions = {},
): Promise<CheckoutResult> {
  let replayed = false;
  const confirmation = await apiRequest('/api/checkout', parseCheckoutConfirmation, {
    method: 'POST',
    body: request,
    ...(signal ? { signal } : {}),
    headers: {
      ...(idempotencyKey ? { [IDEMPOTENCY_KEY_HEADER]: idempotencyKey } : {}),
      ...(accessToken ? bearer(accessToken) : {}),
    },
    onResponse: (response) => {
      replayed = response.headers.get(IDEMPOTENT_REPLAYED_HEADER) === 'true';
    },
  });
  return { confirmation, replayed };
}
