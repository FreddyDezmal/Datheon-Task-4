import { useEffect, useId, useState } from 'react';
import { MinusIcon, PlusIcon } from './Icons';

interface QuantityStepperProps {
  quantity: number;
  max: number;
  itemName: string;
  onChange: (quantity: number) => void;
}

const HINT_MS = 2500;

/**
 * Decrement policy: quantity never drops below 1 here. At 1 the "−" button is marked
 * aria-disabled but stays focusable; pressing it shows a hint pointing to Remove,
 * which is the only way to delete a line.
 */
export function QuantityStepper({ quantity, max, itemName, onChange }: QuantityStepperProps) {
  const hintId = useId();
  const [hint, setHint] = useState<string | null>(null);
  const atMin = quantity <= 1;
  const atMax = quantity >= max;

  useEffect(() => {
    if (!hint) return;
    const timer = setTimeout(() => setHint(null), HINT_MS);
    return () => clearTimeout(timer);
  }, [hint]);

  const decrease = () => {
    if (atMin) {
      setHint('Quantity can’t go below 1. Use Remove to delete this item.');
      return;
    }
    setHint(null);
    onChange(quantity - 1);
  };

  const increase = () => {
    if (atMax) {
      setHint(`You can order up to ${max} of this item.`);
      return;
    }
    setHint(null);
    onChange(quantity + 1);
  };

  return (
    <div className="stepper">
      <div className="stepper__controls" role="group" aria-label={`Quantity of ${itemName}`}>
        <button
          type="button"
          className="stepper__button"
          onClick={decrease}
          aria-disabled={atMin}
          aria-label={`Decrease quantity of ${itemName}`}
          aria-describedby={hint ? hintId : undefined}
        >
          <MinusIcon width={16} height={16} />
        </button>
        <span className="stepper__value" aria-live="polite" aria-atomic="true">
          <span className="visually-hidden">Quantity </span>
          {quantity}
        </span>
        <button
          type="button"
          className="stepper__button"
          onClick={increase}
          aria-disabled={atMax}
          aria-label={`Increase quantity of ${itemName}`}
          aria-describedby={hint ? hintId : undefined}
        >
          <PlusIcon width={16} height={16} />
        </button>
      </div>
      <span id={hintId} className="stepper__hint" role="status">
        {hint}
      </span>
    </div>
  );
}
