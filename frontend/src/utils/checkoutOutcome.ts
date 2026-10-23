import type { ApiError } from '../services/apiClient';

/**
 * True when a checkout failed in a way that doesn't tell us whether the order was saved:
 * the request may have reached the server and committed before the connection dropped, the
 * response timed out, or the server answered 200 with a body we couldn't read.
 *
 * Only a retry with the same Idempotency-Key is safe after these. Failures the server reports
 * before saving anything (validation, price change, unavailable products, an outage of the
 * product catalogue, a busy or unavailable database) are not uncertain.
 */
/** 5xx codes the API only returns before anything is written (the order transaction never committed). */
const DEFINITE_SERVER_FAILURES = new Set<string>(['DATABASE_BUSY', 'DATABASE_UNAVAILABLE']);

export function isUncertainOutcome(error: ApiError): boolean {
  if (error.code === 'NETWORK_ERROR' || error.code === 'TIMEOUT' || error.code === 'INVALID_RESPONSE') return true;
  return error.status >= 500 && !error.code.startsWith('UPSTREAM_') && !DEFINITE_SERVER_FAILURES.has(error.code);
}
