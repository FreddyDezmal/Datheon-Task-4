import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AppProviders, Storefront } from '../App';
import { callsTo, jsonResponse, mockApi, pricingRules, products } from '../test/fixtures';

function renderApp() {
  const user = userEvent.setup();
  render(
    <AppProviders pricingRules={pricingRules}>
      <Storefront />
    </AppProviders>,
  );
  return { user };
}

describe('product details', () => {
  it('opens from the product title without another request, and puts the product in the URL', async () => {
    const fetchMock = mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    const link = screen.getByRole('link', { name: 'Gold Dragon Bracelet' });
    expect(link).toHaveAttribute('href', '?product=5');
    await user.click(link);

    const dialog = screen.getByRole('dialog', { name: 'Gold Dragon Bracelet' });
    expect(within(dialog).getByText('Gold Dragon Bracelet description')).toBeInTheDocument();
    expect(within(dialog).getByText('$695.00')).toBeInTheDocument();
    expect(within(dialog).getByRole('img', { name: 'Gold Dragon Bracelet' })).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Close product details' })).toHaveFocus();
    expect(window.location.search).toBe('?product=5');
    expect(callsTo(fetchMock, '/api/products/5')).toHaveLength(0);

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Gold Dragon Bracelet' })).not.toBeInTheDocument();
    expect(window.location.search).toBe('');
    expect(link).toHaveFocus();
  });

  it('keeps search filters in the details link so a shared link restores both', async () => {
    window.history.replaceState(null, '', '/?category=jewelry');
    mockApi();
    renderApp();

    expect(await screen.findByRole('link', { name: 'Gold Dragon Bracelet' })).toHaveAttribute('href', '?category=jewelry&product=5');
  });

  it('loads a product from a shared link', async () => {
    window.history.replaceState(null, '', '/?product=9');
    const fetchMock = mockApi();
    renderApp();

    expect(await screen.findByRole('dialog', { name: 'WD 2TB Portable Hard Drive' })).toBeInTheDocument();
    expect(callsTo(fetchMock, '/api/products/9')).toHaveLength(1);
  });

  it('says so when a linked product no longer exists', async () => {
    window.history.replaceState(null, '', '/?product=999');
    mockApi();
    renderApp();

    expect(await screen.findByRole('dialog', { name: 'Product unavailable' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('no longer in the catalogue');
  });

  it('offers a retry when the product cannot be loaded', async () => {
    window.history.replaceState(null, '', '/?product=9');
    let fail = true;
    mockApi({
      routes: {
        '/api/products/9': () =>
          fail ? jsonResponse({ status: 502, title: 'The product catalogue is unavailable.', code: 'UPSTREAM_UNAVAILABLE' }, 502) : jsonResponse(products[2]),
      },
    });
    const { user } = renderApp();

    expect(await screen.findByRole('alert')).toHaveTextContent('The product catalogue is unavailable.');
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('dialog', { name: 'WD 2TB Portable Hard Drive' })).toBeInTheDocument();
  });

  it('ignores an invalid product parameter', async () => {
    window.history.replaceState(null, '', '/?product=abc');
    const fetchMock = mockApi();
    renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/api/products/abc'))).toBe(false);
  });

  it('adds to the cart from the details view and links to the cart', async () => {
    mockApi();
    const { user } = renderApp();
    await user.click(await screen.findByRole('link', { name: 'Gold Dragon Bracelet' }));
    const dialog = screen.getByRole('dialog', { name: 'Gold Dragon Bracelet' });

    await user.click(within(dialog).getByRole('button', { name: 'Add to cart' }));
    expect(screen.getByTestId('cart-badge')).toHaveTextContent('1');
    await user.click(within(dialog).getByRole('button', { name: /View cart \(1 in cart\)/ }));
    expect(screen.getByRole('dialog', { name: /Your cart/ })).toBeInTheDocument();
  });
});
