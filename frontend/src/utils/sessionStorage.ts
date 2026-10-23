/**
 * What survives a page refresh for a signed-in shopper: the refresh token and email, in this tab's
 * sessionStorage (cleared when the tab closes, not shared with other tabs). The access token is
 * kept in memory only and is re-issued from the refresh token after a reload.
 *
 * Trade-off: anything in web storage is readable by script running on this origin, so a cross-site
 * scripting bug could steal the refresh token. The app renders no untrusted HTML, and the API's
 * tokens expire; an httpOnly-cookie session would remove this exposure but needs CSRF protection
 * on the API (see Documentation/Frontend.md).
 */

export const SESSION_STORAGE_KEY = 'greenhaus.session';

export interface StoredSession {
  refreshToken: string;
  email: string;
}

function storage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null; // blocked by browser settings
  }
}

export function loadSession(): StoredSession | null {
  try {
    const raw = storage()?.getItem(SESSION_STORAGE_KEY);
    if (!raw) return null;
    const value: unknown = JSON.parse(raw);
    if (
      typeof value === 'object' &&
      value !== null &&
      typeof (value as StoredSession).refreshToken === 'string' &&
      typeof (value as StoredSession).email === 'string' &&
      (value as StoredSession).refreshToken.length > 0
    ) {
      return { refreshToken: (value as StoredSession).refreshToken, email: (value as StoredSession).email };
    }
  } catch {
    // Invalid JSON: fall through and discard it.
  }
  clearSession();
  return null;
}

export function saveSession(session: StoredSession): void {
  try {
    storage()?.setItem(SESSION_STORAGE_KEY, JSON.stringify(session));
  } catch {
    // Storage full or blocked: the session still works until the page is reloaded.
  }
}

export function clearSession(): void {
  try {
    storage()?.removeItem(SESSION_STORAGE_KEY);
  } catch {
    // Nothing to clear.
  }
}
