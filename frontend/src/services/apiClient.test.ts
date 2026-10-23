import { describe, expect, it, vi } from 'vitest';
import { ApiError, apiRequest } from './apiClient';

const respond = (body: string | null, status: number, headers: Record<string, string> = {}) =>
  vi.stubGlobal('fetch', vi.fn(async () => new Response(body, { status, headers })));

const failureOf = async (promise: Promise<unknown>) => {
  try {
    await promise;
  } catch (error) {
    return error as ApiError;
  }
  throw new Error('expected the request to fail');
};

describe('apiRequest', () => {
  it('uses the problem title and keeps the code and details for callers', async () => {
    respond(JSON.stringify({ status: 409, title: 'Prices have changed.', code: 'PRICE_CHANGED', quote: { x: 1 } }), 409);
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error).toBeInstanceOf(ApiError);
    expect(error.message).toBe('Prices have changed.');
    expect(error.code).toBe('PRICE_CHANGED');
    expect(error.problem?.quote).toEqual({ x: 1 });
    expect(error.isRetryable).toBe(false);
  });

  it('reads Retry-After on 429 and says how long to wait', async () => {
    respond(JSON.stringify({ status: 429, title: 'Too Many Requests', code: 'TOO_MANY_REQUESTS' }), 429, { 'Retry-After': '30' });
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.retryAfterSeconds).toBe(30);
    expect(error.message).toBe('Too many requests. Wait 30 seconds and try again.');
    expect(error.isRetryable).toBe(true);
  });

  it('gives a safe message for a server error without a body', async () => {
    respond(null, 500);
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.message).toBe('The store is having a problem right now. Try again in a moment.');
    expect(error.code).toBe('HTTP_500');
    expect(error.isRetryable).toBe(true);
  });

  it('does not show an HTML error page to the user', async () => {
    respond('<html><body>Stack trace: at Storefront.Api…</body></html>', 502, { 'Content-Type': 'text/html' });
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.message).not.toMatch(/Stack trace|html/i);
  });

  it('reports a response that passes HTTP but fails validation as INVALID_RESPONSE', async () => {
    respond(JSON.stringify({ unexpected: true }), 200);
    const error = await failureOf(
      apiRequest('/x', () => {
        throw new Error('bad shape');
      }),
    );

    expect(error.code).toBe('INVALID_RESPONSE');
  });

  it('passes successful responses to onResponse so callers can read headers', async () => {
    respond(JSON.stringify({ ok: true }), 200, { 'Idempotent-Replayed': 'true' });
    let header: string | null = null;
    await apiRequest('/x', (d) => d, { onResponse: (r) => (header = r.headers.get('Idempotent-Replayed')) });

    expect(header).toBe('true');
  });

  it('maps a network failure to NETWORK_ERROR', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.code).toBe('NETWORK_ERROR');
    expect(error.status).toBe(0);
  });
});
