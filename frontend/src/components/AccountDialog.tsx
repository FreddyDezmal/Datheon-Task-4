import { useEffect, useId, useRef, useState } from 'react';
import { useSession } from '../context/SessionContext';
import { usePricingRules } from '../context/StoreConfigContext';
import { useToast } from '../context/ToastContext';
import { useModalBehavior } from '../hooks/useModalBehavior';
import { AuthForm, type AuthMode } from './AuthForm';
import { CloseIcon } from './Icons';
import { MyOrders } from './MyOrders';

interface AccountDialogProps {
  isOpen: boolean;
  onClose: () => void;
  /**
   * Called after signing in or creating an account. Defaults to staying open on the account view;
   * checkout passes a callback that returns the shopper to their order.
   */
  onSignedIn?: () => void;
}

/** Sign in / create an account when signed out; the account's orders and sign-out when signed in. */
export function AccountDialog({ isOpen, onClose, onSignedIn }: AccountDialogProps) {
  const session = useSession();
  const { locale } = usePricingRules();
  const { notify } = useToast();
  const [mode, setMode] = useState<AuthMode>('sign-in');
  const panelRef = useRef<HTMLDivElement>(null);
  const emailInputRef = useRef<HTMLInputElement>(null);
  const titleId = useId();

  useEffect(() => {
    if (isOpen) setMode('sign-in');
  }, [isOpen]);

  useModalBehavior(isOpen, onClose, panelRef, session.status === 'anonymous' ? emailInputRef : undefined);

  // When a session ends while this is open (e.g. it expired), move focus to the sign-in form.
  const previousStatus = useRef(session.status);
  useEffect(() => {
    if (isOpen && previousStatus.current === 'signed-in' && session.status === 'anonymous') {
      requestAnimationFrame(() => emailInputRef.current?.focus());
    }
    previousStatus.current = session.status;
  }, [isOpen, session.status]);

  const title = session.status !== 'anonymous' ? 'Your account' : mode === 'register' ? 'Create an account' : 'Sign in';

  // No toast here: the dialog (or the checkout banner) already shows the signed-in state, and on
  // small screens a toast would sit over the buttons the shopper reaches for next.
  const handleSuccess = () => {
    if (onSignedIn) onSignedIn();
    // The form (and the focused button) is replaced by the account view: keep focus inside the dialog.
    else requestAnimationFrame(() => panelRef.current?.focus());
  };

  const handleSignOut = () => {
    session.signOut();
    notify('You have signed out.', 'info', 'session');
    onClose();
  };

  return (
    <div className={`dialog${isOpen ? ' is-open' : ''}`} inert={!isOpen}>
      <div className="dialog__backdrop" onClick={onClose} aria-hidden="true" />
      <div
        ref={panelRef}
        className="dialog__panel dialog__panel--narrow"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-hidden={!isOpen}
        tabIndex={-1}
      >
        <header className="dialog__header">
          <h2 id={titleId} className="dialog__title">
            {title}
          </h2>
          <button type="button" className="icon-button" onClick={onClose} aria-label="Close">
            <CloseIcon />
          </button>
        </header>

        <div className="dialog__body">
          {session.status === 'restoring' && (
            <p className="my-orders__status" role="status">
              <span className="spinner" aria-hidden="true" /> Checking your session…
            </p>
          )}

          {session.status === 'anonymous' && isOpen && (
            <AuthForm mode={mode} onModeChange={setMode} onSuccess={handleSuccess} emailInputRef={emailInputRef} />
          )}

          {session.status === 'signed-in' && (
            <div className="account">
              <div className="account__identity">
                <p>
                  Signed in as <strong data-testid="account-email">{session.email}</strong>
                </p>
                <button type="button" className="button button--secondary" onClick={handleSignOut}>
                  Sign out
                </button>
              </div>
              {isOpen && <MyOrders locale={locale} />}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
