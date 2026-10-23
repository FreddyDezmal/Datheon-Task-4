import { afterEach, describe, expect, it, vi } from 'vitest';
import { SESSION_STORAGE_KEY, clearSession, loadSession, saveSession } from './sessionStorage';

describe('session storage', () => {
  afterEach(() => vi.restoreAllMocks());

  it('round-trips a session in sessionStorage, not localStorage', () => {
    saveSession({ refreshToken: 'r1', email: 'ada@example.com' });

    expect(loadSession()).toEqual({ refreshToken: 'r1', email: 'ada@example.com' });
    expect(window.localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });

  it('returns null when nothing is stored', () => {
    expect(loadSession()).toBeNull();
  });

  it.each([
    ['invalid JSON', '{"refreshToken":'],
    ['an empty refresh token', JSON.stringify({ refreshToken: '', email: 'a@b.co' })],
    ['a missing email', JSON.stringify({ refreshToken: 'r1' })],
    ['a number for a token', JSON.stringify({ refreshToken: 42, email: 'a@b.co' })],
    ['a JSON string', JSON.stringify('r1')],
  ])('discards %s and clears it', (_case, raw) => {
    window.sessionStorage.setItem(SESSION_STORAGE_KEY, raw);

    expect(loadSession()).toBeNull();
    expect(window.sessionStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });

  it('drops extra fields rather than keeping them (an access token never goes to storage)', () => {
    window.sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify({ refreshToken: 'r1', email: 'a@b.co', accessToken: 'secret' }));

    expect(loadSession()).toEqual({ refreshToken: 'r1', email: 'a@b.co' });
  });

  it('copes with storage that is blocked or full', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Blocked', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Full', 'QuotaExceededError');
    });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Blocked', 'SecurityError');
    });

    expect(() => saveSession({ refreshToken: 'r1', email: 'a@b.co' })).not.toThrow();
    expect(loadSession()).toBeNull();
    expect(() => clearSession()).not.toThrow();
  });
});
