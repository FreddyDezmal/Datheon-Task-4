/**
 * Response headers for the static site (Security-Audit.md, SA-12).
 *
 * The same function produces the values for Render's Static Site settings (Headers, path `/*`; see
 * Documentation/Deployment.md, section 10) and for `vite preview`, so the browser tests run the
 * production bundle under exactly this policy and fail on any violation (they treat console errors
 * as failures).
 *
 * The Content-Security-Policy allows only this site's own scripts and styles, Google Fonts, product
 * images from the catalogue's image host, and requests to the API. No inline script, no eval, no
 * framing, no plugins. If a cross-site scripting bug ever appeared, it could neither load code from
 * elsewhere nor send a stolen token anywhere but the API.
 */
export interface SiteOrigins {
  /** The API's origin, e.g. https://datheon-task-4.onrender.com (from VITE_API_BASE_URL). */
  api: string;
  /** Origins product images are served from, e.g. https://fakestoreapi.com. */
  images: readonly string[];
}

export function contentSecurityPolicy({ api, images }: SiteOrigins): string {
  return [
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self' https://fonts.googleapis.com",
    'font-src https://fonts.gstatic.com',
    ['img-src', "'self'", 'data:', ...images].join(' '),
    `connect-src ${api}`,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
  ].join('; ');
}

export function securityHeaders(origins: SiteOrigins): Record<string, string> {
  return {
    'Content-Security-Policy': contentSecurityPolicy(origins),
    'X-Content-Type-Options': 'nosniff',
    'X-Frame-Options': 'DENY',
    'Referrer-Policy': 'strict-origin-when-cross-origin',
    'Cross-Origin-Opener-Policy': 'same-origin',
    'Strict-Transport-Security': 'max-age=31536000',
  };
}

/** The origin (scheme, host, port) of an absolute URL, or null when it isn't one. */
export function originOf(url: string | undefined): string | null {
  if (!url) return null;
  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
}
