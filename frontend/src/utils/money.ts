const formatters = new Map<string, Intl.NumberFormat>();

function getFormatter(currency: string, locale: string): Intl.NumberFormat {
  const key = `${locale}|${currency}`;
  let formatter = formatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(locale, { style: 'currency', currency });
    formatters.set(key, formatter);
  }
  return formatter;
}

/** Formats an integer amount of minor units (cents) as currency. */
export function formatMoney(minor: number, currency: string, locale: string): string {
  return getFormatter(currency, locale).format(minor / 100);
}

/** Formats an amount already in major units (as returned by the checkout API). Display only. */
export function formatMajor(amount: number, currency: string, locale: string): string {
  return getFormatter(currency, locale).format(amount);
}

/**
 * Converts minor units to a major-unit number for an API payload.
 * Integer cents divided by 100 and fixed to 2 dp round-trips exactly through JSON into a C# decimal.
 */
export function minorToMajor(minor: number): number {
  return Number((minor / 100).toFixed(2));
}

/** Rounds half away from zero, matching MidpointRounding.AwayFromZero on the server. */
export function roundHalfAwayFromZero(value: number): number {
  return Math.sign(value) * Math.round(Math.abs(value));
}
