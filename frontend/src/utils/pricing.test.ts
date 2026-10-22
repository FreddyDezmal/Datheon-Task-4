import { describe, expect, it } from 'vitest';
import { pricingRules } from '../test/fixtures';
import type { CartItem } from '../types/cart';
import {
  calculateCartTotals,
  calculateShipping,
  calculateSubtotal,
  calculateTax,
  calculateTotal,
  countUnits,
} from './pricing';

const item = (productId: number, unitPriceMinor: number, quantity = 1): CartItem => ({
  productId,
  title: `Product ${productId}`,
  image: '',
  unitPriceMinor,
  quantity,
});

// These cases mirror Storefront.Api.Tests/Pricing/PricingCalculatorTests.cs so both sides agree.
describe('pricing', () => {
  it('returns zero for every amount when the cart is empty', () => {
    expect(calculateCartTotals([], pricingRules)).toEqual({
      subtotalMinor: 0,
      taxMinor: 0,
      shippingMinor: 0,
      totalMinor: 0,
      amountToFreeShippingMinor: 0,
      unitCount: 0,
    });
  });

  it('charges tax and flat shipping below the threshold', () => {
    const totals = calculateCartTotals([item(1, 2230)], pricingRules);
    expect(totals).toMatchObject({ subtotalMinor: 2230, taxMinor: 178, shippingMinor: 599, totalMinor: 3007 });
    expect(totals.amountToFreeShippingMinor).toBe(2770);
  });

  it('multiplies by quantity and drops shipping once the subtotal reaches the threshold', () => {
    expect(calculateCartTotals([item(2, 2230, 2)], pricingRules).shippingMinor).toBe(599);
    expect(calculateCartTotals([item(2, 2230, 3)], pricingRules)).toMatchObject({
      subtotalMinor: 6690,
      taxMinor: 535,
      shippingMinor: 0,
      totalMinor: 7225,
      unitCount: 3,
    });
  });

  it('sums lines before rounding tax once', () => {
    const totals = calculateCartTotals([item(1, 995), item(2, 995), item(3, 995)], pricingRules);
    expect(totals).toMatchObject({ subtotalMinor: 2985, taxMinor: 239, totalMinor: 3823 });
  });

  it.each([
    [4999, 599],
    [5000, 0],
    [5001, 0],
    [1, 599],
    [0, 0],
  ])('shipping for a %i cent subtotal is %i', (subtotal, expected) => {
    expect(calculateShipping(subtotal, pricingRules)).toBe(expected);
  });

  it('bases free shipping on the pre-tax subtotal', () => {
    const totals = calculateCartTotals([item(1, 4999)], pricingRules);
    expect(totals).toMatchObject({ taxMinor: 400, shippingMinor: 599, totalMinor: 5998 });
  });

  it('rounds tax half away from zero', () => {
    expect(calculateTax(125, pricingRules)).toBe(10); // 10.0
    expect(calculateTax(5, { ...pricingRules, taxRateBasisPoints: 1000 })).toBe(1); // 0.5 -> 1
    expect(calculateTax(31, pricingRules)).toBe(2); // 2.48 -> 2
    expect(calculateTax(1937, pricingRules)).toBe(155); // 154.96 -> 155
  });

  it('avoids floating-point drift by working in integer cents', () => {
    // 0.1 + 0.2 style errors: ten items at $0.10 must be exactly $1.00.
    const items = Array.from({ length: 10 }, (_, i) => item(i + 1, 10));
    expect(calculateSubtotal(items)).toBe(100);
    expect(calculateTotal(100, 8, 599)).toBe(707);
  });

  it('counts units, not distinct products', () => {
    expect(countUnits([item(1, 100, 3), item(2, 100, 2)])).toBe(5);
  });
});
