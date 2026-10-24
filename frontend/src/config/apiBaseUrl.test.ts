import { describe, expect, it } from 'vitest';
import { apiBaseUrlProblem } from './apiBaseUrl';

describe('apiBaseUrlProblem', () => {
  it.each([
    ['the deployed API', 'https://datheon-task-4.onrender.com'],
    ['a trailing slash (trimmed at runtime)', 'https://api.example.com/'],
    ['a path prefix', 'https://example.com/storefront-api'],
    ['the local API in development', 'http://localhost:5080'],
    ['the browser tests\' API', 'http://localhost:5081'],
    ['127.0.0.1', 'http://127.0.0.1:5080'],
    ['IPv6 loopback', 'http://[::1]:5080'],
  ])('accepts %s', (_, value) => {
    expect(apiBaseUrlProblem(value, { required: true })).toBeNull();
  });

  it('allows it to be unset outside Render (a same-origin build)', () => {
    expect(apiBaseUrlProblem(undefined, { required: false })).toBeNull();
  });

  it('requires it on Render', () => {
    expect(apiBaseUrlProblem(undefined, { required: true })).toMatch(/^VITE_API_BASE_URL is not set/);
  });

  it.each([
    ['empty', '', /set but empty/],
    ['blank', '   ', /set but empty/],
    ['a bare host name', 'datheon-task-4.onrender.com', /must be an absolute address/],
    ['a relative path', '/api', /must be an absolute address/],
    ['http to a public host (mixed content)', 'http://datheon-task-4.onrender.com', /must use https:\/\//],
    ['another scheme', 'ftp://api.example.com', /must start with https:\/\//],
    ['credentials', 'https://user:secret@api.example.com', /user name or password/],
    ['a query string', 'https://api.example.com?x=1', /query string or fragment/],
    ['a fragment', 'https://api.example.com/#x', /query string or fragment/],
  ])('rejects %s', (_, value, message) => {
    expect(apiBaseUrlProblem(value, { required: false })).toMatch(message);
  });

  it('never repeats credentials in its message', () => {
    expect(apiBaseUrlProblem('https://user:secret@api.example.com', { required: false })).not.toContain('secret');
  });
});
