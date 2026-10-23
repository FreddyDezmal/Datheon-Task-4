import { useRef, useState } from 'react';
import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { jsonResponse, products } from '../test/fixtures';
import type { CatalogQuery } from '../types/product';
import { useCatalogQuery } from './useCatalogQuery';
import { useDebouncedValue } from './useDebouncedValue';
import { useModalBehavior } from './useModalBehavior';
import { useProducts } from './useProducts';

describe('useProducts', () => {
  it('never lets a slow, superseded response overwrite a newer one', async () => {
    const pending = new Map<string, (r: Response) => void>();
    const fetchMock = vi.fn(
      (input: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((resolve, reject) => {
          const search = new URL(String(input)).searchParams.get('search') ?? '';
          pending.set(search, resolve);
          init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
        }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const query = (search: string): CatalogQuery => ({ search, category: 'all', sort: 'featured' });

    const { result, rerender } = renderHook(({ q }) => useProducts(q), { initialProps: { q: query('gold') } });
    rerender({ q: query('drive') });
    // The "drive" response arrives first, then the stale "gold" one.
    await act(async () => pending.get('drive')?.(jsonResponse({ items: [products[2]], count: 1 })));
    await act(async () => pending.get('gold')?.(jsonResponse({ items: [products[1]], count: 1 })));

    expect(result.current.status).toBe('success');
    expect(result.current.products.map((p) => p.title)).toEqual(['WD 2TB Portable Hard Drive']);
    // The first request was cancelled when the query changed.
    expect((fetchMock.mock.calls[0]?.[1] as RequestInit).signal?.aborted).toBe(true);
  });

  it('keeps the last good results when a later request fails, and retries on demand', async () => {
    let fail = false;
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        fail ? jsonResponse({ status: 502, title: 'Catalogue unavailable.', code: 'UPSTREAM_UNAVAILABLE' }, 502) : jsonResponse({ items: products, count: 4 }),
      ),
    );
    const { result, rerender } = renderHook(({ q }) => useProducts(q), {
      initialProps: { q: { search: '', category: 'all', sort: 'featured' } as CatalogQuery },
    });
    await waitFor(() => expect(result.current.status).toBe('success'));

    fail = true;
    rerender({ q: { search: 'x', category: 'all', sort: 'featured' } });
    await waitFor(() => expect(result.current.status).toBe('error'));
    expect(result.current.products).toHaveLength(4);
    expect(result.current.error?.code).toBe('UPSTREAM_UNAVAILABLE');

    fail = false;
    act(() => result.current.retry());
    await waitFor(() => expect(result.current.status).toBe('success'));
  });
});

describe('useCatalogQuery', () => {
  it('reads valid values from the URL and ignores unsafe or unknown ones', () => {
    window.history.replaceState(null, '', `/?q=${'a'.repeat(150)}&category=Bad%20Slug!&sort=cheapest`);
    const { result } = renderHook(() => useCatalogQuery());

    expect(result.current.query).toEqual({ search: 'a'.repeat(100), category: 'all', sort: 'featured' });
  });

  it('writes changes back to the URL without adding history entries, keeping unrelated parameters', () => {
    window.history.replaceState(null, '', '/?product=5');
    const before = window.history.length;
    const { result } = renderHook(() => useCatalogQuery());

    act(() => result.current.setCategory('jewelry'));
    act(() => result.current.setSort('price-asc'));
    expect(new URLSearchParams(window.location.search).get('category')).toBe('jewelry');
    expect(new URLSearchParams(window.location.search).get('sort')).toBe('price-asc');
    expect(new URLSearchParams(window.location.search).get('product')).toBe('5');

    act(() => result.current.reset());
    expect(window.location.search).toBe('?product=5');
    expect(window.history.length).toBe(before);
  });
});

describe('useDebouncedValue', () => {
  afterEach(() => vi.useRealTimers());

  it('only settles once typing pauses for the full delay', () => {
    vi.useFakeTimers();
    const { result, rerender } = renderHook(({ v }) => useDebouncedValue(v, 250), { initialProps: { v: 'd' } });

    rerender({ v: 'dr' });
    act(() => vi.advanceTimersByTime(200));
    rerender({ v: 'dri' });
    act(() => vi.advanceTimersByTime(200));
    expect(result.current).toBe('d');

    act(() => vi.advanceTimersByTime(50));
    expect(result.current).toBe('dri');
  });
});

/** A minimal dialog using the shared modal behaviour, opened by a real button. */
function ModalHarness({ onClose }: { onClose?: () => void }) {
  const [open, setOpen] = useState(false);
  const panel = useRef<HTMLDivElement>(null);
  const first = useRef<HTMLButtonElement>(null);
  const close = () => {
    onClose?.();
    setOpen(false);
  };
  useModalBehavior(open, close, panel, first);
  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>
        Open
      </button>
      {open && (
        <div ref={panel} role="dialog" aria-label="Harness" tabIndex={-1}>
          <button ref={first} type="button">
            First
          </button>
          <button type="button" disabled>
            Disabled
          </button>
          <input aria-label="Middle" />
          <button type="button" onClick={close}>
            Last
          </button>
        </div>
      )}
    </>
  );
}

describe('useModalBehavior', () => {
  it('moves focus in, wraps Tab in both directions, skips disabled controls and locks scrolling', async () => {
    const user = userEvent.setup();
    render(<ModalHarness />);
    await user.click(screen.getByRole('button', { name: 'Open' }));

    expect(screen.getByRole('button', { name: 'First' })).toHaveFocus();
    expect(document.body).toHaveClass('scroll-locked');

    await user.tab();
    expect(screen.getByRole('textbox', { name: 'Middle' })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Last' })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'First' })).toHaveFocus();
    await user.tab({ shift: true });
    expect(screen.getByRole('button', { name: 'Last' })).toHaveFocus();
  });

  it('closes on Escape, unlocks scrolling and returns focus to the button that opened it', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<ModalHarness onClose={onClose} />);
    const opener = screen.getByRole('button', { name: 'Open' });
    await user.click(opener);

    await user.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(document.body).not.toHaveClass('scroll-locked');
    expect(opener).toHaveFocus();
  });
});
