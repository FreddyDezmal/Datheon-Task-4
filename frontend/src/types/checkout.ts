export interface CustomerDetails {
  name: string;
  email: string;
}

export interface CheckoutRequest {
  customer: CustomerDetails;
  items: { productId: number; quantity: number }[];
  /** The total shown to the shopper, in major units. The server rejects the order with 409 if it differs. */
  expectedTotal: number;
}

export interface OrderLine {
  productId: number;
  title: string;
  unitPrice: number;
  unitPriceMinor: number;
  quantity: number;
  lineTotal: number;
}

export interface OrderTotals {
  subtotal: number;
  tax: number;
  shipping: number;
  total: number;
  currency: string;
}

export interface OrderQuote {
  lines: OrderLine[];
  totals: OrderTotals;
}

export interface CheckoutConfirmation {
  orderReference: string;
  createdAt: string;
  customerName: string;
  customerEmail: string;
  lines: OrderLine[];
  totals: OrderTotals;
  isSimulated: boolean;
  notice: string;
}
