import type { PricingRules } from '../types/store';

const trimTrailingSlash = (value: string) => value.replace(/\/+$/, '');

/**
 * Development defaults to the local API. A production build without VITE_API_BASE_URL calls the
 * API on the same origin (e.g. behind a reverse proxy at /api), never a developer's localhost.
 */
export const API_BASE_URL = trimTrailingSlash(
  import.meta.env.VITE_API_BASE_URL ?? (import.meta.env.DEV ? 'http://localhost:5080' : ''),
);

/**
 * Used until /api/store/config responds (or if it can't be reached).
 * Mirrors the backend defaults in appsettings.json; the server remains authoritative at checkout.
 */
export const DEFAULT_PRICING_RULES: PricingRules = {
  currency: import.meta.env.VITE_DEFAULT_CURRENCY ?? 'USD',
  locale: import.meta.env.VITE_DEFAULT_LOCALE ?? 'en-US',
  taxRateBasisPoints: 800,
  flatShippingFeeMinor: 599,
  freeShippingThresholdMinor: 5000,
  maxQuantityPerLine: 99,
  maxLineItems: 50,
};

export const SEARCH_DEBOUNCE_MS = 250;
export const REQUEST_TIMEOUT_MS = 15_000;

/** Refresh the access token this long before it expires, so a request never starts with a stale one. */
export const TOKEN_REFRESH_MARGIN_MS = 60_000;
