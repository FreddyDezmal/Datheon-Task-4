import type { OrderLine, OrderTotals } from './checkout';

/** POST /api/account/login and /api/account/refresh (ASP.NET Core Identity bearer tokens). */
export interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  /** Lifetime of the access token, in seconds. */
  expiresIn: number;
}

/** GET /api/account/manage/info */
export interface AccountInfo {
  email: string;
}

export interface Credentials {
  email: string;
  password: string;
}

/** One row of GET /api/orders. */
export interface OrderSummary {
  orderReference: string;
  createdAt: string;
  itemCount: number;
  total: number;
  currency: string;
}

/** GET /api/orders/{reference}: the order as it was priced and saved at checkout. */
export interface OrderDetails {
  orderReference: string;
  createdAt: string;
  customerName: string;
  customerEmail: string;
  lines: OrderLine[];
  totals: OrderTotals;
}
