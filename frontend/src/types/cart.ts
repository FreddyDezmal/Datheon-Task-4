/** A cart line. Prices are snapshots for display; the server re-prices at checkout. */
export interface CartItem {
  productId: number;
  title: string;
  image: string;
  unitPriceMinor: number;
  quantity: number;
}

export interface CartState {
  items: CartItem[];
}

/** The product fields the cart needs when adding an item. */
export interface CartProductInput {
  id: number;
  title: string;
  image: string;
  priceMinor: number;
}

export interface CartTotals {
  subtotalMinor: number;
  taxMinor: number;
  shippingMinor: number;
  totalMinor: number;
  /** How much more merchandise would make shipping free (0 when already free or empty). */
  amountToFreeShippingMinor: number;
  unitCount: number;
}
