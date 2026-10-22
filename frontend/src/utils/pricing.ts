import type { CartItem, CartTotals } from '../types/cart';
import type { PricingRules } from '../types/store';
import { roundHalfAwayFromZero } from './money';

/*
 * Pricing policy (identical to Storefront.Api PricingCalculator):
 *  - All arithmetic happens in integer minor units, so sums never accumulate float error.
 *  - Subtotal = Σ unit price × quantity (merchandise only).
 *  - Tax = subtotal × rate, rounded once, half away from zero. Shipping is not taxed.
 *  - Shipping = flat fee when 0 < subtotal < threshold; free at or above it; 0 when empty.
 *  - Total = subtotal + tax + shipping.
 */

export function calculateLineTotal(item: Pick<CartItem, 'unitPriceMinor' | 'quantity'>): number {
  return item.unitPriceMinor * item.quantity;
}

export function calculateSubtotal(items: readonly CartItem[]): number {
  return items.reduce((sum, item) => sum + calculateLineTotal(item), 0);
}

export function calculateTax(subtotalMinor: number, rules: PricingRules): number {
  // Integer × integer / 10_000: the quotient is exact or correctly rounded, so .5 cases are exact.
  return roundHalfAwayFromZero((subtotalMinor * rules.taxRateBasisPoints) / 10_000);
}

export function calculateShipping(subtotalMinor: number, rules: PricingRules): number {
  return subtotalMinor > 0 && subtotalMinor < rules.freeShippingThresholdMinor ? rules.flatShippingFeeMinor : 0;
}

export function calculateTotal(subtotalMinor: number, taxMinor: number, shippingMinor: number): number {
  return subtotalMinor + taxMinor + shippingMinor;
}

export function countUnits(items: readonly CartItem[]): number {
  return items.reduce((sum, item) => sum + item.quantity, 0);
}

export function calculateCartTotals(items: readonly CartItem[], rules: PricingRules): CartTotals {
  const subtotalMinor = calculateSubtotal(items);
  const taxMinor = calculateTax(subtotalMinor, rules);
  const shippingMinor = calculateShipping(subtotalMinor, rules);
  return {
    subtotalMinor,
    taxMinor,
    shippingMinor,
    totalMinor: calculateTotal(subtotalMinor, taxMinor, shippingMinor),
    amountToFreeShippingMinor: shippingMinor > 0 ? rules.freeShippingThresholdMinor - subtotalMinor : 0,
    unitCount: countUnits(items),
  };
}
