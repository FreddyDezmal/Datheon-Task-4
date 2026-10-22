import { useId } from 'react';
import type { SortOption } from '../types/product';

const LABELS: Record<SortOption, string> = {
  featured: 'Featured',
  'price-asc': 'Price: low to high',
  'price-desc': 'Price: high to low',
  rating: 'Highest rated',
};

interface SortSelectProps {
  value: SortOption;
  onChange: (value: SortOption) => void;
}

export function SortSelect({ value, onChange }: SortSelectProps) {
  const id = useId();
  return (
    <div className="sort-select">
      <label htmlFor={id}>Sort by</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value as SortOption)}>
        {(Object.keys(LABELS) as SortOption[]).map((option) => (
          <option key={option} value={option}>
            {LABELS[option]}
          </option>
        ))}
      </select>
    </div>
  );
}
