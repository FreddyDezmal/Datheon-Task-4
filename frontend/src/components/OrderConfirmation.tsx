import type { CheckoutConfirmation } from '../types/checkout';
import { formatMajor } from '../utils/money';
import { CheckIcon } from './Icons';

interface OrderConfirmationProps {
  confirmation: CheckoutConfirmation;
  locale: string;
  onDone: () => void;
}

/** Shows the server's confirmation. All amounts here come from the server, not the cart. */
export function OrderConfirmation({ confirmation, locale, onDone }: OrderConfirmationProps) {
  const { totals } = confirmation;
  const money = (amount: number) => formatMajor(amount, totals.currency, locale);
  const placedAt = new Date(confirmation.createdAt).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' });

  return (
    <div className="confirmation">
      <div className="confirmation__hero">
        <span className="confirmation__icon" aria-hidden="true">
          <CheckIcon width={28} height={28} />
        </span>
        <div>
          <p className="confirmation__lead">Thanks, {confirmation.customerName}.</p>
          <p>
            Your order reference is{' '}
            <strong className="confirmation__reference" data-testid="order-reference">
              {confirmation.orderReference}
            </strong>
            .
          </p>
          <p className="confirmation__meta">
            Placed {placedAt} for {confirmation.customerEmail}
          </p>
        </div>
      </div>

      <table className="confirmation__table">
        <caption className="visually-hidden">Items in order {confirmation.orderReference}</caption>
        <thead>
          <tr>
            <th scope="col">Item</th>
            <th scope="col" className="num">
              Qty
            </th>
            <th scope="col" className="num">
              Price
            </th>
            <th scope="col" className="num">
              Total
            </th>
          </tr>
        </thead>
        <tbody>
          {confirmation.lines.map((line) => (
            <tr key={line.productId}>
              <td>{line.title}</td>
              <td className="num">{line.quantity}</td>
              <td className="num">{money(line.unitPrice)}</td>
              <td className="num">{money(line.lineTotal)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th scope="row" colSpan={3}>
              Subtotal
            </th>
            <td className="num">{money(totals.subtotal)}</td>
          </tr>
          <tr>
            <th scope="row" colSpan={3}>
              Tax
            </th>
            <td className="num">{money(totals.tax)}</td>
          </tr>
          <tr>
            <th scope="row" colSpan={3}>
              Shipping
            </th>
            <td className="num">{totals.shipping === 0 ? 'Free' : money(totals.shipping)}</td>
          </tr>
          <tr className="confirmation__total">
            <th scope="row" colSpan={3}>
              Total
            </th>
            <td className="num" data-testid="confirmation-total">
              {money(totals.total)}
            </td>
          </tr>
        </tfoot>
      </table>

      {confirmation.isSimulated && <p className="confirmation__notice">{confirmation.notice}</p>}

      <button type="button" className="button button--primary button--large" onClick={onDone}>
        Continue shopping
      </button>
    </div>
  );
}
