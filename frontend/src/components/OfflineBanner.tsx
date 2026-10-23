import { useOnlineStatus } from '../hooks/useOnlineStatus';

/** A persistent notice while the browser has no connection. Announced politely, never steals focus. */
export function OfflineBanner() {
  const online = useOnlineStatus();
  return (
    <div className="offline-banner" role="status" aria-live="polite">
      {!online && (
        <p className="offline-banner__text">
          <strong>You're offline.</strong> Your cart is saved on this device. Products will reload and checkout will be
          available when you reconnect.
        </p>
      )}
    </div>
  );
}
