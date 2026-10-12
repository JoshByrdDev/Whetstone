import { useSyncExternalStore } from 'react';

/**
 * The width below which a page is being read on a phone rather than at a desk.
 *
 * One number for the whole client, for the same reason the motion layer has one curve: a board that
 * folds at 640 beside a header that folds at 720 is a strip of widths where the page is half of
 * each. Matches Tailwind's `sm`, so a class written `sm:` and a style chosen by {@link useNarrow}
 * change over on the same pixel.
 */
export const NARROW_PX = 640;

const QUERY = `(max-width: ${NARROW_PX - 1}px)`;

function subscribe(onChange: () => void) {
  const mq = window.matchMedia(QUERY);
  mq.addEventListener('change', onChange);
  return () => mq.removeEventListener('change', onChange);
}

/**
 * True on a phone-width viewport.
 *
 * A hook rather than a media query because most of this client is laid out in inline styles, which
 * a stylesheet cannot reach - and because the phone view is the same components arranged
 * differently, not a second set of them. A component asks this, then stacks what it would have put
 * side by side. Anything that can be said in a class should still be said in a class.
 */
export function useNarrow(): boolean {
  return useSyncExternalStore(subscribe, () => window.matchMedia(QUERY).matches, () => false);
}
