import type { Ref } from 'react';
import { useSession } from '../context/SessionContext';
import type { Category } from '../types/product';
import { CategoryNav } from './CategoryNav';
import { BagIcon, UserIcon } from './Icons';
import { SearchField } from './SearchField';

interface NavbarProps {
  search: string;
  onSearchChange: (value: string) => void;
  categories: readonly Category[];
  selectedCategory: string;
  onCategoryChange: (slug: string) => void;
  cartUnitCount: number;
  onCartOpen: () => void;
  onAccountOpen: () => void;
  onBrandClick: () => void;
  cartButtonRef?: Ref<HTMLButtonElement>;
}

export function Navbar({
  search,
  onSearchChange,
  categories,
  selectedCategory,
  onCategoryChange,
  cartUnitCount,
  onCartOpen,
  onAccountOpen,
  onBrandClick,
  cartButtonRef,
}: NavbarProps) {
  const { status, email } = useSession();
  const cartLabel = cartUnitCount === 1 ? 'Open cart, 1 item' : `Open cart, ${cartUnitCount} items`;
  const signedIn = status !== 'anonymous';
  const accountLabel = status === 'signed-in' && email ? `Your account, signed in as ${email}` : signedIn ? 'Your account' : 'Sign in';

  return (
    <header className="site-header">
      <div className="site-header__bar">
        <a
          className="brand"
          href="/"
          onClick={(e) => {
            e.preventDefault();
            onBrandClick();
          }}
        >
          <span className="brand__mark" aria-hidden="true">
            <BagIcon width={18} height={18} />
          </span>
          <span className="brand__name">Greenhaus</span>
        </a>

        <SearchField value={search} onChange={onSearchChange} />

        <div className="site-header__actions">
          <button
            type="button"
            className={`cart-button account-button${signedIn ? ' is-signed-in' : ''}`}
            onClick={onAccountOpen}
            aria-label={accountLabel}
            data-testid="account-button"
          >
            <UserIcon />
            <span className="cart-button__label">{signedIn ? 'Account' : 'Sign in'}</span>
            {signedIn && <span className="account-button__dot" aria-hidden="true" />}
          </button>
          <button ref={cartButtonRef} type="button" className="cart-button" onClick={onCartOpen} aria-label={cartLabel}>
            <BagIcon />
            <span className="cart-button__label">Cart</span>
            {cartUnitCount > 0 && (
              <span className="cart-button__badge" data-testid="cart-badge" aria-hidden="true">
                {cartUnitCount > 99 ? '99+' : cartUnitCount}
              </span>
            )}
          </button>
        </div>
      </div>
      <CategoryNav categories={categories} selected={selectedCategory} onSelect={onCategoryChange} />
    </header>
  );
}
