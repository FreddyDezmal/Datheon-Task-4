/** Pricing rules shared with the backend (GET /api/store/config). */
export interface PricingRules {
  currency: string;
  locale: string;
  taxRateBasisPoints: number;
  flatShippingFeeMinor: number;
  freeShippingThresholdMinor: number;
  maxQuantityPerLine: number;
  maxLineItems: number;
}
