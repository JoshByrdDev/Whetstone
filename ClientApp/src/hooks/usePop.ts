import { useEffect, useRef, useState } from 'react';

/** How long a popover takes to leave. Must outlast the pop-out keyframes in index.css. */
export const POP_MS = 120;

/**
 * The state behind `.orbit-pop` — an anchored panel that arrives and leaves rather than blinking.
 *
 * The mirror of {@link useVeil}, and the difference is who owns the unmount. A dialog is dropped by
 * the page that opened it, so it has to hold the close off; a popover is rendered by the component
 * that holds its own `open` flag, so it can simply stay mounted a little longer than the flag says.
 * That way round costs the caller nothing: every existing way of closing — a click outside, escape,
 * picking an item, a value going empty — keeps working untouched, because none of them are
 * intercepted.
 *
 *   const { show, fx } = usePop(open);
 *   {show && <div className="orbit-pop" data-fx={fx}>…</div>}
 */
export function usePop(open: boolean) {
  const [show, setShow] = useState(open);
  const [fx, setFx] = useState<'in' | 'out'>(open ? 'in' : 'out');

  useEffect(() => {
    if (open) { setShow(true); setFx('in'); return; }
    setFx('out');
    // Cleared if `open` comes back before it fires, so a menu reopened mid-fade is not closed a
    // moment later by the timer the previous close started.
    const id = window.setTimeout(() => setShow(false), POP_MS);
    return () => window.clearTimeout(id);
  }, [open]);

  return { show, fx };
}

/**
 * The last value this had while it was open.
 *
 * For a panel whose open-ness IS its content — a suggestion list is open precisely while it has
 * suggestions. Left to itself such a panel spends its whole exit empty, which is not a list
 * leaving, it is an empty box appearing. Holding the last contents keeps the thing that fades out
 * the thing that was there.
 */
export function useHeld<T>(value: T, open: boolean) {
  const held = useRef(value);
  if (open) held.current = value;
  return open ? value : held.current;
}
