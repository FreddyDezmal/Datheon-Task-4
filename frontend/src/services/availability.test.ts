import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiRequest } from './apiClient';
import { fetchProductsWithFreshness } from './productService';

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

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('error reference', () => {
  it('comes from the X-Correlation-Id header', async () => {
    respond(JSON.stringify({ status: 500, title: 'Unexpected error.', traceId: 'from-body' }), 500, {
      'X-Correlation-Id': 'abc123',
    });
    expect((await failureOf(apiRequest('/x', () => null))).reference).toBe('abc123');
  });

  it("falls back to the problem's traceId", async () => {
    respond(JSON.stringify({ status: 503, title: 'Unavailable.', code: 'DATABASE_UNAVAILABLE', traceId: 'trace-7' }), 503);
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.reference).toBe('trace-7');
    expect(error.code).toBe('DATABASE_UNAVAILABLE');
    expect(error.isRetryable).toBe(true);
  });

  it('is dropped when it is not a plain identifier, so nothing arbitrary reaches the page', async () => {
    respond(JSON.stringify({ status: 500, title: 'Unexpected error.', traceId: '<img src=x onerror=alert(1)>' }), 500);
    expect((await failureOf(apiRequest('/x', () => null))).reference).toBeNull();
  });

  it('is absent when the request never got an answer', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));
    expect((await failureOf(apiRequest('/x', () => null))).reference).toBeNull();
  });
});

describe('network failure wording', () => {
  it('says the shopper is offline when the browser knows it has no connection', async () => {
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));

    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.code).toBe('NETWORK_ERROR');
    expect(error.message).toBe("You're offline. Check your internet connection.");
  });

  it('otherwise says the store could not be reached, without developer instructions', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));

    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.message).toBe("We couldn't reach the store. Check your connection, or try again in a moment.");
    expect(error.message).not.toMatch(/API|localhost|port/i);
  });
});

describe('fetchProductsWithFreshness', () => {
  it.each([
    [{ 'Catalogue-Stale': 'true' }, true],
    [{}, false],
    [{ 'Catalogue-Stale': 'false' }, false],
  ])('reads the stale flag from %o', async (headers, stale) => {
    respond(JSON.stringify({ items: [], count: 0 }), 200, { 'Content-Type': 'application/json', ...headers });

    const result = await fetchProductsWithFreshness({ search: '', category: 'all', sort: 'featured' });

    expect(result).toEqual({ items: [], count: 0, stale });
  });
});
