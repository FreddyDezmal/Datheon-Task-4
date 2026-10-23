import { describe, expect, it } from 'vitest';
import { ApiError } from '../services/apiClient';
import { isUncertainOutcome } from './checkoutOutcome';

const error = (status: number, code: string) => new ApiError('x', status, code);

describe('isUncertainOutcome', () => {
  it.each([
    ['network failure', error(0, 'NETWORK_ERROR')],
    ['client timeout', error(0, 'TIMEOUT')],
    ['200 with an unreadable body (the order was saved)', error(200, 'INVALID_RESPONSE')],
    ['database error while saving', error(500, 'DATABASE_ERROR')],
    ['unexpected server error', error(500, 'ERROR')],
    ['5xx without a problem body', error(500, 'HTTP_500')],
    ['bad gateway from a proxy', error(502, 'HTTP_502')],
  ])('%s might have placed the order', (_case, failure) => {
    expect(isUncertainOutcome(failure)).toBe(true);
  });

  it.each([
    ['product catalogue down', error(502, 'UPSTREAM_UNAVAILABLE')],
    ['product catalogue timed out', error(504, 'UPSTREAM_TIMEOUT')],
    ['product catalogue sent bad data', error(502, 'UPSTREAM_MALFORMED')],
    ['database busy (rejected before saving)', error(503, 'DATABASE_BUSY')],
    ['database unavailable (could not be opened, so nothing was saved)', error(503, 'DATABASE_UNAVAILABLE')],
    ['validation', error(400, 'VALIDATION_FAILED')],
    ['expired session', error(401, 'SESSION_EXPIRED')],
    ['price changed', error(409, 'PRICE_CHANGED')],
    ['request too large', error(413, 'REQUEST_TOO_LARGE')],
    ['unavailable products', error(422, 'PRODUCTS_UNAVAILABLE')],
    ['key reused', error(422, 'IDEMPOTENCY_KEY_REUSED')],
    ['rate limited', error(429, 'TOO_MANY_REQUESTS')],
  ])('%s is a definite failure: nothing was saved', (_case, failure) => {
    expect(isUncertainOutcome(failure)).toBe(false);
  });
});
