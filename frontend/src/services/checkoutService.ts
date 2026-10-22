import type { CheckoutConfirmation, CheckoutRequest } from '../types/checkout';
import { apiRequest } from './apiClient';
import { parseCheckoutConfirmation } from './responseParsers';

/** Submits a simulated order. Rejects with ApiError (see apiClient) on any failure. */
export function submitCheckout(request: CheckoutRequest, signal?: AbortSignal): Promise<CheckoutConfirmation> {
  return apiRequest('/api/checkout', parseCheckoutConfirmation, {
    method: 'POST',
    body: request,
    ...(signal ? { signal } : {}),
  });
}
