import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AppProviders, Storefront } from '../App';
import { jsonResponse, mockApi, pricingRules } from '../test/fixtures';
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
});
