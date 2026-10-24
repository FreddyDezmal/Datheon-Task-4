/**
 * Build-time check of VITE_API_BASE_URL (run by vite.config.ts for `vite build`).
 *
 * The value is compiled into the bundle, so a wrong one only shows up after deploying: without it a
 * production build calls its own origin, where a static host answers /api/... with index.html, and an
 * http:// address is blocked by browsers on an https:// site (mixed content). Failing the build instead
 * keeps the previous deploy serving.
 *
 * Returns why the value is unusable, or null when it's fine. The value isn't secret (it ships to every
 * browser), so the message repeats it.
 */
export function apiBaseUrlProblem(value: string | undefined, options: { required: boolean }): string | null {
  if (value === undefined) {
    return options.required
      ? 'VITE_API_BASE_URL is not set. Set it to the API\'s https:// address (for example https://<api>.onrender.com), then build again.'
      : null;
  }

  const trimmed = value.trim();
  if (trimmed === '') {
    return 'VITE_API_BASE_URL is set but empty. Set it to the API\'s https:// address, or remove it.';
  }

  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return `VITE_API_BASE_URL must be an absolute address such as https://api.example.com (got "${trimmed}").`;
  }

  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    return `VITE_API_BASE_URL must start with https:// (got "${trimmed}").`;
  }

  if (url.protocol === 'http:' && !isLocalHost(url.hostname)) {
    return `VITE_API_BASE_URL must use https:// (got "${trimmed}"). Browsers block http:// requests from a site served over https.`;
  }

  if (url.username || url.password) {
    return 'VITE_API_BASE_URL must not contain a user name or password: everything in it is visible to every visitor.';
  }

  if (url.search || url.hash) {
    return `VITE_API_BASE_URL must not contain a query string or fragment (got "${trimmed}").`;
  }

  return null;
}

function isLocalHost(hostname: string): boolean {
  return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]';
}
