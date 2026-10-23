import { afterEach, describe, expect, it, vi } from 'vitest';
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

  it('reads an HTTP-date Retry-After as seconds from now', async () => {
    vi.useFakeTimers({ now: new Date('2026-10-22T10:00:00Z'), toFake: ['Date'] });
    respond(null, 429, { 'Retry-After': 'Thu, 22 Oct 2026 10:00:45 GMT' });
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.retryAfterSeconds).toBe(45);
  });

  it('ignores an unreadable Retry-After', async () => {
    respond(null, 429, { 'Retry-After': 'soon' });
    const error = await failureOf(apiRequest('/x', () => null));

    expect(error.retryAfterSeconds).toBeNull();
    expect(error.message).toBe('Too many requests. Wait a moment and try again.');
  });
});

describe('apiRequest request construction', () => {
  it('sends JSON with the right headers only when there is a body, and merges extra headers', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiRequest('/api/checkout', (d) => d, { method: 'POST', body: { a: 1 }, headers: { 'Idempotency-Key': 'key-12345' } });
    await apiRequest('/api/products', (d) => d);

    const [postUrl, post] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(postUrl).toBe('http://localhost:5080/api/checkout');
    expect(post.method).toBe('POST');
    expect(post.body).toBe('{"a":1}');
    expect(post.headers).toEqual({ Accept: 'application/json', 'Content-Type': 'application/json', 'Idempotency-Key': 'key-12345' });

    const [, get] = fetchMock.mock.calls[1] as unknown as [string, RequestInit];
    expect(get.method).toBe('GET');
    expect(get.body).toBeUndefined();
    expect(get.headers).toEqual({ Accept: 'application/json' });
  });

  it('treats an empty success body as null for the parser', async () => {
    respond('', 200);
    const parse = vi.fn(() => 'parsed');

    await expect(apiRequest('/x', parse)).resolves.toBe('parsed');
    expect(parse).toHaveBeenCalledWith(null);
  });
});

describe('apiRequest timeouts and cancellation', () => {
  afterEach(() => vi.useRealTimers());

  // A fetch that only settles when its signal aborts, like a request to a server that never answers.
  // Like the real fetch, it rejects at once if the signal is already aborted.
  const hangingFetch = () =>
    vi.fn(
      (_url: string, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          const abort = () => reject(new DOMException('Aborted', 'AbortError'));
          if (init?.signal?.aborted) abort();
          else init?.signal?.addEventListener('abort', abort);
        }),
    );

  it('gives up after the timeout with a TIMEOUT error the UI can retry', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', hangingFetch());
    const pending = failureOf(apiRequest('/x', () => null, { timeoutMs: 5000 }));

    await vi.advanceTimersByTimeAsync(4999);
    let settled = false;
    void pending.then(() => (settled = true));
    await Promise.resolve();
    expect(settled).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    const error = await pending;
    expect(error.code).toBe('TIMEOUT');
    expect(error.isRetryable).toBe(true);
  });

  it('rethrows a caller abort as an AbortError, not as a failure to show the user', async () => {
    vi.stubGlobal('fetch', hangingFetch());
    const controller = new AbortController();
    const pending = apiRequest('/x', () => null, { signal: controller.signal });

    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('fails at once, without waiting for the timeout, when the signal is already aborted', async () => {
    const fetchMock = hangingFetch();
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    controller.abort();

    await expect(apiRequest('/x', () => null, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    // The signal handed to fetch is aborted too, so the browser never sends the request.
    expect((fetchMock.mock.calls[0]?.[1] as RequestInit | undefined)?.signal?.aborted).toBe(true);
  });
});
