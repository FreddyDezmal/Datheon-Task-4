import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { TOKEN_REFRESH_MARGIN_MS } from '../config/env';
import { fetchAccountInfo, login, logout, refreshTokens, register as registerAccount } from '../services/accountService';
import { ApiError } from '../services/apiClient';
import type { AuthTokens, Credentials } from '../types/account';
import { clearSession, loadSession, saveSession } from '../utils/sessionStorage';
import { useToast } from './ToastContext';

export type SessionStatus = 'restoring' | 'anonymous' | 'signed-in';

export interface SessionValue {
  status: SessionStatus;
  email: string | null;
  signIn: (credentials: Credentials) => Promise<void>;
  /** Creates the account, then signs in with the same credentials. */
  register: (credentials: Credentials) => Promise<void>;
  signOut: () => void;
  /**
   * Runs an API call with a valid access token. The token is refreshed first if it is about to
   * expire, and refreshed once more if the API still answers 401. If the session can't be renewed,
   * the shopper is signed out locally and the call rejects with ApiError `SESSION_EXPIRED`.
   * Nothing here redirects or opens a sign-in screen, so an expired session can't cause a loop.
   */
  withAccessToken: <T>(call: (accessToken: string) => Promise<T>) => Promise<T>;
}

interface TokenState {
  accessToken: string;
  refreshToken: string;
  /** Epoch ms. 0 means "unknown, refresh before use" (e.g. restored while the API was unreachable). */
  expiresAt: number;
}

const SessionContext = createContext<SessionValue | null>(null);

const sessionExpired = () =>
  new ApiError('Your session has expired. Sign in again to continue.', 401, 'SESSION_EXPIRED');

/** A refresh token the server no longer accepts (expired, or the account's security stamp changed). */
const isRejectedRefresh = (error: unknown) => error instanceof ApiError && (error.status === 401 || error.status === 400);

export function SessionProvider({ children }: { children: ReactNode }) {
  const { notify } = useToast();
  const [state, setState] = useState<{ status: SessionStatus; email: string | null }>(() => {
    const stored = loadSession();
    return stored ? { status: 'restoring', email: stored.email } : { status: 'anonymous', email: null };
  });

  const tokens = useRef<TokenState | null>(null);
  const email = useRef<string | null>(state.email);
  const refreshing = useRef<Promise<string> | null>(null);
  // Bumped on every sign-in and sign-out, so a refresh that finishes late can't resurrect an old session.
  const generation = useRef(0);

  const adopt = useCallback((issued: AuthTokens, accountEmail: string) => {
    tokens.current = {
      accessToken: issued.accessToken,
      refreshToken: issued.refreshToken,
      expiresAt: Date.now() + issued.expiresIn * 1000,
    };
    email.current = accountEmail;
    saveSession({ refreshToken: issued.refreshToken, email: accountEmail });
    setState({ status: 'signed-in', email: accountEmail });
  }, []);

  const endSession = useCallback(
    (reason: 'signed-out' | 'expired') => {
      generation.current += 1;
      tokens.current = null;
      email.current = null;
      refreshing.current = null;
      clearSession();
      setState({ status: 'anonymous', email: null });
      if (reason === 'expired') notify('Your session has expired. Sign in again to see your orders.', 'info', 'session');
    },
    [notify],
  );

  /** One refresh at a time: concurrent callers share the same request. */
  const renew = useCallback((): Promise<string> => {
    if (refreshing.current) return refreshing.current;
    const current = tokens.current;
    if (!current) return Promise.reject(sessionExpired());

    const startedIn = generation.current;
    const request = refreshTokens(current.refreshToken)
      .then((issued) => {
        if (generation.current !== startedIn) throw sessionExpired();
        adopt(issued, email.current ?? '');
        return issued.accessToken;
      })
      .catch((error: unknown) => {
        if (isRejectedRefresh(error) && generation.current === startedIn) {
          endSession('expired');
          throw sessionExpired();
        }
        throw error; // e.g. network failure: keep the session and let the caller report it
      })
      .finally(() => {
        if (refreshing.current === request) refreshing.current = null;
      });
    refreshing.current = request;
    return request;
  }, [adopt, endSession]);

  const withAccessToken = useCallback(
    async <T,>(call: (accessToken: string) => Promise<T>): Promise<T> => {
      const current = tokens.current;
      if (!current) throw sessionExpired();
      const token =
        current.expiresAt - TOKEN_REFRESH_MARGIN_MS > Date.now() ? current.accessToken : await renew();
      try {
        return await call(token);
      } catch (error) {
        if (!(error instanceof ApiError) || error.status !== 401) throw error;
      }
      // The API rejected a token we thought was valid (e.g. the server's keys changed): renew once.
      const renewed = await renew();
      try {
        return await call(renewed);
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          endSession('expired');
          throw sessionExpired();
        }
        throw error;
      }
    },
    [renew, endSession],
  );

  // Restore a session saved in this tab. Guarded so React Strict Mode's double effect sends one request.
  const restoreStarted = useRef(false);
  useEffect(() => {
    if (restoreStarted.current) return;
    restoreStarted.current = true;
    const stored = loadSession();
    if (!stored) return;
    tokens.current = { accessToken: '', refreshToken: stored.refreshToken, expiresAt: 0 };
    renew().catch((error: unknown) => {
      if (error instanceof ApiError && error.code === 'SESSION_EXPIRED') return; // endSession already ran
      // The API is unreachable right now. Stay signed in; the next request retries the refresh.
      setState({ status: 'signed-in', email: stored.email });
    });
  }, [renew]);

  const signIn = useCallback(
    async (credentials: Credentials) => {
      const issued = await login(credentials);
      generation.current += 1;
      refreshing.current = null;
      let accountEmail = credentials.email.trim();
      try {
        accountEmail = (await fetchAccountInfo(issued.accessToken)).email;
      } catch {
        // The email typed in is good enough to show; the token itself has just been issued.
      }
      adopt(issued, accountEmail);
    },
    [adopt],
  );

  const register = useCallback(
    async (credentials: Credentials) => {
      await registerAccount(credentials);
      await signIn(credentials);
    },
    [signIn],
  );

  // Ends the session on the server too (SA-02), so a copy of the tokens stops working. The tab signs
  // out at once either way; the server call is best-effort.
  const signOut = useCallback(() => {
    const refreshToken = tokens.current?.refreshToken;
    endSession('signed-out');
    if (refreshToken) void logout(refreshToken);
  }, [endSession]);

  const value = useMemo<SessionValue>(
    () => ({ status: state.status, email: state.email, signIn, register, signOut, withAccessToken }),
    [state, signIn, register, signOut, withAccessToken],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionValue {
  const context = useContext(SessionContext);
  if (!context) throw new Error('useSession must be used inside <SessionProvider>.');
  return context;
}
