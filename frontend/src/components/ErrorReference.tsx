import type { ApiError } from '../services/apiClient';

/**
 * "Reference: …" for server-side failures, so a shopper can quote it and the request can be found in
 * the API's logs. Shown only for 5xx answers and only when the server sent a correlation ID.
 */
export function ErrorReference({ error }: { error: ApiError }) {
  if (!error.reference || error.status < 500) return null;
  return (
    <p className="error-reference">
      Reference: <code>{error.reference}</code>
    </p>
  );
}
