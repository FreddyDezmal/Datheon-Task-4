import { useCallback, useEffect, useState } from 'react';
import { useSession } from '../context/SessionContext';
import { fetchMyOrders, fetchOrder } from '../services/accountService';
import { ApiError, isAbortError } from '../services/apiClient';
import type { OrderDetails, OrderSummary } from '../types/account';
import { formatMajor } from '../utils/money';

type Load<T> = { status: 'loading' } | { status: 'success'; data: T } | { status: 'error'; message: string };

const errorMessage = (error: unknown) =>
  error instanceof ApiError ? error.message : 'Something went wrong loading your orders.';

/** The signed-in shopper's orders, newest first, each expandable to its saved lines and totals. */
export function MyOrders({ locale }: { locale: string }) {
  const { withAccessToken } = useSession();
  const [orders, setOrders] = useState<Load<OrderSummary[]>>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [expanded, setExpanded] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setOrders({ status: 'loading' });
    withAccessToken((token) => fetchMyOrders(token, controller.signal))
      .then((data) => setOrders({ status: 'success', data }))
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        // SESSION_EXPIRED signs the shopper out, which replaces this view with the sign-in form.
        setOrders({ status: 'error', message: errorMessage(error) });
      });
    return () => controller.abort();
  }, [withAccessToken, attempt]);

  const date = (iso: string) => new Date(iso).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' });

  return (
    <section className="my-orders" aria-labelledby="my-orders-heading" aria-busy={orders.status === 'loading'}>
      <h3 id="my-orders-heading" className="my-orders__heading">
        Orders
      </h3>

      {orders.status === 'loading' && (
        <p className="my-orders__status" role="status">
          <span className="spinner" aria-hidden="true" /> Loading your orders…
        </p>
      )}

      {orders.status === 'error' && (
        <div className="alert alert--error" role="alert">
          <p>{orders.message}</p>
          <button type="button" className="button button--secondary" onClick={() => setAttempt((n) => n + 1)}>
            Try again
          </button>
        </div>
      )}

      {orders.status === 'success' && orders.data.length === 0 && (
        <div className="my-orders__empty">
          <p className="my-orders__empty-title">No orders yet</p>
          <p>Orders you place while signed in will appear here. Orders placed as a guest aren’t linked to an account.</p>
        </div>
      )}

      {orders.status === 'success' && orders.data.length > 0 && (
        <ul className="my-orders__list">
          {orders.data.map((order) => {
            const isOpen = expanded === order.orderReference;
            const detailsId = `order-${order.orderReference}`;
            return (
              <li key={order.orderReference} className="my-orders__item">
                <button
                  type="button"
                  className="my-orders__row"
                  aria-expanded={isOpen}
                  aria-controls={detailsId}
                  onClick={() => setExpanded(isOpen ? null : order.orderReference)}
                >
                  <span className="my-orders__ref">{order.orderReference}</span>
                  <span className="my-orders__meta">
                    {date(order.createdAt)} · {order.itemCount} {order.itemCount === 1 ? 'item' : 'items'}
                  </span>
                  <span className="my-orders__total">{formatMajor(order.total, order.currency, locale)}</span>
                  <span className="my-orders__chevron" aria-hidden="true" />
                </button>
                {isOpen && <OrderLines id={detailsId} reference={order.orderReference} locale={locale} />}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function OrderLines({ id, reference, locale }: { id: string; reference: string; locale: string }) {
  const { withAccessToken } = useSession();
  const [order, setOrder] = useState<Load<OrderDetails>>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  useEffect(() => {
    const controller = new AbortController();
    setOrder({ status: 'loading' });
    withAccessToken((token) => fetchOrder(token, reference, controller.signal))
      .then((data) => setOrder({ status: 'success', data }))
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setOrder({
          status: 'error',
          message: error instanceof ApiError && error.status === 404 ? 'This order could not be found.' : errorMessage(error),
        });
      });
    return () => controller.abort();
  }, [withAccessToken, reference, attempt]);

  if (order.status === 'loading') {
    return (
      <div id={id} className="my-orders__details" role="status">
        <span className="spinner" aria-hidden="true" /> Loading order…
      </div>
    );
  }
  if (order.status === 'error') {
    return (
      <div id={id} className="my-orders__details alert alert--error" role="alert">
        <p>{order.message}</p>
        <button type="button" className="button button--secondary" onClick={retry}>
          Try again
        </button>
      </div>
    );
  }

  const { lines, totals } = order.data;
  const money = (amount: number) => formatMajor(amount, totals.currency, locale);
  return (
    <div id={id} className="my-orders__details">
      <table className="confirmation__table">
        <caption className="visually-hidden">Items in order {reference}</caption>
        <thead>
          <tr>
            <th scope="col">Item</th>
            <th scope="col" className="num">
              Qty
            </th>
            <th scope="col" className="num">
              Total
            </th>
          </tr>
        </thead>
        <tbody>
          {lines.map((line) => (
            <tr key={line.productId}>
              <td>{line.title}</td>
              <td className="num">{line.quantity}</td>
              <td className="num">{money(line.lineTotal)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th scope="row" colSpan={2}>
              Subtotal
            </th>
            <td className="num">{money(totals.subtotal)}</td>
          </tr>
          <tr>
            <th scope="row" colSpan={2}>
              Tax
            </th>
            <td className="num">{money(totals.tax)}</td>
          </tr>
          <tr>
            <th scope="row" colSpan={2}>
              Shipping
            </th>
            <td className="num">{totals.shipping === 0 ? 'Free' : money(totals.shipping)}</td>
          </tr>
          <tr className="confirmation__total">
            <th scope="row" colSpan={2}>
              Total
            </th>
            <td className="num">{money(totals.total)}</td>
          </tr>
        </tfoot>
      </table>
      <p className="my-orders__sent">
        Placed for {order.data.customerName} ({order.data.customerEmail}). Prices are as they were at checkout.
      </p>
    </div>
  );
}
