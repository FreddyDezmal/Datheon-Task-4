import type { AccountInfo, AuthTokens, Credentials, OrderDetails, OrderSummary } from '../types/account';
import { apiRequest } from './apiClient';
import { parseAccountInfo, parseAuthTokens, parseOrderDetails, parseOrderSummaries } from './responseParsers';

/*
 * The backend's account endpoints under /api/account (ASP.NET Core Identity underneath).
 * Tokens are opaque bearer tokens, sent as `Authorization: Bearer …`. Each sign-in is a session on the
 * server: a refresh token can be exchanged once (the response carries the next one), and signing out
 * ends the session there, so copied tokens stop working too.
 */

const bearer = (accessToken: string) => ({ Authorization: `Bearer ${accessToken}` });

/** 200 with an empty body on success; 400 VALIDATION_FAILED with Identity error codes otherwise. */
export async function register(credentials: Credentials): Promise<void> {
  await apiRequest('/api/account/register', () => undefined, { method: 'POST', body: credentials });
}

/** 401 UNAUTHORIZED with detail "Failed" (wrong email or password); 429 after repeated failures. */
export function login(credentials: Credentials): Promise<AuthTokens> {
  return apiRequest('/api/account/login', parseAuthTokens, { method: 'POST', body: credentials });
}

/** Exchanges a refresh token for new tokens (the old one is then used up). 401 when it has expired or been invalidated. */
export function refreshTokens(refreshToken: string): Promise<AuthTokens> {
  return apiRequest('/api/account/refresh', parseAuthTokens, { method: 'POST', body: { refreshToken } });
}

/** Ends the session on the server. Always 204; never throws (signing out locally must not depend on it). */
export async function logout(refreshToken: string): Promise<void> {
  try {
    await apiRequest('/api/account/logout', () => undefined, { method: 'POST', body: { refreshToken } });
  } catch {
    // Offline or the API is down: the tab forgets the session anyway, and the server-side session
    // expires on its own. Nothing useful to tell the shopper.
  }
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
