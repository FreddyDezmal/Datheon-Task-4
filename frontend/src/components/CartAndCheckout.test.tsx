import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AppProviders, Storefront } from '../App';
import { bodyOf, callsTo, jsonResponse, mockApi, pricingRules, problem } from '../test/fixtures';
import { CART_STORAGE_KEY } from '../utils/cartStorage';

const seedCart = (items: { productId: number; title: string; unitPriceMinor: number; quantity: number }[]) =>
  window.localStorage.setItem(CART_STORAGE_KEY, JSON.stringify({ version: 1, items: items.map((i) => ({ image: '', ...i })) }));

const shirt = { productId: 2, title: 'Mens Casual Slim Fit T-Shirt', unitPriceMinor: 2230, quantity: 1 };

function renderApp() {
  const user = userEvent.setup();
  render(
    <AppProviders pricingRules={pricingRules}>
      <Storefront />
    </AppProviders>,
  );
  return { user };
}

async function openCart(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Gold Dragon Bracelet');
  await user.click(screen.getByRole('button', { name: /open cart/i }));
  return screen.getByRole('dialog', { name: /Your cart/ });
}

const confirmation = {
  orderReference: 'DEMO-20261022-7KQ2XM',
  createdAt: '2026-10-22T10:00:00Z',
  customerName: 'Ada Lovelace',
  customerEmail: 'ada@example.com',
  lines: [{ productId: 2, title: 'Mens Casual Slim Fit T-Shirt', unitPrice: 22.3, unitPriceMinor: 2230, quantity: 1, lineTotal: 22.3 }],
  totals: { subtotal: 22.3, tax: 1.78, shipping: 5.99, total: 30.07, currency: 'USD' },
  isSimulated: true,
  notice: 'Demo.',
};

describe('cart drawer', () => {
  it('will not go below 1 and points to Remove instead', async () => {
    seedCart([shirt]);
    mockApi();
    const { user } = renderApp();
    const cart = await openCart(user);

    const decrease = within(cart).getByRole('button', { name: `Decrease quantity of ${shirt.title}` });
    expect(decrease).toHaveAttribute('aria-disabled', 'true');
    await user.click(decrease);

    expect(within(cart).getByText(/can’t go below 1. Use Remove/)).toBeInTheDocument();
    expect(within(cart).getByRole('group', { name: `Quantity of ${shirt.title}` })).toHaveTextContent('1');
    expect(screen.getByTestId('cart-badge')).toHaveTextContent('1');
  });

  it('stops at the maximum quantity and says why', async () => {
    seedCart([{ ...shirt, quantity: 99 }]);
    mockApi();
    const { user } = renderApp();
    const cart = await openCart(user);

    await user.click(within(cart).getByRole('button', { name: `Increase quantity of ${shirt.title}` }));

    expect(within(cart).getByText('You can order up to 99 of this item.')).toBeInTheDocument();
    expect(screen.getByTestId('cart-badge')).toHaveTextContent('99');
  });

  it('shows free shipping progress and drops shipping at the threshold', async () => {
    seedCart([shirt]);
    mockApi();
    const { user } = renderApp();
    const cart = await openCart(user);

    expect(within(cart).getByRole('progressbar', { name: 'Progress towards free shipping' })).toHaveAttribute('aria-valuenow', '45');
    expect(within(cart).getByText('$27.70')).toBeInTheDocument(); // $50.00 - $22.30
    expect(within(cart).getByTestId('summary-shipping')).toHaveTextContent('$5.99');

    await user.click(within(cart).getByRole('button', { name: `Increase quantity of ${shirt.title}` }));
    await user.click(within(cart).getByRole('button', { name: `Increase quantity of ${shirt.title}` }));

    expect(within(cart).getByTestId('summary-subtotal')).toHaveTextContent('$66.90');
    expect(within(cart).getByTestId('summary-shipping')).toHaveTextContent('Free');
    expect(within(cart).getByText('Your order ships free.')).toBeInTheDocument();
  });

  it('shows an empty state whose button returns focus to the cart button', async () => {
    mockApi();
    const { user } = renderApp();
    const cart = await openCart(user);

    expect(within(cart).getByText('Your cart is empty')).toBeInTheDocument();
    expect(within(cart).queryByRole('button', { name: 'Check out' })).not.toBeInTheDocument();
    await user.click(within(cart).getByRole('button', { name: 'Browse products' }));

    expect(screen.queryByRole('dialog', { name: /Your cart/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /open cart/i })).toHaveFocus();
  });
});

describe('product cards', () => {
  it('shows how many are in the cart and refuses to add past the limit', async () => {
    seedCart([{ productId: 5, title: 'Gold Dragon Bracelet', unitPriceMinor: 69500, quantity: 99 }]);
    mockApi();
    const { user } = renderApp();
    // The closed cart drawer also contains the title, so find the product card by role.
    const card = await screen.findByRole('article', { name: 'Gold Dragon Bracelet' });
    expect(within(card).getByText('99 in your cart')).toBeInTheDocument();
    await user.click(within(card).getByRole('button', { name: 'Add Gold Dragon Bracelet to cart' }));

    expect(await screen.findByText(/already have the maximum quantity of Gold Dragon Bracelet/)).toBeInTheDocument();
    expect(screen.getByTestId('cart-badge')).toHaveTextContent('99');
  });

  it('falls back to the category name when an image fails to load', async () => {
    mockApi();
    renderApp();
    await screen.findByText('Gold Dragon Bracelet');
    const card = screen.getByRole('article', { name: 'Gold Dragon Bracelet' });

    // Product images are decorative (the title names the product), so they are found by tag, not role.
    const img = card.querySelector('img');
    expect(img).not.toBeNull();
    fireEvent.error(img as HTMLImageElement);

    expect(card.querySelector('img')).toBeNull();
    expect(within(card).getAllByText('Jewelry').length).toBeGreaterThan(1);
  });
});

describe('checkout form', () => {
  async function openCheckout(user: ReturnType<typeof userEvent.setup>) {
    const cart = await openCart(user);
    await user.click(within(cart).getByRole('button', { name: 'Check out' }));
    return screen.getByRole('dialog', { name: 'Check out' });
  }

  it('puts server validation errors on the matching field', async () => {
    seedCart([shirt]);
    mockApi({
      checkout: () =>
        problem(400, 'VALIDATION_FAILED', {
          title: 'One or more validation errors occurred.',
          errors: { 'customer.email': ['That email domain cannot receive mail.'] },
        }),
    });
    const { user } = renderApp();
    const dialog = await openCheckout(user);
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    const email = await within(dialog).findByLabelText('Email');
    await waitFor(() => expect(email).toHaveAttribute('aria-invalid', 'true'));
    expect(email).toHaveAccessibleDescription('That email domain cannot receive mail.');
    expect(email).toHaveValue('ada@example.com');
  });

  it('after a price change, confirming sends the new server total, not the old one', async () => {
    seedCart([{ ...shirt, unitPriceMinor: 1999 }]);
    const fetchMock = mockApi({
      checkout: (_url, init) =>
        bodyOf(init).expectedTotal === 30.07
          ? jsonResponse(confirmation)
          : jsonResponse(
              { status: 409, title: 'Prices have changed.', code: 'PRICE_CHANGED', quote: { lines: confirmation.lines, totals: confirmation.totals } },
              409,
            ),
    });
    const { user } = renderApp();
    const dialog = await openCheckout(user);
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    await user.click(await within(dialog).findByRole('button', { name: 'Confirm new total and place order' }));

    expect(await screen.findByTestId('order-reference')).toBeInTheDocument();
    const totals = callsTo(fetchMock, '/api/checkout').map(([, init]) => bodyOf(init).expectedTotal);
    expect(totals).toEqual([27.58, 30.07]);
  });

  it('cannot be closed or edited while the order is being placed', async () => {
    seedCart([shirt]);
    let release: (r: Response) => void = () => {};
    mockApi({ checkout: () => new Promise<Response>((resolve) => (release = resolve)) });
    const { user } = renderApp();
    const dialog = await openCheckout(user);
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(within(dialog).getByRole('button', { name: 'Close checkout' })).toBeDisabled();
    expect(within(dialog).getByLabelText('Full name')).toBeDisabled();
    expect(dialog).toHaveAttribute('aria-busy', 'true');
    await user.keyboard('{Escape}');
    expect(screen.getByRole('dialog', { name: 'Check out' })).toBeInTheDocument();

    release(jsonResponse(confirmation));
    expect(await screen.findByRole('dialog', { name: 'Order placed' })).toBeInTheDocument();
  });

  it('keeps the cart and typed details when checkout is closed and reopened', async () => {
    seedCart([shirt]);
    mockApi();
    const { user } = renderApp();
    let dialog = await openCheckout(user);
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.click(within(dialog).getByRole('button', { name: 'Back to cart' }));

    dialog = await openCheckout(user);
    expect(within(dialog).getByLabelText('Full name')).toHaveValue('Ada Lovelace');
    expect(screen.getByTestId('cart-badge')).toHaveTextContent('1');
  });

  it('names nothing it cannot identify when the server reports unavailable items without ids', async () => {
    seedCart([shirt]);
    mockApi({ checkout: () => problem(422, 'PRODUCTS_UNAVAILABLE', { title: 'Some products are no longer available.' }) });
    const { user } = renderApp();
    const dialog = await openCheckout(user);
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Some items in your cart are no longer available.');
  });

  it('maps a server name error to the name field and shows errors that belong to no field', async () => {
    seedCart([shirt]);
    mockApi({
      checkout: () =>
        problem(400, 'VALIDATION_FAILED', {
          errors: { 'customer.name': ['Name contains unsupported characters.'], items: ['Too many lines.'] },
        }),
    });
    const { user } = renderApp();
    const dialog = await openCheckout(user);
    await user.type(within(dialog).getByLabelText('Full name'), 'Ada Lovelace');
    await user.type(within(dialog).getByLabelText('Email'), 'ada@example.com');
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));

    await waitFor(() => expect(within(dialog).getByLabelText('Full name')).toHaveAccessibleDescription('Name contains unsupported characters.'));
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Too many lines.');
  });

  it('clears a field error as soon as the field becomes valid', async () => {
    seedCart([shirt]);
    mockApi();
    const { user } = renderApp();
    const dialog = await openCheckout(user);
    await user.click(within(dialog).getByRole('button', { name: 'Place demo order' }));
    expect(within(dialog).getByLabelText('Full name')).toHaveAttribute('aria-invalid', 'true');

    await user.type(within(dialog).getByLabelText('Full name'), 'Ada');

    expect(within(dialog).getByLabelText('Full name')).toHaveAttribute('aria-invalid', 'false');
    expect(within(dialog).getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true');
  });
});
