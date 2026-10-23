import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders, Storefront } from '../App';
import { callsTo, jsonResponse, mockApi, pricingRules, products, settle } from '../test/fixtures';
import { CART_STORAGE_KEY } from '../utils/cartStorage';

afterEach(() => vi.restoreAllMocks());

const renderApp = () => {
  const user = userEvent.setup();
  render(
    <AppProviders pricingRules={pricingRules}>
      <Storefront />
    </AppProviders>,
  );
  return { user };
};

const setOnline = (online: boolean) => {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(online);
  act(() => {
    window.dispatchEvent(new Event(online ? 'online' : 'offline'));
  });
};

const seedCart = () =>
  window.localStorage.setItem(
    CART_STORAGE_KEY,
    JSON.stringify({
      version: 1,
      items: [{ productId: 2, title: 'Mens Casual Slim Fit T-Shirt', image: '', unitPriceMinor: 2230, quantity: 1 }],
    }),
  );

const productsWithHeaders = (headers: Record<string, string>) => () =>
  new Response(JSON.stringify({ items: products, count: products.length }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ...headers },
  });

const outage = () =>
  new Response(
    JSON.stringify({
      status: 502,
      title: 'The product service is unavailable.',
      detail: 'Please try again in a moment.',
      code: 'UPSTREAM_UNAVAILABLE',
      traceId: 'corr-502',
    }),
    { status: 502, headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': 'corr-502' } },
  );

describe('offline banner', () => {
  it('appears while the browser is offline and goes away when it reconnects', async () => {
    mockApi();
    renderApp();
    await screen.findByText('Gold Dragon Bracelet');
    expect(screen.queryByText(/You're offline/)).not.toBeInTheDocument();

    setOnline(false);
    const banner = screen.getByText(/You're offline/).closest('[role="status"]');
    expect(banner).toHaveTextContent('Your cart is saved on this device');

    setOnline(true);
    expect(screen.queryByText(/You're offline/)).not.toBeInTheDocument();
  });
});

describe('catalogue outage', () => {
  it('explains the outage in shopper terms and shows a reference to quote', async () => {
    mockApi({ products: outage });
    renderApp();

    const message = (await screen.findByRole('heading', { name: "We couldn't load the catalogue" })).closest('[role="alert"]');
    expect(message).toHaveTextContent('The product service is unavailable. Please try again in a moment.');
    expect(message).toHaveTextContent("Our product supplier isn't responding right now. Your cart is saved");
    expect(message).toHaveTextContent('Reference: corr-502');
    expect(message).not.toHaveTextContent(/API is running|configured URL/);
  });

  it('reloads the catalogue once when the connection comes back, and not again', async () => {
    let fail = true;
    const fetchMock = mockApi({ products: () => (fail ? outage() : jsonResponse({ items: products, count: products.length })) });
    renderApp();
    await screen.findByRole('heading', { name: "We couldn't load the catalogue" });
    expect(callsTo(fetchMock, '/api/products')).toHaveLength(1);

    setOnline(false);
    fail = false;
    setOnline(true);

    await screen.findByText('Gold Dragon Bracelet');
    expect(callsTo(fetchMock, '/api/products')).toHaveLength(2);

    // A later reconnect after a good load doesn't refetch, and nothing loops.
    setOnline(false);
    setOnline(true);
    await settle();
    expect(callsTo(fetchMock, '/api/products')).toHaveLength(2);
  });

  it('does not retry by itself while the outage continues', async () => {
    const fetchMock = mockApi({ products: outage });
    renderApp();
    await screen.findByRole('heading', { name: "We couldn't load the catalogue" });

    await settle(30);

    expect(callsTo(fetchMock, '/api/products')).toHaveLength(1);
  });

  it('a reconnect while still failing makes exactly one attempt', async () => {
    const fetchMock = mockApi({ products: outage });
    renderApp();
    await screen.findByRole('heading', { name: "We couldn't load the catalogue" });

    setOnline(false);
    setOnline(true);
    await settle(30);

    expect(callsTo(fetchMock, '/api/products')).toHaveLength(2);
  });
});

describe('stale catalogue', () => {
  it('shows the saved copy with a notice, and Refresh clears it once the supplier is back', async () => {
    let stale = true;
    const fetchMock = mockApi({
      products: () => productsWithHeaders(stale ? { 'Catalogue-Stale': 'true' } : {})(),
    });
    const { user } = renderApp();

    await screen.findByText('Gold Dragon Bracelet');
    const notice = screen.getByText(/recently saved copy/).closest('[role="status"]') as HTMLElement;
    expect(notice).toHaveTextContent('Prices and availability are checked again when you place an order.');

    stale = false;
    await user.click(within(notice).getByRole('button', { name: 'Refresh' }));

    await waitFor(() => expect(screen.queryByText(/recently saved copy/)).not.toBeInTheDocument());
    expect(screen.getByText('Gold Dragon Bracelet')).toBeInTheDocument();
    expect(callsTo(fetchMock, '/api/products')).toHaveLength(2);
  });

  it('shows no notice for a fresh catalogue', async () => {
    mockApi({ products: productsWithHeaders({}) });
    renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    expect(screen.queryByText(/recently saved copy/)).not.toBeInTheDocument();
  });
});

async function openCheckoutAndFill(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Gold Dragon Bracelet');
  await user.click(screen.getByRole('button', { name: /open cart/i }));
  await user.click(screen.getByRole('button', { name: 'Check out' }));
  const dialog = screen.getByRole('dialog', { name: 'Check out' });
  await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
  await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
  return dialog;
}

describe('checkout availability', () => {
  it('does not send an order while offline, says so, and keeps the cart', async () => {
    seedCart();
    const checkout = vi.fn(() => jsonResponse({}, 500));
    const fetchMock = mockApi({ checkout });
    const { user } = renderApp();
    const dialog = await openCheckoutAndFill(user);

    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("You're offline, so your order hasn't been sent.");
    expect(checkout).not.toHaveBeenCalled();
    expect(callsTo(fetchMock, '/api/checkout')).toHaveLength(0);
    expect(window.localStorage.getItem(CART_STORAGE_KEY)).toContain('"productId":2');
  });

  it('treats an unavailable database as a definite failure and shows the reference', async () => {
    seedCart();
    const fetchMock = mockApi({
      checkout: () =>
        new Response(
          JSON.stringify({
            status: 503,
            title: "The store's database is unavailable.",
            detail: 'Please try again in a moment.',
            code: 'DATABASE_UNAVAILABLE',
            traceId: 'corr-db',
          }),
          { status: 503, headers: { 'Content-Type': 'application/json', 'Retry-After': '5', 'X-Correlation-Id': 'corr-db' } },
        ),
    });
    const { user } = renderApp();
    const dialog = await openCheckoutAndFill(user);

    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent("The store's database is unavailable. Please try again in a moment. Your cart is saved.");
    expect(alert).not.toHaveTextContent("couldn't confirm whether your order went through");
    expect(alert).toHaveTextContent('Reference: corr-db');
    expect(callsTo(fetchMock, '/api/checkout')).toHaveLength(1); // no automatic retry
  });

  it('shows the reference with an uncertain outcome, and retrying reuses the same idempotency key', async () => {
    seedCart();
    let calls = 0;
    const fetchMock = mockApi({
      checkout: () => {
        calls += 1;
        return new Response(JSON.stringify({ status: 500, title: 'An unexpected error occurred.', traceId: 'corr-500' }), {
          status: 500,
          headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': 'corr-500' },
        });
      },
    });
    const { user } = renderApp();
    const dialog = await openCheckoutAndFill(user);

    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));
    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent("We couldn't confirm whether your order went through.");
    expect(alert).toHaveTextContent('Reference: corr-500');
    await settle();
    expect(calls).toBe(1);

    await user.click(within(dialog).getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(calls).toBe(2));
    const keys = callsTo(fetchMock, '/api/checkout').map(([, init]) => new Headers(init?.headers).get('Idempotency-Key'));
    expect(keys[0]).toBeTruthy();
    expect(keys[1]).toBe(keys[0]);
  });
});
