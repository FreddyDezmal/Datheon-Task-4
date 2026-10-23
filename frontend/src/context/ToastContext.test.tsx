import { act, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ToastProvider, useToast } from './ToastContext';

let api: ReturnType<typeof useToast>;
function Capture() {
  api = useToast();
  return null;
}

const renderToasts = () =>
  render(
    <ToastProvider>
      <Capture />
    </ToastProvider>,
  );

// The text of each toast, in order (the × on the close button is not a message).
const messages = () =>
  within(screen.getByRole('status'))
    .queryAllByText(/./)
    .map((n) => n.textContent)
    .filter((t) => t !== '×');

describe('toasts', () => {
  afterEach(() => vi.useRealTimers());

  it('announces messages in a polite live region', () => {
    renderToasts();
    act(() => api.notify('Added Shirt to your cart.', 'success'));

    const region = screen.getByRole('status');
    expect(region).toHaveAttribute('aria-live', 'polite');
    expect(region).toHaveTextContent('Added Shirt to your cart.');
  });

  it('replaces a toast with the same key instead of stacking, and keeps at most three', () => {
    renderToasts();
    act(() => {
      api.notify('Added A.', 'success', 'cart-add');
      api.notify('Added B.', 'success', 'cart-add');
    });
    expect(messages()).toEqual(['Added B.']);

    act(() => {
      api.notify('one');
      api.notify('two');
      api.notify('three');
    });
    // The oldest toast makes way for the newest.
    expect(messages()).toEqual(['one', 'two', 'three']);
  });

  it('keeps errors on screen longer than confirmations', () => {
    vi.useFakeTimers();
    renderToasts();
    act(() => {
      api.notify('Saved.', 'success');
      api.notify('Something failed.', 'error');
    });

    act(() => vi.advanceTimersByTime(4000));
    expect(messages()).toEqual(['Something failed.']);
    act(() => vi.advanceTimersByTime(3000));
    expect(messages()).toEqual([]);
  });

  it('dismisses by key and by the close button', () => {
    renderToasts();
    act(() => {
      api.notify('Added A.', 'success', 'cart-add');
      api.notify('Signed out.', 'info', 'session');
    });

    act(() => api.dismissKey('cart-add'));
    expect(messages()).toEqual(['Signed out.']);

    act(() => screen.getByRole('button', { name: 'Dismiss notification' }).click());
    expect(messages()).toEqual([]);
  });
});
