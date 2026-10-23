import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AppProviders, Storefront } from '../App';
import {
  accountRoutes,
  authHeader,
  callsTo,
  issueTokens,
  jsonResponse,
  mockApi,
  pricingRules,
  problem,
  seedSession,
  unauthorized,
} from '../test/fixtures';
import { CART_STORAGE_KEY } from '../utils/cartStorage';

const seedCart = (unitPriceMinor = 2230, quantity = 1) =>
  window.localStorage.setItem(
    CART_STORAGE_KEY,
    JSON.stringify({
      version: 1,
      items: [{ productId: 2, title: 'Mens Casual Slim Fit T-Shirt', image: '', unitPriceMinor, quantity }],
    }),
  );

const confirmation = {
  orderReference: 'DEMO-20261022-7KQ2XM',
  createdAt: '2026-10-22T10:00:00Z',
  customerName: 'Ada Lovelace',
  customerEmail: 'ada@example.com',
  lines: [{ productId: 2, title: 'Mens Casual Slim Fit T-Shirt', unitPrice: 22.3, unitPriceMinor: 2230, quantity: 1, lineTotal: 22.3 }],
  totals: { subtotal: 22.3, tax: 1.78, shipping: 5.99, total: 30.07, currency: 'USD' },
  isSimulated: true,
  notice: 'This is a demo checkout. No payment was taken. Your order has been saved.',
};

async function openCheckout() {
  const user = userEvent.setup();
  render(
    <AppProviders pricingRules={pricingRules}>
      <Storefront />
    </AppProviders>,
  );
  await screen.findByText('Gold Dragon Bracelet');
  await user.click(screen.getByRole('button', { name: /open cart/i }));
  await user.click(screen.getByRole('button', { name: 'Check out' }));
  const dialog = screen.getByRole('dialog', { name: 'Check out' });
  return { user, dialog };
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>, dialog: HTMLElement) {
  await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
  await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
  await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));
}

const badge = () => screen.queryByTestId('cart-badge');

describe('checkout', () => {
  it('validates customer details before submitting', async () => {
    seedCart();
    const fetchMock = mockApi({ checkout: () => jsonResponse(confirmation) });
    const { user, dialog } = await openCheckout();

    expect(within(dialog).getByLabelText('Full name')).toHaveFocus();
    await user.type(within(dialog).getByLabelText('Email'), 'not-an-email');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(within(dialog).getByText('Enter your name.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true');
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/api/checkout'))).toBe(false);
  });

  it('submits product IDs, quantities and the expected total (never unit prices), then clears the cart', async () => {
    seedCart();
    let body: unknown;
    mockApi({
      checkout: async (_url, init) => {
        body = JSON.parse(String(init?.body));
        return jsonResponse(confirmation);
      },
    });
    const { user, dialog } = await openCheckout();

    await fillAndSubmit(user, dialog);

    expect(await screen.findByTestId('order-reference')).toHaveTextContent('DEMO-20261022-7KQ2XM');
    expect(screen.getByTestId('confirmation-total')).toHaveTextContent('$30.07');
    expect(body).toEqual({
      customer: { name: 'Ada Lovelace', email: 'ada@example.com' },
      items: [{ productId: 2, quantity: 1 }],
      expectedTotal: 30.07,
    });
    expect(badge()).not.toBeInTheDocument();
    expect(window.localStorage.getItem(CART_STORAGE_KEY)).toBeNull();
  });

  it('keeps the cart when the server is unreachable and retries with the same idempotency key', async () => {
    seedCart();
    let attempts = 0;
    const keys: (string | null)[] = [];
    mockApi({
      checkout: (_url, init) => {
        attempts += 1;
        keys.push(new Headers(init?.headers).get('Idempotency-Key'));
        if (attempts === 1) throw new TypeError('Failed to fetch');
        return jsonResponse(confirmation);
      },
    });
    const { user, dialog } = await openCheckout();

    await fillAndSubmit(user, dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/your cart is saved/i);
    expect(badge()).toHaveTextContent('1');
    expect(window.localStorage.getItem(CART_STORAGE_KEY)).not.toBeNull();

    await user.click(within(dialog).getByRole('button', { name: 'Try again' }));
    expect(await screen.findByTestId('order-reference')).toBeInTheDocument();
    expect(attempts).toBe(2);
    // The retry is the same order, so it must carry the same key: if the first request had in
    // fact reached the server, the API returns that order instead of creating a second one.
    expect(keys[0]).toMatch(/^[A-Za-z0-9_-]{8,100}$/);
    expect(keys[1]).toBe(keys[0]);
  });

  it('uses a new idempotency key when the order changes between attempts', async () => {
    seedCart(2230, 1);
    const keys: (string | null)[] = [];
    mockApi({
      checkout: (_url, init) => {
        keys.push(new Headers(init?.headers).get('Idempotency-Key'));
        throw new TypeError('Failed to fetch');
      },
    });
    const { user, dialog } = await openCheckout();

    await fillAndSubmit(user, dialog);
    await within(dialog).findByRole('alert');
    await user.clear(within(dialog).getByLabelText('Full name'));
    await user.type(within(dialog).getByLabelText('Full name'), 'Grace Hopper');
    await user.click(within(dialog).getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(keys).toHaveLength(2));

    expect(keys[1]).not.toBe(keys[0]);
  });

  it('re-prices the cart from the server quote when prices changed, and asks the user to confirm', async () => {
    seedCart(1999); // stale price saved in an old session
    mockApi({
      checkout: () =>
        jsonResponse(
          {
            status: 409,
            title: 'Prices have changed.',
            code: 'PRICE_CHANGED',
            quote: { lines: confirmation.lines, totals: confirmation.totals },
          },
          409,
        ),
    });
    const { user, dialog } = await openCheckout();

    await fillAndSubmit(user, dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Your new total is $30.07');
    await waitFor(() => expect(within(dialog).getByTestId('summary-total')).toHaveTextContent('$30.07'));
    expect(within(dialog).getByRole('button', { name: 'Confirm new total and place order' })).toBeInTheDocument();
    expect(badge()).toHaveTextContent('1');
  });

  it('offers to remove items the server reports as unavailable', async () => {
    seedCart();
    mockApi({
      checkout: () =>
        jsonResponse({ status: 422, title: 'Some products are no longer available.', code: 'PRODUCTS_UNAVAILABLE', unavailableProductIds: [2] }, 422),
    });
    const { user, dialog } = await openCheckout();

    await fillAndSubmit(user, dialog);
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Mens Casual Slim Fit T-Shirt');

    await user.click(within(dialog).getByRole('button', { name: 'Remove unavailable items' }));
    expect(badge()).not.toBeInTheDocument();
  });

  it('prevents a double submit from placing two orders', async () => {
    seedCart();
    let release: () => void = () => {};
    const fetchMock = mockApi({
      checkout: () => new Promise((resolve) => (release = () => resolve(jsonResponse(confirmation)))),
    });
    const { user, dialog } = await openCheckout();
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');

    const submit = within(dialog).getByRole('button', { name: 'Place demo order' });
    await user.click(submit);
    await user.keyboard('{Enter}');
    expect(within(dialog).getByRole('button', { name: /Placing order/ })).toBeDisabled();
    release();

    expect(await screen.findByTestId('order-reference')).toBeInTheDocument();
    expect(callsTo(fetchMock, '/api/checkout')).toHaveLength(1);
  });

  it('treats a lost response as an uncertain outcome and shows the order the retry recovered', async () => {
    seedCart();
    let attempts = 0;
    mockApi({
      checkout: () => {
        attempts += 1;
        if (attempts === 1) return problem(500, 'DATABASE_ERROR', { title: 'The order could not be saved.' });
        return new Response(JSON.stringify(confirmation), {
          status: 200,
          headers: { 'Content-Type': 'application/json', 'Idempotent-Replayed': 'true' },
        });
      },
    });
    const { user, dialog } = await openCheckout();
    await fillAndSubmit(user, dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("couldn't confirm whether your order went through");
    expect(badge()).toHaveTextContent('1');
    await user.click(within(dialog).getByRole('button', { name: 'Try again' }));

    expect(await screen.findByTestId('confirmation-replayed')).toHaveTextContent('not placed a second time');
    expect(screen.getByTestId('confirmation-where')).toHaveTextContent('guest');
  });

  it('tells the shopper how long to wait when checkout is rate limited, and keeps the cart', async () => {
    seedCart();
    mockApi({
      checkout: () =>
        new Response(JSON.stringify({ status: 429, title: 'Too Many Requests', code: 'TOO_MANY_REQUESTS' }), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '12' },
        }),
    });
    const { user, dialog } = await openCheckout();
    await fillAndSubmit(user, dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Wait 12 seconds and try again. Your cart is saved.');
    expect(within(dialog).getByRole('button', { name: 'Try again' })).toBeEnabled();
    expect(badge()).toHaveTextContent('1');
  });

  it('warns about a possible earlier order when the server reports the key was reused, then starts a new key', async () => {
    seedCart();
    const keys: (string | null)[] = [];
    mockApi({
      checkout: (_url, init) => {
        keys.push(new Headers(init?.headers).get('Idempotency-Key'));
        return keys.length === 1
          ? problem(422, 'IDEMPOTENCY_KEY_REUSED', { title: 'This Idempotency-Key was already used for a different order.' })
          : jsonResponse(confirmation);
      },
    });
    const { user, dialog } = await openCheckout();
    await fillAndSubmit(user, dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('may already have been placed');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));
    expect(await screen.findByTestId('order-reference')).toBeInTheDocument();
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('reports a request the server refuses as too large', async () => {
    seedCart();
    mockApi({ checkout: () => problem(413, 'REQUEST_TOO_LARGE', { title: 'Payload Too Large' }) });
    const { user, dialog } = await openCheckout();
    await fillAndSubmit(user, dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('too large');
    expect(badge()).toHaveTextContent('1');
  });
});

describe('checkout with an account', () => {
  it('sends the bearer token, prefills the account email and says the order is saved to the account', async () => {
    seedCart();
    seedSession('ada@example.com');
    const fetchMock = mockApi({ routes: accountRoutes(), checkout: () => jsonResponse(confirmation) });
    const { user, dialog } = await openCheckout();

    await waitFor(() => expect(within(dialog).getByLabelText('Email')).toHaveValue('ada@example.com'));
    expect(within(dialog).getByTestId('checkout-account')).toHaveTextContent('Signed in as ada@example.com');
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(await screen.findByTestId('confirmation-where')).toHaveTextContent('saved to your account');
    expect(authHeader(callsTo(fetchMock, '/api/checkout')[0]?.[1])).toMatch(/^Bearer access-\d+$/);
  });

  it('checks out as a guest with no Authorization header', async () => {
    seedCart();
    const fetchMock = mockApi({ checkout: () => jsonResponse(confirmation) });
    const { user, dialog } = await openCheckout();
    expect(within(dialog).getByTestId('checkout-account')).toHaveTextContent('Checking out as a guest');
    await fillAndSubmit(user, dialog);

    await screen.findByTestId('order-reference');
    expect(authHeader(callsTo(fetchMock, '/api/checkout')[0]?.[1])).toBeNull();
  });

  it('renews an expired token and resends the same order with the same key', async () => {
    seedCart();
    seedSession();
    const sent: { auth: string | null; key: string | null }[] = [];
    const fetchMock = mockApi({
      routes: accountRoutes(),
      checkout: (_url, init) => {
        const headers = new Headers(init?.headers);
        sent.push({ auth: headers.get('Authorization'), key: headers.get('Idempotency-Key') });
        return sent.length === 1 ? unauthorized() : jsonResponse(confirmation);
      },
    });
    const { user, dialog } = await openCheckout();
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await waitFor(() => expect(within(dialog).getByLabelText('Email')).toHaveValue('ada@example.com'));
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(await screen.findByTestId('confirmation-where')).toHaveTextContent('saved to your account');
    expect(sent).toHaveLength(2);
    expect(sent[1]?.key).toBe(sent[0]?.key);
    expect(sent[1]?.auth).not.toBe(sent[0]?.auth);
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(2); // restore + renewal
  });

  it('does not silently turn an expired session into a guest order', async () => {
    seedCart();
    seedSession();
    let refreshes = 0;
    const sent: (string | null)[] = [];
    mockApi({
      routes: {
        ...accountRoutes(),
        '/api/account/refresh': () => (++refreshes === 1 ? jsonResponse(issueTokens()) : unauthorized()),
      },
      checkout: (_url, init) => {
        sent.push(authHeader(init));
        return sent.length === 1 ? unauthorized() : jsonResponse(confirmation);
      },
    });
    const { user, dialog } = await openCheckout();
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await waitFor(() => expect(within(dialog).getByLabelText('Email')).toHaveValue('ada@example.com'));
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("Your session expired, so this order hasn't been placed");
    expect(sent).toHaveLength(1);
    expect(badge()).toHaveTextContent('1');
    expect(within(dialog).getByLabelText('Full name')).toHaveValue('Ada Lovelace');

    // Only an explicit choice places it as a guest.
    await user.click(within(dialog).getByRole('button', { name: 'Place order as a guest' }));
    expect(await screen.findByTestId('confirmation-where')).toHaveTextContent('guest');
    expect(sent[1]).toBeNull();
  });

  it('lets a guest sign in from checkout and come back with their details kept', async () => {
    seedCart();
    mockApi({ routes: accountRoutes('ada@example.com'), checkout: () => jsonResponse(confirmation) });
    const { user, dialog } = await openCheckout();
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');

    await user.click(within(dialog).getByRole('button', { name: 'Sign in' }));
    const signIn = screen.getByRole('dialog', { name: 'Sign in' });
    await user.type(within(signIn).getByLabelText('Email'), 'ada@example.com');
    await user.type(within(signIn).getByLabelText('Password'), 'Str0ng!Passw0rd');
    await user.click(within(signIn).getByRole('button', { name: 'Sign in' }));

    const back = await screen.findByRole('dialog', { name: 'Check out' });
    await waitFor(() => expect(within(back).getByTestId('checkout-account')).toHaveTextContent('Signed in as ada@example.com'));
    expect(within(back).getByLabelText('Full name')).toHaveValue('Ada Lovelace');
    expect(within(back).getByLabelText('Email')).toHaveValue('ada@example.com');
  });
});
