import { useId, useRef } from 'react';
import { useCart } from '../context/CartContext';
import { useModalBehavior } from '../hooks/useModalBehavior';
import { CartLineItem } from './CartLineItem';
import { CloseIcon } from './Icons';
import { OrderSummary } from './OrderSummary';

interface CartDrawerProps {
  isOpen: boolean;
  onClose: () => void;
  onCheckout: () => void;
}

export function CartDrawer({ isOpen, onClose, onCheckout }: CartDrawerProps) {
  const { items, totals } = useCart();
  const panelRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const titleId = useId();

  useModalBehavior(isOpen, onClose, panelRef, closeRef);

  const unitLabel = totals.unitCount === 1 ? '1 item' : `${totals.unitCount} items`;

  return (
    <div className={`drawer${isOpen ? ' is-open' : ''}`} inert={!isOpen} data-testid="cart-drawer">
      <div className="drawer__backdrop" onClick={onClose} aria-hidden="true" />
      <div
        ref={panelRef}
        className="drawer__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-hidden={!isOpen}
        tabIndex={-1}
      >
        <header className="drawer__header">
          <h2 id={titleId} className="drawer__title">
            Your cart {totals.unitCount > 0 && <span className="drawer__count">{unitLabel}</span>}
          </h2>
          <button ref={closeRef} type="button" className="icon-button" onClick={onClose} aria-label="Close cart">
            <CloseIcon />
          </button>
        </header>

        {items.length === 0 ? (
          <div className="drawer__empty">
            <p className="drawer__empty-title">Your cart is empty</p>
            <p>Add something from the catalogue and it will show up here.</p>
            <button type="button" className="button button--primary" onClick={onClose}>
              Browse products
            </button>
          </div>
        ) : (
          <>
            <ul className="drawer__lines" aria-label="Items in your cart">
              {items.map((item) => (
                <CartLineItem key={item.productId} item={item} />
              ))}
            </ul>
            <footer className="drawer__footer">
              <OrderSummary totals={totals} />
              <p className="drawer__note">Prices are confirmed by the store when you check out.</p>
              <div className="drawer__actions">
                <button type="button" className="button button--primary button--large" onClick={onCheckout}>
                  Check out
                </button>
                <button type="button" className="button button--ghost" onClick={onClose}>
                  Continue shopping
                </button>
              </div>
            </footer>
          </>
        )}
      </div>
    </div>
  );
}
