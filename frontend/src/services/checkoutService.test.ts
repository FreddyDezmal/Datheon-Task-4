import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CheckoutRequest } from '../types/checkout';
import { createIdempotencyKey, submitCheckout } from './checkoutService';

// The API's rule for keys (IdempotencyKeys in the backend): 8–100 of A–Z, a–z, 0–9, _ and -.
const API_KEY_PATTERN = /^[A-Za-z0-9_-]{8,100}$/;

const request: CheckoutRequest = {
  customer: { name: 'Ada Lovelace', email: 'ada@example.com' },
  items: [{ productId: 2, quantity: 1 }],
  expectedTotal: 30.07,
};

const confirmation = {
  orderReference: 'DEMO-20261022-AAAAAA',
  createdAt: '2026-10-22T10:00:00Z',
  customerName: 'Ada Lovelace',
  customerEmail: 'ada@example.com',
  lines: [{ productId: 2, title: 'Shirt', unitPrice: 22.3, unitPriceMinor: 2230, quantity: 1, lineTotal: 22.3 }],
  totals: { subtotal: 22.3, tax: 1.78, shipping: 5.99, total: 30.07, currency: 'USD' },
  isSimulated: true,
  notice: 'Demo.',
};

const stubResponse = (headers: Record<string, string> = {}) => {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify(confirmation), { status: 200, headers }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
};
const sentHeaders = (fetchMock: ReturnType<typeof stubResponse>) =>
  new Headers((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].headers);

describe('createIdempotencyKey', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('creates keys the API accepts, different every time', () => {
    const keys = new Set(Array.from({ length: 200 }, createIdempotencyKey));

    expect(keys.size).toBe(200);
    for (const key of keys) expect(key).toMatch(API_KEY_PATTERN);
  });

  it('falls back to random bytes when crypto.randomUUID is unavailable (older browsers, plain http hosts)', () => {
    const realCrypto = globalThis.crypto;
    vi.stubGlobal('crypto', { getRandomValues: (bytes: Uint8Array<ArrayBuffer>) => realCrypto.getRandomValues(bytes) });

    const key = createIdempotencyKey();
    expect(key).toMatch(/^[0-9a-f]{32}$/);
    expect(key).toMatch(API_KEY_PATTERN);
  });
});

describe('submitCheckout', () => {
  it('sends the key and the bearer token as headers, never in the body', async () => {
    const fetchMock = stubResponse();
    await submitCheckout(request, { idempotencyKey: 'key-123456', accessToken: 'tok' });

    const headers = sentHeaders(fetchMock);
    expect(headers.get('Idempotency-Key')).toBe('key-123456');
    expect(headers.get('Authorization')).toBe('Bearer tok');
    const body = JSON.parse(String((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].body));
    expect(body).toEqual(request);
  });

  it('sends neither header for a guest checkout without a key', async () => {
    const fetchMock = stubResponse();
    await submitCheckout(request);

    const headers = sentHeaders(fetchMock);
    expect(headers.get('Idempotency-Key')).toBeNull();
    expect(headers.get('Authorization')).toBeNull();
  });

  it.each([
    [{ 'Idempotent-Replayed': 'true' }, true],
    [{}, false],
    [{ 'Idempotent-Replayed': 'false' }, false],
  ])('reports a replay only when the server says so (%o)', async (headers, replayed) => {
    stubResponse(headers);
    const result = await submitCheckout(request, { idempotencyKey: 'key-123456' });

    expect(result.replayed).toBe(replayed);
    expect(result.confirmation.orderReference).toBe('DEMO-20261022-AAAAAA');
  });
});
