import { API_BASE_URL, REQUEST_TIMEOUT_MS } from '../config/env';

/** RFC 7807 problem details as produced by the ASP.NET Core API. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

export type ApiErrorCode =
  | 'NETWORK_ERROR'
  | 'TIMEOUT'
  | 'INVALID_RESPONSE'
  | 'VALIDATION_FAILED'
  | 'PRICE_CHANGED'
  | 'PRODUCTS_UNAVAILABLE'
  | 'NOT_FOUND'
  | 'UPSTREAM_UNAVAILABLE'
  | 'UPSTREAM_TIMEOUT'
  | 'UPSTREAM_MALFORMED'
  | 'UNAUTHORIZED'
  | 'SESSION_EXPIRED'
  | 'TOO_MANY_REQUESTS'
  | 'IDEMPOTENCY_KEY_REUSED'
  | 'DATABASE_BUSY'
  | 'DATABASE_UNAVAILABLE'
  | (string & {});

export class ApiError extends Error {
  readonly status: number;
  readonly code: ApiErrorCode;
  readonly problem: ProblemDetails | null;
  /** From the Retry-After header (429 and some 503 responses), when the server sent one. */
  readonly retryAfterSeconds: number | null;
  /**
   * The request's correlation ID (X-Correlation-Id / problem traceId), when the server answered.
   * Safe to show: it identifies the request in the server's logs and reveals nothing else.
   */
  readonly reference: string | null;

  constructor(
    message: string,
    status: number,
    code: ApiErrorCode,
    problem: ProblemDetails | null = null,
    retryAfterSeconds: number | null = null,
    reference: string | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.problem = problem;
    this.retryAfterSeconds = retryAfterSeconds;
    this.reference = reference;
  }

  /** True when retrying the same request might succeed. */
  get isRetryable(): boolean {
    return this.status === 0 || this.status === 408 || this.status === 429 || this.status >= 500;
  }
}

export const CORRELATION_ID_HEADER = 'X-Correlation-Id';
/** Set by the API when browsing data came from its last good copy because FakeStoreAPI was unreachable. */
export const CATALOGUE_STALE_HEADER = 'Catalogue-Stale';

/** False only when the browser knows it has no connection (navigator.onLine can't prove the opposite). */
export const isBrowserOffline = (): boolean => typeof navigator !== 'undefined' && navigator.onLine === false;

export const isAbortError = (error: unknown): boolean =>
  error instanceof DOMException && error.name === 'AbortError';

interface RequestOptions {
  method?: 'GET' | 'POST';
  body?: unknown;
  signal?: AbortSignal;
  headers?: Record<string, string>;
  timeoutMs?: number;
  /** Called with a successful response before it is parsed, e.g. to read a response header. */
  onResponse?: (response: Response) => void;
}

/**
 * Fetch wrapper: JSON in/out, timeouts, problem-details errors and response validation.
 * `parse` must validate the payload's shape and throw if it doesn't match.
 */
export async function apiRequest<T>(
  path: string,
  parse: (data: unknown) => T,
  { method = 'GET', body, signal, headers, timeoutMs = REQUEST_TIMEOUT_MS, onResponse }: RequestOptions = {},
): Promise<T> {
  const controller = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, timeoutMs);
  const forwardAbort = () => controller.abort();
  signal?.addEventListener('abort', forwardAbort, { once: true });
  if (signal?.aborted) controller.abort();

  let response: Response;
  try {
    const init: RequestInit = {
      method,
      headers: {
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...headers,
      },
      signal: controller.signal,
    };
    if (body !== undefined) init.body = JSON.stringify(body);
    response = await fetch(`${API_BASE_URL}${path}`, init);
  } catch (error) {
    if (timedOut) throw new ApiError('The store is taking too long to respond.', 0, 'TIMEOUT');
    if (signal?.aborted || isAbortError(error)) throw error;
    throw new ApiError(
      isBrowserOffline()
        ? "You're offline. Check your internet connection."
        : "We couldn't reach the store. Check your connection, or try again in a moment.",
      0,
      'NETWORK_ERROR',
    );
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', forwardAbort);
  }

  const data = await readJson(response);

  if (!response.ok) {
    const problem = isProblemDetails(data) ? data : null;
    const retryAfter = parseRetryAfter(response.headers.get('Retry-After'));
    throw new ApiError(
      describeFailure(response.status, problem, retryAfter),
      response.status,
      problem?.code ?? `HTTP_${response.status}`,
      problem,
      retryAfter,
      readReference(response, problem),
    );
  }

  onResponse?.(response);
  try {
    return parse(data);
  } catch {
    throw new ApiError(
      'The store sent an unexpected response.',
      response.status,
      'INVALID_RESPONSE',
      null,
      null,
      readReference(response, null),
    );
  }
}

/**
 * A message that is safe to show. The API's problem titles are written for users and never contain
 * internals, so they are used as-is; statuses that need a specific next step get their own wording.
 */
function describeFailure(status: number, problem: ProblemDetails | null, retryAfterSeconds: number | null): string {
  if (status === 429) {
    if (!retryAfterSeconds) return 'Too many requests. Wait a moment and try again.';
    // Sign-in protection can ask for a quarter of an hour: say so in minutes.
    if (retryAfterSeconds >= 120) return `Too many requests. Wait ${Math.ceil(retryAfterSeconds / 60)} minutes and try again.`;
    return `Too many requests. Wait ${retryAfterSeconds} ${retryAfterSeconds === 1 ? 'second' : 'seconds'} and try again.`;
  }
  if (status === 413) return 'That request was too large for the store to accept.';
  const text = [problem?.title, problem?.detail].filter(Boolean).join(' ');
  if (text) return text;
  if (status >= 500) return 'The store is having a problem right now. Try again in a moment.';
  return `Request failed with status ${status}.`;
}

const REFERENCE_PATTERN = /^[A-Za-z0-9._:-]{1,64}$/;

/** The correlation ID, if it looks like one (never echo arbitrary text from a response into the UI). */
function readReference(response: Response, problem: ProblemDetails | null): string | null {
  const candidates = [response.headers.get(CORRELATION_ID_HEADER), problem?.traceId];
  const found = candidates.find((v): v is string => typeof v === 'string' && REFERENCE_PATTERN.test(v));
  return found ?? null;
}

/** Retry-After is either delta-seconds or an HTTP date. */
function parseRetryAfter(value: string | null): number | null {
  if (!value) return null;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) return Math.ceil(seconds);
  const date = Date.parse(value);
  return Number.isNaN(date) ? null : Math.max(0, Math.ceil((date - Date.now()) / 1000));
}

async function readJson(response: Response): Promise<unknown> {
  const text = await response.text().catch(() => '');
  if (text.trim() === '') return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
}

function isProblemDetails(value: unknown): value is ProblemDetails {
  return typeof value === 'object' && value !== null && ('title' in value || 'status' in value);
}
