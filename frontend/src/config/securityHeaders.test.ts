import { describe, expect, it } from 'vitest';
import { contentSecurityPolicy, originOf, securityHeaders } from './securityHeaders';

const live = { api: 'https://datheon-task-4.onrender.com', images: ['https://fakestoreapi.com'] };

const directives = (csp: string) =>
  Object.fromEntries(csp.split('; ').map((d) => [d.split(' ')[0], d.split(' ').slice(1)]));

describe('securityHeaders', () => {
  it('sets the hardening headers the static site lacked (SA-12)', () => {
    expect(securityHeaders(live)).toMatchObject({
      'X-Content-Type-Options': 'nosniff',
      'X-Frame-Options': 'DENY',
      'Referrer-Policy': 'strict-origin-when-cross-origin',
      'Cross-Origin-Opener-Policy': 'same-origin',
      'Strict-Transport-Security': 'max-age=31536000',
    });
  });

  it('allows scripts only from the site itself: no inline script, no eval, no other host', () => {
    const d = directives(contentSecurityPolicy(live));
    expect(d['script-src']).toEqual(["'self'"]);
    expect(d['default-src']).toEqual(["'self'"]);
    expect(contentSecurityPolicy(live)).not.toMatch(/unsafe-inline|unsafe-eval|\*/);
  });

  it('lets the page call only the API, and forbids framing, plugins and base or form hijacking', () => {
    const d = directives(contentSecurityPolicy(live));
    expect(d['connect-src']).toEqual(['https://datheon-task-4.onrender.com']);
    expect(d['frame-ancestors']).toEqual(["'none'"]);
    expect(d['object-src']).toEqual(["'none'"]);
    expect(d['base-uri']).toEqual(["'self'"]);
    expect(d['form-action']).toEqual(["'self'"]);
  });

  it('allows fonts from Google Fonts and images from the listed hosts only', () => {
    const d = directives(contentSecurityPolicy({ ...live, images: ['https://fakestoreapi.com', 'http://127.0.0.1:9101'] }));
    expect(d['style-src']).toEqual(["'self'", 'https://fonts.googleapis.com']);
    expect(d['font-src']).toEqual(['https://fonts.gstatic.com']);
    expect(d['img-src']).toEqual(["'self'", 'data:', 'https://fakestoreapi.com', 'http://127.0.0.1:9101']);
  });

  it('gives exactly the policy documented for Render', () => {
    expect(contentSecurityPolicy(live)).toBe(
      "default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; " +
        "img-src 'self' data: https://fakestoreapi.com; connect-src https://datheon-task-4.onrender.com; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'",
    );
  });
});

describe('originOf', () => {
  it.each([
    ['https://datheon-task-4.onrender.com', 'https://datheon-task-4.onrender.com'],
    ['https://example.com/api/', 'https://example.com'],
    ['http://localhost:5081', 'http://localhost:5081'],
  ])('%s -> %s', (url, origin) => expect(originOf(url)).toBe(origin));

  it.each([undefined, '', 'not a url', '/relative'])('returns null for %s', (url) => expect(originOf(url)).toBeNull());
});
