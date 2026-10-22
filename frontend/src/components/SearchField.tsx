import { useId } from 'react';
import { CloseIcon, SearchIcon } from './Icons';

interface SearchFieldProps {
  value: string;
  onChange: (value: string) => void;
}

export function SearchField({ value, onChange }: SearchFieldProps) {
  const id = useId();
  return (
    <form className="search" role="search" onSubmit={(e) => e.preventDefault()}>
      <label htmlFor={id} className="visually-hidden">
        Search products
      </label>
      <SearchIcon className="search__icon" />
      <input
        id={id}
        className="search__input"
        type="search"
        placeholder="Search products"
        value={value}
        maxLength={100}
        autoComplete="off"
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape' && value) {
            e.preventDefault();
            onChange('');
          }
        }}
      />
      {value && (
        <button type="button" className="search__clear" onClick={() => onChange('')} aria-label="Clear search">
          <CloseIcon width={16} height={16} />
        </button>
      )}
    </form>
  );
}
