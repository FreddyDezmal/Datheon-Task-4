import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { DEFAULT_PRICING_RULES } from '../config/env';
import { fetchStoreConfig } from '../services/productService';
import type { PricingRules } from '../types/store';

const StoreConfigContext = createContext<PricingRules>(DEFAULT_PRICING_RULES);

/**
 * Loads pricing rules from the API so cart estimates match checkout exactly.
 * Falls back to the built-in defaults if the request fails; checkout still re-prices on the server.
 */
export function StoreConfigProvider({ children, initialRules }: { children: ReactNode; initialRules?: PricingRules }) {
  const [rules, setRules] = useState<PricingRules>(initialRules ?? DEFAULT_PRICING_RULES);

  useEffect(() => {
    if (initialRules) return;
    const controller = new AbortController();
    fetchStoreConfig(controller.signal)
      .then(setRules)
      .catch(() => {
        /* keep defaults */
      });
    return () => controller.abort();
  }, [initialRules]);

  return <StoreConfigContext.Provider value={rules}>{children}</StoreConfigContext.Provider>;
}

export const usePricingRules = () => useContext(StoreConfigContext);
