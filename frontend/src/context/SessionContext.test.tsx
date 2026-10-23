import type { ReactNode } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../services/apiClient';
import {
  accountRoutes,
  authHeader,
  callsTo,
  issueTokens,
  jsonResponse,
  mockApi,
  seedSession,
  unauthorized,
  type Handler,
} from '../test/fixtures';
import { SESSION_STORAGE_KEY } from '../utils/sessionStorage';
import { SessionProvider, useSession } from './SessionContext';
import { ToastProvider } from './ToastContext';

const wrapper = ({ children }: { children: ReactNode }) => (
  <ToastProvider>
    <SessionProvider>{children}</SessionProvider>
  </ToastProvider>
);

const renderSession = () => renderHook(() => useSession(), { wrapper });

/** An endpoint that answers only when the test says so. */
function deferred() {
  let release: (response: Response) => void = () => {};
  const handler: Handler = () => new Promise<Response>((resolve) => (release = resolve));
  return { handler, release: (response: Response) => release(response) };
}

const echoToken = async (token: string) => token;

describe('SessionContext', () => {
  afterEach(() => vi.useRealTimers());

  it('rejects withAccessToken when signed out, without calling the API', async () => {
    const fetchMock = mockApi();
    const { result } = renderSession();

    expect(result.current.status).toBe('anonymous');
    await expect(result.current.withAccessToken(echoToken)).rejects.toMatchObject({ code: 'SESSION_EXPIRED' });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('uses the current token without refreshing while it is fresh', async () => {
    const fetchMock = mockApi({ routes: accountRoutes() });
    const { result } = renderSession();
    await act(() => result.current.signIn({ email: 'ada@example.com', password: 'x' }));

    const token = await result.current.withAccessToken(echoToken);
    expect(token).toMatch(/^access-\d+$/);
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(0);
  });

  it('refreshes before the token expires, so no request is sent with a stale token', async () => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-10-22T10:00:00Z') });
    const fetchMock = mockApi({ routes: accountRoutes() });
    const { result } = renderSession();
    await act(() => result.current.signIn({ email: 'ada@example.com', password: 'x' }));
    const first = await result.current.withAccessToken(echoToken);

    // 59 minutes and 10 seconds later the token has under a minute left.
    vi.setSystemTime(new Date('2026-10-22T10:59:10Z'));
    const second = await result.current.withAccessToken(echoToken);

    expect(second).not.toBe(first);
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(1);
  });

  it('shares one refresh between concurrent requests', async () => {
    seedSession();
    const refresh = deferred();
    const fetchMock = mockApi({ routes: { ...accountRoutes(), '/api/account/refresh': refresh.handler } });
    const { result } = renderSession();
    // The saved session is being restored: its refresh is in flight.
    expect(result.current.status).toBe('restoring');

    const calls = [1, 2, 3].map(() => result.current.withAccessToken(echoToken));
    await act(async () => refresh.release(jsonResponse(issueTokens())));

    const tokens = await Promise.all(calls);
    expect(new Set(tokens).size).toBe(1);
    expect(callsTo(fetchMock, '/api/account/refresh')).toHaveLength(1);
    expect(result.current.status).toBe('signed-in');
  });

  it('ignores a refresh that finishes after the shopper signed out', async () => {
    seedSession();
    const refresh = deferred();
    mockApi({ routes: { ...accountRoutes(), '/api/account/refresh': refresh.handler } });
    const { result } = renderSession();
    // Handle the rejection as soon as it happens, so it never surfaces as unhandled.
    const pending = result.current.withAccessToken(echoToken).catch((error: unknown) => error);

    act(() => result.current.signOut());
    await act(async () => refresh.release(jsonResponse(issueTokens())));

    expect(await pending).toMatchObject({ code: 'SESSION_EXPIRED' });
    expect(result.current.status).toBe('anonymous');
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });

  it('keeps the session when a refresh fails because the network is down', async () => {
    seedSession();
    mockApi({ routes: { ...accountRoutes(), '/api/account/refresh': () => Promise.reject(new TypeError('Failed to fetch')) } });
    const { result } = renderSession();
    await waitFor(() => expect(result.current.status).toBe('signed-in'));

    const failure = await result.current.withAccessToken(echoToken).catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).code).toBe('NETWORK_ERROR');
    expect(result.current.status).toBe('signed-in');
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).not.toBeNull();
  });

  it('does not retry a request that fails for reasons other than authentication', async () => {
    mockApi({ routes: accountRoutes() });
    const { result } = renderSession();
    await act(() => result.current.signIn({ email: 'ada@example.com', password: 'x' }));
    const call = vi.fn(async () => {
      throw new ApiError('Prices have changed.', 409, 'PRICE_CHANGED');
    });

    await expect(result.current.withAccessToken(call)).rejects.toMatchObject({ code: 'PRICE_CHANGED' });
    expect(call).toHaveBeenCalledTimes(1);
  });

  it('signs in with the typed email when account info is unavailable', async () => {
    mockApi({ routes: { ...accountRoutes(), '/api/account/manage/info': () => unauthorized() } });
    const { result } = renderSession();

    await act(() => result.current.signIn({ email: '  ada@example.com ', password: 'x' }));
    expect(result.current.email).toBe('ada@example.com');
    expect(result.current.status).toBe('signed-in');
  });

  it('stays signed out when registration fails', async () => {
    const fetchMock = mockApi({
      routes: {
        ...accountRoutes(),
        '/api/account/register': () =>
          jsonResponse({ status: 400, title: 'Invalid', code: 'VALIDATION_FAILED', errors: { DuplicateEmail: ['x'] } }, 400),
      },
    });
    const { result } = renderSession();

    await expect(act(() => result.current.register({ email: 'ada@example.com', password: 'x' }))).rejects.toMatchObject({
      code: 'VALIDATION_FAILED',
    });
    expect(result.current.status).toBe('anonymous');
    expect(callsTo(fetchMock, '/api/account/login')).toHaveLength(0);
  });

  it('sends the access token as a bearer header to the account info endpoint', async () => {
    const fetchMock = mockApi({ routes: accountRoutes() });
    const { result } = renderSession();
    await act(() => result.current.signIn({ email: 'ada@example.com', password: 'x' }));

    expect(authHeader(callsTo(fetchMock, '/api/account/manage/info')[0]?.[1])).toMatch(/^Bearer access-\d+$/);
  });
});
