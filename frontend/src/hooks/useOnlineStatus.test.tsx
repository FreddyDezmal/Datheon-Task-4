import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useOnlineStatus } from './useOnlineStatus';

afterEach(() => vi.restoreAllMocks());

const goOffline = () => {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
  act(() => {
    window.dispatchEvent(new Event('offline'));
  });
};
const goOnline = () => {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true);
  act(() => {
    window.dispatchEvent(new Event('online'));
  });
};

describe('useOnlineStatus', () => {
  it('follows the browser between offline and online', () => {
    const { result } = renderHook(() => useOnlineStatus());
    expect(result.current).toBe(true);

    goOffline();
    expect(result.current).toBe(false);

    goOnline();
    expect(result.current).toBe(true);
  });

  it('stops listening when unmounted', () => {
    const remove = vi.spyOn(window, 'removeEventListener');
    const { unmount } = renderHook(() => useOnlineStatus());

    unmount();

    expect(remove).toHaveBeenCalledWith('online', expect.any(Function));
    expect(remove).toHaveBeenCalledWith('offline', expect.any(Function));
  });
});
