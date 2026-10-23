import type { AccountInfo, AuthTokens, Credentials, OrderDetails, OrderSummary } from '../types/account';
import { apiRequest } from './apiClient';
import { parseAccountInfo, parseAuthTokens, parseOrderDetails, parseOrderSummaries } from './responseParsers';

/*
 * The backend uses ASP.NET Core Identity's built-in API endpoints under /api/account.
 * Tokens are opaque bearer tokens: the browser stores nothing but sends `Authorization: Bearer …`.
 */

const bearer = (accessToken: string) => ({ Authorization: `Bearer ${accessToken}` });

/** 200 with an empty body on success; 400 VALIDATION_FAILED with Identity error codes otherwise. */
export async function register(credentials: Credentials): Promise<void> {
  await apiRequest('/api/account/register', () => undefined, { method: 'POST', body: credentials });
}

/** 401 UNAUTHORIZED with detail "Failed" (wrong email or password) or "LockedOut". */
export function login(credentials: Credentials): Promise<AuthTokens> {
  return apiRequest('/api/account/login', parseAuthTokens, { method: 'POST', body: credentials });
}

/** Exchanges a refresh token for new tokens. 401 when it has expired or been invalidated. */
export function refreshTokens(refreshToken: string): Promise<AuthTokens> {
  return apiRequest('/api/account/refresh', parseAuthTokens, { method: 'POST', body: { refreshToken } });
}

export function fetchAccountInfo(accessToken: string, signal?: AbortSignal): Promise<AccountInfo> {
  return apiRequest('/api/account/manage/info', parseAccountInfo, { headers: bearer(accessToken), ...(signal ? { signal } : {}) });
}

export function fetchMyOrders(accessToken: string, signal?: AbortSignal): Promise<OrderSummary[]> {
  return apiRequest('/api/orders', parseOrderSummaries, { headers: bearer(accessToken), ...(signal ? { signal } : {}) });
}

/** 404 when the reference doesn't exist or belongs to someone else. */
export function fetchOrder(accessToken: string, reference: string, signal?: AbortSignal): Promise<OrderDetails> {
  return apiRequest(`/api/orders/${encodeURIComponent(reference)}`, parseOrderDetails, {
    headers: bearer(accessToken),
    ...(signal ? { signal } : {}),
  });
}

export { bearer };
