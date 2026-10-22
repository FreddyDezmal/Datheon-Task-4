import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AppProviders, Storefront } from './App';
import { jsonResponse, mockApi, pricingRules, requestedUrls } from './test/fixtures';
import { CART_STORAGE_KEY } from './utils/cartStorage';

function renderApp() {
  const user = userEvent.setup();
  render(
    <AppProviders pricingRules={pricingRules}>
      <Storefront />
    </AppProviders>,
  );
  return { user };
}

const productCards = () => screen.getAllByRole('article');
const badge = () => screen.queryByTestId('cart-badge');
const productUrls = (fetchMock: ReturnType<typeof mockApi>) =>
  requestedUrls(fetchMock).filter((u) => u.pathname === '/api/products');

describe('catalogue', () => {
  it('loads and renders products from the API', async () => {
    mockApi();
    renderApp();

    expect(await screen.findByText('Gold Dragon Bracelet')).toBeInTheDocument();
    expect(productCards()).toHaveLength(4);
    expect(screen.getByText('$695.00')).toBeInTheDocument();
    expect(screen.getByText('Top Rated')).toBeInTheDocument();
    expect(screen.getByText('4 products')).toBeInTheDocument();
  });

  it('sends a debounced search to the API and shows the matches', async () => {
    const fetchMock = mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    await user.type(screen.getByRole('searchbox', { name: 'Search products' }), 'drive');

    await waitFor(() => expect(productCards()).toHaveLength(1));
    expect(screen.getByText('WD 2TB Portable Hard Drive')).toBeInTheDocument();
    // One request for the initial load, one for the settled search term, none per keystroke.
    expect(productUrls(fetchMock).map((u) => u.searchParams.get('search'))).toEqual([null, 'drive']);
    expect(window.location.search).toBe('?q=drive');
  });

  it('filters by the Clothing group and combines it with sorting', async () => {
    const fetchMock = mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    await user.click(screen.getByRole('button', { name: 'Clothing' }));
    await waitFor(() => expect(productCards()).toHaveLength(2));
    expect(screen.getByRole('button', { name: 'Clothing' })).toHaveAttribute('aria-pressed', 'true');

    await user.selectOptions(screen.getByLabelText('Sort by'), 'price-asc');
    await waitFor(() => expect(productUrls(fetchMock).at(-1)?.searchParams.get('sort')).toBe('price-asc'));
    expect(productUrls(fetchMock).at(-1)?.searchParams.get('category')).toBe('clothing');
  });

  it('shows an empty state with a way back when nothing matches', async () => {
    mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    await user.type(screen.getByRole('searchbox'), 'zzz');
    expect(await screen.findByRole('heading', { name: 'No products match' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Clear search and filters' }));
    await waitFor(() => expect(productCards()).toHaveLength(4));
  });

  it('shows a recoverable error when the API fails, then retries', async () => {
    let fail = true;
    mockApi({
      products: () =>
        fail
          ? jsonResponse({ title: 'The product service is unavailable.', status: 502, code: 'UPSTREAM_UNAVAILABLE', detail: 'Please try again in a moment.' }, 502)
          : jsonResponse({ items: [], count: 0 }),
    });
    const { user } = renderApp();

    expect(await screen.findByRole('heading', { name: "We couldn't load the catalogue" })).toBeInTheDocument();
    expect(screen.getByText('The product service is unavailable. Please try again in a moment.')).toBeInTheDocument();

    fail = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'No products match' })).toBeInTheDocument();
  });
});

describe('cart', () => {
  it('adds products, counts units in the badge and persists to localStorage', async () => {
    mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    const addShirt = screen.getByRole('button', { name: 'Add Mens Casual Slim Fit T-Shirt to cart' });
    await user.click(addShirt);
    await user.click(addShirt);
    await user.click(screen.getByRole('button', { name: "Add Women's Moisture Tee to cart" }));

    expect(badge()).toHaveTextContent('3');
    expect(screen.getByRole('button', { name: 'Open cart, 3 items' })).toBeInTheDocument();

    const stored = JSON.parse(window.localStorage.getItem(CART_STORAGE_KEY) ?? '{}');
    expect(stored.items).toEqual([
      expect.objectContaining({ productId: 2, quantity: 2, unitPriceMinor: 2230 }),
      expect.objectContaining({ productId: 19, quantity: 1, unitPriceMinor: 795 }),
    ]);
  });

  it('opens the drawer, updates quantities and totals, removes items, and closes on Escape', async () => {
    mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');
    await user.click(screen.getByRole('button', { name: 'Add Mens Casual Slim Fit T-Shirt to cart' }));

    const cartButton = screen.getByRole('button', { name: 'Open cart, 1 item' });
    await user.click(cartButton);
    const drawer = screen.getByRole('dialog', { name: /your cart/i });
    expect(within(drawer).getByRole('button', { name: 'Close cart' })).toHaveFocus();

    // $22.30 + $1.78 tax + $5.99 shipping
    expect(within(drawer).getByTestId('summary-total')).toHaveTextContent('$30.07');

    // Decrement at 1 is prevented with feedback.
    await user.click(within(drawer).getByRole('button', { name: /decrease quantity/i }));
    expect(within(drawer).getByText(/use remove to delete this item/i)).toBeInTheDocument();
    expect(badge()).toHaveTextContent('1');

    const increase = within(drawer).getByRole('button', { name: /increase quantity/i });
    await user.click(increase);
    await user.click(increase);
    // 3 × $22.30 = $66.90 → free shipping, tax $5.35
    expect(within(drawer).getByTestId('summary-shipping')).toHaveTextContent('Free');
    expect(within(drawer).getByTestId('summary-total')).toHaveTextContent('$72.25');
    expect(badge()).toHaveTextContent('3');

    await user.click(within(drawer).getByRole('button', { name: /remove mens casual/i }));
    expect(within(drawer).getByText('Your cart is empty')).toBeInTheDocument();
    expect(badge()).not.toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.getByTestId('cart-drawer')).not.toHaveClass('is-open');
    expect(cartButton).toHaveFocus();
  });

  it('closes the drawer when the backdrop is clicked', async () => {
    mockApi();
    const { user } = renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    await user.click(screen.getByRole('button', { name: /open cart/i }));
    const drawer = screen.getByTestId('cart-drawer');
    expect(drawer).toHaveClass('is-open');

    await user.click(drawer.querySelector('.drawer__backdrop') as HTMLElement);
    expect(drawer).not.toHaveClass('is-open');
  });

  it('restores the cart from localStorage on start', async () => {
    window.localStorage.setItem(
      CART_STORAGE_KEY,
      JSON.stringify({ version: 1, items: [{ productId: 9, title: 'WD 2TB Portable Hard Drive', image: '', unitPriceMinor: 6400, quantity: 2 }] }),
    );
    mockApi();
    renderApp();

    expect(badge()).toHaveTextContent('2');
  });

  it('starts with an empty cart and a notice when stored data is invalid', async () => {
    window.localStorage.setItem(CART_STORAGE_KEY, '{"version":1,"items":');
    mockApi();
    renderApp();

    expect(badge()).not.toBeInTheDocument();
    expect(await screen.findByText(/saved cart could not be read/i)).toBeInTheDocument();
    await screen.findByText('Gold Dragon Bracelet');
  });
});
