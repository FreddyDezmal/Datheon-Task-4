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
  | (string & {});

export class ApiError extends Error {
  readonly status: number;
  readonly code: ApiErrorCode;
  readonly problem: ProblemDetails | null;

  constructor(message: string, status: number, code: ApiErrorCode, problem: ProblemDetails | null = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.problem = problem;
  }

  /** True when retrying the same request might succeed. */
  get isRetryable(): boolean {
    return this.status === 0 || this.status === 408 || this.status === 429 || this.status >= 500;
  }
}

export const isAbortError = (error: unknown): boolean =>
  error instanceof DOMException && error.name === 'AbortError';

interface RequestOptions {
  method?: 'GET' | 'POST';
  body?: unknown;
  signal?: AbortSignal;
  headers?: Record<string, string>;
  timeoutMs?: number;
}

/**
 * Fetch wrapper: JSON in/out, timeouts, problem-details errors and response validation.
 * `parse` must validate the payload's shape and throw if it doesn't match.
 */
export async function apiRequest<T>(
  path: string,
  parse: (data: unknown) => T,
  { method = 'GET', body, signal, headers, timeoutMs = REQUEST_TIMEOUT_MS }: RequestOptions = {},
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
    if (timedOut) throw new ApiError('The store took too long to respond.', 0, 'TIMEOUT');
    if (signal?.aborted || isAbortError(error)) throw error;
    throw new ApiError('Could not reach the store. Check that the API is running and try again.', 0, 'NETWORK_ERROR');
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', forwardAbort);
  }

  const data = await readJson(response);

  if (!response.ok) {
    const problem = isProblemDetails(data) ? data : null;
    throw new ApiError(
      [problem?.title, problem?.detail].filter(Boolean).join(' ') || `Request failed with status ${response.status}.`,
      response.status,
      problem?.code ?? `HTTP_${response.status}`,
      problem,
    );
  }

  try {
    return parse(data);
  } catch {
    throw new ApiError('The store sent an unexpected response.', response.status, 'INVALID_RESPONSE');
  }
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
