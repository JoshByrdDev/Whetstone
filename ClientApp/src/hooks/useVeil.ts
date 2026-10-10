import { useCallback, useEffect, useRef, useState } from 'react';

/** How long a dismissal takes. Must outlast the veil-out/panel-out keyframes in index.css, or the
 *  overlay unmounts mid-fade. */
export const CLOSE_MS = 180;

/**
 * The state behind `.orbit-veil` — an overlay that arrives and leaves rather than blinking.
 *
 * An exit animation only exists if something outlives it, and the page that opened an overlay
 * unmounts it the moment `onClose` fires. So the overlay holds its own unmount off: flip to `out`,
 * let the veil play, and only then tell the page. That is the whole of it — `fx` goes on the fixed
 * container as `data-fx`, and every way out goes through `dismiss` (or `leave`, for an exit that
 * ends somewhere other than closed, like a form that just submitted).
 *
 * One hook per mounted overlay. A panel that stays mounted while it is hidden — SkillsPanel's two
 * documents were written that way — needs splitting into a component that is rendered only while
 * open, or it re-opens already faded out with its timer spent.
 */
export function useVeil(onClose: () => void) {
  const [fx, setFx] = useState<'in' | 'out'>('in');
  const leaving = useRef<number | null>(null);
  useEffect(() => () => { if (leaving.current) window.clearTimeout(leaving.current); }, []);

  // First call wins. A second one - Escape while the click is still animating - would otherwise
  // queue a second timer and fire whatever it was given after the overlay has already gone.
  const leave = useCallback((then: () => void) => {
    if (leaving.current) return;
    setFx('out');
    leaving.current = window.setTimeout(then, CLOSE_MS);
  }, []);

  const dismiss = useCallback(() => leave(onClose), [leave, onClose]);
  return { fx, leave, dismiss };
}
