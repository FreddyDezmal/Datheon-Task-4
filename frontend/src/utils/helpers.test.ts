import { describe, expect, it } from 'vitest';
import { buildProductQueryString } from '../services/productService';
import { validateCustomer } from './checkoutValidation';
import { formatMajor, formatMoney, minorToMajor, roundHalfAwayFromZero } from './money';

describe('buildProductQueryString', () => {
  it('omits defaults so the canonical catalogue URL has no query', () => {
    expect(buildProductQueryString({ search: '', category: 'all', sort: 'featured' })).toBe('');
    expect(buildProductQueryString({ search: '   ', category: 'all', sort: 'featured' })).toBe('');
  });

  it('trims and encodes the search, and includes category and sort', () => {
    expect(buildProductQueryString({ search: '  gold & silver ', category: 'jewelry', sort: 'price-desc' })).toBe(
      '?search=gold+%26+silver&category=jewelry&sort=price-desc',
    );
  });
});

describe('validateCustomer', () => {
  const ok = { name: 'Ada', email: 'ada@example.com' };

  it.each([
    ['', 'Enter your name.'],
    ['   ', 'Enter your name.'],
    ['A', 'Name must be between 2 and 100 characters.'],
    ['x'.repeat(101), 'Name must be between 2 and 100 characters.'],
  ])('rejects the name %j', (name, message) => {
    expect(validateCustomer({ ...ok, name }).name).toBe(message);
  });

  it.each(['Al', 'x'.repeat(100), '  Ada  '])('accepts the name %j', (name) => {
    expect(validateCustomer({ ...ok, name }).name).toBeUndefined();
  });

  it.each(['', 'ada', 'ada@', '@example.com', 'ada@example', 'a da@example.com', `${'a'.repeat(250)}@b.co`])(
    'rejects the email %j',
    (email) => {
      expect(validateCustomer({ ...ok, email }).email).toBeDefined();
    },
  );

  it.each(['ada@example.com', ' ada@example.com ', 'a.b+c@sub.example.co.za'])('accepts the email %j', (email) => {
    expect(validateCustomer({ ...ok, email })).toEqual({});
  });
});

describe('money helpers', () => {
  it('formats minor and major units identically', () => {
    expect(formatMoney(3007, 'USD', 'en-US')).toBe('$30.07');
    expect(formatMajor(30.07, 'USD', 'en-US')).toBe('$30.07');
    expect(formatMoney(0, 'USD', 'en-US')).toBe('$0.00');
  });

  it('converts cents to a two-decimal number for the API without float noise', () => {
    expect(minorToMajor(3007)).toBe(30.07);
    expect(minorToMajor(10)).toBe(0.1);
    expect(minorToMajor(1 + 2)).toBe(0.03);
    expect(String(minorToMajor(110 + 220))).toBe('3.3');
  });

  it('rounds half away from zero like the server', () => {
    expect(roundHalfAwayFromZero(2.5)).toBe(3);
    expect(roundHalfAwayFromZero(-2.5)).toBe(-3);
    expect(roundHalfAwayFromZero(2.4999)).toBe(2);
    expect(roundHalfAwayFromZero(0)).toBe(0);
  });
});
