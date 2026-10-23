import { StrictMode } from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AppProviders, Storefront } from '../App';
import {
  accountRoutes,
  authHeader,
  bodyOf,
  callsTo,
  issueTokens,
  jsonResponse,
  mockApi,
  orderDetails,
  orderSummary,
  pricingRules,
  problem,
  seedSession,
  settle,
  unauthorized,
} from '../test/fixtures';
import { SESSION_STORAGE_KEY } from '../utils/sessionStorage';

function renderApp({ strict = false } = {}) {
  const user = userEvent.setup();
  const app = (
    <AppProviders pricingRules={pricingRules}>
      <Storefront />
    </AppProviders>
  );
  render(strict ? <StrictMode>{app}</StrictMode> : app);
  return { user };
}

const accountButton = () => screen.getByTestId('account-button');

async function openSignIn(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Gold Dragon Bracelet');
  await user.click(accountButton());
  return screen.getByRole('dialog', { name: 'Sign in' });
}

async function fillCredentials(user: ReturnType<typeof userEvent.setup>, dialog: HTMLElement, email: string, password: string) {
  await user.type(within(dialog).getByLabelText('Email'), email);
  await user.type(within(dialog).getByLabelText('Password'), password);
}

describe('signing in and creating an account', () => {
  it('creates an account, signs in, and keeps only the refresh token in this tab', async () => {
    const fetchMock = mockApi({ routes: { ...accountRoutes('new@example.com'), '/api/orders': () => jsonResponse([]) } });
    const { user } = renderApp();
    const dialog = await openSignIn(user);
    expect(within(dialog).getByLabelText('Email')).toHaveFocus();

    await user.click(within(dialog).getByRole('button', { name: 'Create an account' }));
    const register = screen.getByRole('dialog', { name: 'Create an account' });
    await fillCredentials(user, register, 'new@example.com', 'Str0ng!Passw0rd');
    await user.click(within(register).getByRole('button', { name: 'Create account' }));

    const account = await screen.findByRole('dialog', { name: 'Your account' });
    expect(within(account).getByTestId('account-email')).toHaveTextContent('new@example.com');
    expect(await within(account).findByText('No orders yet')).toBeInTheDocument();
    expect(accountButton()).toHaveAccessibleName('Your account, signed in as new@example.com');

    expect(bodyOf(callsTo(fetchMock, '/api/account/register')[0]?.[1])).toEqual({ email: 'new@example.com', password: 'Str0ng!Passw0rd' });
    expect(callsTo(fetchMock, '/api/account/login')).toHaveLength(1);
    // The orders request carries the access token the login returned.
    expect(authHeader(callsTo(fetchMock, '/api/orders')[0]?.[1])).toMatch(/^Bearer access-\d+$/);
    const stored = JSON.parse(window.sessionStorage.getItem(SESSION_STORAGE_KEY) ?? '{}');
    expect(Object.keys(stored).sort()).toEqual(['email', 'refreshToken']);
    expect(JSON.stringify(window.localStorage)).not.toMatch(/access-|refresh-/);
  });

  it('checks the password rules before calling the API', async () => {
    const fetchMock = mockApi({ routes: accountRoutes() });
    const { user } = renderApp();
    const dialog = await openSignIn(user);
    await user.click(within(dialog).getByRole('button', { name: 'Create an account' }));

    await fillCredentials(user, dialog, 'new@example.com', 'short');
    await user.click(within(dialog).getByRole('button', { name: 'Create account' }));

    expect(within(dialog).getByLabelText('Password')).toHaveAttribute('aria-invalid', 'true');
    expect(within(dialog).getByText(/meets all the requirements/)).toBeInTheDocument();
    expect(callsTo(fetchMock, '/api/account/register')).toHaveLength(0);
  });

  it('maps Identity validation errors to the right field', async () => {
    mockApi({
      routes: {
        '/api/account/register': () =>
          problem(400, 'VALIDATION_FAILED', {
            title: 'One or more validation errors occurred.',
            errors: {
              DuplicateUserName: ["Username 'ada@example.com' is already taken."],
              DuplicateEmail: ["Email 'ada@example.com' is already taken."],
            },
          }),
      },
    });
    const { user } = renderApp();
    const dialog = await openSignIn(user);
    await user.click(within(dialog).getByRole('button', { name: 'Create an account' }));
    await fillCredentials(user, dialog, 'ada@example.com', 'Str0ng!Passw0rd');
    await user.click(within(dialog).getByRole('button', { name: 'Create account' }));

    expect(await within(dialog).findByText(/An account with this email already exists/)).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true');
    // The shortcut switches to sign-in and keeps what was typed.
    await user.click(within(dialog).getByRole('button', { name: 'Go to sign in' }));
    expect(screen.getByRole('dialog', { name: 'Sign in' })).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Email')).toHaveValue('ada@example.com');
  });

  it('shows the server password rules it rejected', async () => {
    mockApi({
      routes: {
        '/api/account/register': () =>
          problem(400, 'VALIDATION_FAILED', {
            errors: { PasswordRequiresUniqueChars: ['Passwords must use at least 2 different characters.'] },
          }),
      },
    });
    const { user } = renderApp();
    const dialog = await openSignIn(user);
    await user.click(within(dialog).getByRole('button', { name: 'Create an account' }));
    await fillCredentials(user, dialog, 'new@example.com', 'Str0ng!Passw0rd');
    await user.click(within(dialog).getByRole('button', { name: 'Create account' }));

    expect(await within(dialog).findByText('Passwords must use at least 2 different characters.')).toBeInTheDocument();
  });

  it.each([
    ['a wrong password', () => unauthorized('Failed'), /don’t match an account/],
    ['a locked account', () => unauthorized('LockedOut'), /locked for a few minutes/],
    [
      'rate limiting',
      () => new Response(JSON.stringify({ status: 429, title: 'Too Many Requests', code: 'TOO_MANY_REQUESTS' }), {
        status: 429,
        headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '42' },
      }),
      /Wait 42 seconds/,
    ],
    ['an unreachable API', () => Promise.reject(new TypeError('Failed to fetch')), /We couldn't reach the store/],
  ])('explains %s without signing in', async (_case, respond, message) => {
    mockApi({ routes: { '/api/account/login': respond as () => Response } });
    const { user } = renderApp();
    const dialog = await openSignIn(user);
    await fillCredentials(user, dialog, 'ada@example.com', 'Str0ng!Passw0rd');
    await user.click(within(dialog).getByRole('button', { name: 'Sign in' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(message);
    expect(within(dialog).getByLabelText('Password')).toHaveValue('Str0ng!Passw0rd');
    expect(accountButton()).toHaveAccessibleName('Sign in');
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });

  it('signs out and forgets the session', async () => {
    seedSession();
    mockApi({ routes: { ...accountRoutes(), '/api/orders': () => jsonResponse([]) } });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in as ada@example.com/));

    await user.click(accountButton());
    await user.click(within(screen.getByRole('dialog', { name: 'Your account' })).getByRole('button', { name: 'Sign out' }));

    expect(accountButton()).toHaveAccessibleName('Sign in');
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
    expect(await screen.findByText('You have signed out.')).toBeInTheDocument();
  });
});

describe('restoring and expiring sessions', () => {
  it('restores a session after a reload with a single refresh, even under Strict Mode', async () => {
    seedSession('ada@example.com', 'refresh-saved');
    const fetchMock = mockApi({ routes: accountRoutes() });
    renderApp({ strict: true });

    await waitFor(() => expect(accountButton()).toHaveAccessibleName('Your account, signed in as ada@example.com'));
    const refreshes = callsTo(fetchMock, '/api/account/refresh');
    expect(refreshes).toHaveLength(1);
    expect(bodyOf(refreshes[0]?.[1])).toEqual({ refreshToken: 'refresh-saved' });
    // The rotated refresh token replaces the old one.
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).not.toContain('refresh-saved');
  });

  it('signs out quietly when the saved session is rejected, without retrying in a loop', async () => {
    seedSession();
    const fetchMock = mockApi({ routes: { '/api/account/refresh': () => unauthorized() } });
    renderApp();

    expect(await screen.findByText(/Your session has expired/)).toBeInTheDocument();
    expect(accountButton()).toHaveAccessibleName('Sign in');
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
    await settle();
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(1);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('discards a corrupt saved session', async () => {
    window.sessionStorage.setItem(SESSION_STORAGE_KEY, '{"refreshToken":');
    const fetchMock = mockApi();
    renderApp();
    await screen.findByText('Gold Dragon Bracelet');

    expect(accountButton()).toHaveAccessibleName('Sign in');
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(0);
  });

  it('stays signed in when the API is unreachable during restore, and retries later', async () => {
    seedSession();
    let apiUp = false;
    const fetchMock = mockApi({
      routes: {
        ...accountRoutes(),
        '/api/account/refresh': () => (apiUp ? jsonResponse(issueTokens()) : Promise.reject(new TypeError('Failed to fetch'))),
        '/api/orders': () => jsonResponse([orderSummary]),
      },
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in as ada@example.com/));

    apiUp = true;
    await user.click(accountButton());
    expect(await screen.findByText('DEMO-20261022-AAAAAA')).toBeInTheDocument();
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(2);
  });

  it('renews an access token the API rejects, then repeats the request once', async () => {
    seedSession();
    let ordersCalls = 0;
    const fetchMock = mockApi({
      routes: {
        ...accountRoutes(),
        '/api/orders': () => {
          ordersCalls += 1;
          return ordersCalls === 1 ? unauthorized() : jsonResponse([orderSummary]);
        },
      },
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in/));

    await user.click(accountButton());
    expect(await screen.findByText('DEMO-20261022-AAAAAA')).toBeInTheDocument();
    const [first, second] = callsTo(fetchMock, '/api/orders');
    expect(authHeader(second?.[1])).not.toBe(authHeader(first?.[1]));
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(2); // restore + renewal
  });

  it('shows the sign-in form, not a loop, when the session cannot be renewed', async () => {
    seedSession();
    let refreshCalls = 0;
    const fetchMock = mockApi({
      routes: {
        ...accountRoutes(),
        '/api/account/refresh': () => (++refreshCalls === 1 ? jsonResponse(issueTokens()) : unauthorized()),
        '/api/orders': () => unauthorized(),
      },
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in/));

    await user.click(accountButton());
    expect(await screen.findByRole('dialog', { name: 'Sign in' })).toBeInTheDocument();
    expect(await screen.findByText(/Your session has expired/)).toBeInTheDocument();
    await settle();
    expect(callsTo(fetchMock, '/api/orders')).toHaveLength(1);
    expect(refreshCalls).toBe(2);
  });
});

describe('order history', () => {
  it('lists orders and loads one order’s saved lines on demand', async () => {
    seedSession();
    const fetchMock = mockApi({
      routes: {
        ...accountRoutes(),
        '/api/orders': () => jsonResponse([orderSummary]),
        [`/api/orders/${orderSummary.orderReference}`]: () => jsonResponse(orderDetails),
      },
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in/));
    await user.click(accountButton());

    const row = await screen.findByRole('button', { name: /DEMO-20261022-AAAAAA/ });
    expect(row).toHaveTextContent('2 items');
    expect(row).toHaveTextContent('$54.16');
    expect(row).toHaveAttribute('aria-expanded', 'false');
    expect(callsTo(fetchMock, `/api/orders/${orderSummary.orderReference}`)).toHaveLength(0);

    await user.click(row);
    expect(row).toHaveAttribute('aria-expanded', 'true');
    const table = await screen.findByRole('table', { name: /Items in order DEMO-20261022-AAAAAA/ });
    expect(within(table).getByText('Mens Casual Slim Fit T-Shirt')).toBeInTheDocument();
    expect(within(table).getByText('$5.99')).toBeInTheDocument();
  });

  it('offers a retry when the orders fail to load', async () => {
    seedSession();
    let fail = true;
    mockApi({
      routes: {
        ...accountRoutes(),
        '/api/orders': () => (fail ? problem(503, 'DATABASE_BUSY', { title: 'The store is busy. Try again in a moment.' }) : jsonResponse([])),
      },
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in/));
    await user.click(accountButton());

    expect(await screen.findByRole('alert')).toHaveTextContent('The store is busy');
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('No orders yet')).toBeInTheDocument();
  });

  it('says when an order cannot be found', async () => {
    seedSession();
    mockApi({
      routes: {
        ...accountRoutes(),
        '/api/orders': () => jsonResponse([orderSummary]),
        [`/api/orders/${orderSummary.orderReference}`]: () => problem(404, 'NOT_FOUND', { title: 'Order not found.' }),
      },
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in/));
    await user.click(accountButton());
    await user.click(await screen.findByRole('button', { name: /DEMO-20261022-AAAAAA/ }));

    expect(await screen.findByText('This order could not be found.')).toBeInTheDocument();
  });
});

describe('expired session during checkout', () => {
  it('keeps the expired-session notice if the shopper opens sign-in and comes back without signing in', async () => {
    seedSession();
    window.localStorage.setItem(
      'greenhaus.cart',
      JSON.stringify({ version: 1, items: [{ productId: 2, title: 'Mens Casual Slim Fit T-Shirt', image: '', unitPriceMinor: 2230, quantity: 1 }] }),
    );
    let refreshes = 0;
    mockApi({
      routes: { ...accountRoutes(), '/api/account/refresh': () => (++refreshes === 1 ? jsonResponse(issueTokens()) : unauthorized()) },
      checkout: () => unauthorized(),
    });
    const { user } = renderApp();
    await waitFor(() => expect(accountButton()).toHaveAccessibleName(/signed in/));
    await user.click(screen.getByRole('button', { name: /open cart/i }));
    await user.click(screen.getByRole('button', { name: 'Check out' }));
    const checkout = screen.getByRole('dialog', { name: 'Check out' });
    await user.type(within(checkout).getByLabelText('Full name'), 'Ada Lovelace');
    await user.click(within(checkout).getByRole('button', { name: 'Place demo order' }));
    expect(await within(checkout).findByRole('alert')).toHaveTextContent('Your session expired');

    await user.click(within(checkout).getByRole('button', { name: 'Sign in again' }));
    await user.keyboard('{Escape}'); // leave sign-in without signing in

    const back = await screen.findByRole('dialog', { name: 'Check out' });
    expect(within(back).getByRole('alert')).toHaveTextContent('Your session expired');
    expect(within(back).getByRole('button', { name: 'Place order as a guest' })).toBeInTheDocument();
  });
});
