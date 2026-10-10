import { useEffect, useState } from 'react';
import type { Theme } from './tokens';
import { themeVars } from './css';
import { DEFAULT_THEME, resolveTheme } from './registry';
import { parsePref, resolvePref, rollsOver } from './preference';
import type { ThemePref } from './preference';

/**
 * Applying a theme, remembering how it was chosen, and telling the app it changed.
 *
 * The preference lives in localStorage rather than the server config, for the same reason the
 * animated-background toggle does: it is a statement about the machine in front of you, not about
 * the system, and reading it synchronously means the first paint is already right. It also means
 * changing it takes effect now instead of on the next restart, which is the only sane behaviour
 * for a setting whose entire job is to change what you are looking at.
 *
 * Two things carry a theme:
 *
 *   * CSS custom properties on `<html>`, written by `apply`. Tailwind's classes and `index.css`
 *     both resolve through them, so the repaint is the browser's, not React's.
 *   * `current`, the theme object, for the handful of things a stylesheet cannot answer: the clip
 *     path that takes a size, the backdrop spec, the mascot, the palette a canvas needs in numbers.
 *
 * `current` is a module-level variable rather than a context because most of its readers are not
 * components — `orbit.ts` exports a bare `chamfer(r)` that a dozen files call inline. Keeping it
 * module-level means those call sites never had to change. What makes that safe is that every path
 * that changes it writes `current` *before* it fires the event that re-renders the tree, and
 * nothing in the app is memoised, so every reader is called again with the new value in place.
 *
 * Three of the four modes decide for themselves and change at midnight, so this also owns a clock —
 * see `armRollover`.
 */

const KEY = 'whetstone.theme';
/** Fired on the window when the theme changes, so anything reading it re-renders now. */
const EVENT = 'whetstone-theme-change';

/** The `<link>` and `<style>` this module owns. Reused across swaps rather than piled up. */
const FONT_LINK_ID = 'w-theme-fonts';
const EXTRA_CSS_ID = 'w-theme-css';

function read(): ThemePref {
  try {
    return parsePref(localStorage.getItem(KEY));
  } catch {
    return parsePref(null); // private mode, blocked storage: the default stands
  }
}

function write(p: ThemePref): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(p));
  } catch {
    /* the change still applies to this page; it just will not be remembered */
  }
}

/**
 * Resolve a preference against now, adopt the result, and write back a redraw.
 *
 * Returns whether the painted theme actually moved, so callers can skip repainting when it did not
 * — which is most of the time, since the two clocks that call this fire on a schedule rather than
 * on a change.
 *
 * `persist` is off for the boot and cross-tab cases, where the preference either came straight out
 * of storage or belongs to whichever tab wrote it; it is on wherever the change is this tab's own
 * doing. A redraw writes either way, because a random pick that is not written down is a different
 * theme on every reload — which is not "a theme a day" by any reading.
 */
function settle(next: ThemePref, persist: boolean): boolean {
  const { pref: settled, key } = resolvePref(next, new Date());
  const redrawn = settled !== next;
  pref = settled;
  if (redrawn || persist) write(settled);

  const theme = resolveTheme(key);
  if (theme === current) return false;
  current = theme;
  return true;
}

let pref: ThemePref = read();
let current: Theme = DEFAULT_THEME;

// Settled at import, before anything renders and before `bootTheme` paints. Doing it here rather
// than inside boot is what stops random drawing twice — a draw at import that boot then discarded
// and redid would be a wasted pick, and if the two disagreed, a visible flash of the first one.
// This is also where random notices the day turned over while the app was shut.
settle(pref, false);

/** The theme in force. Safe to call from anywhere, including outside React. */
export const currentTheme = (): Theme => current;

/** How that theme is being chosen. The Settings panel's copy of the truth. */
export const themePref = (): ThemePref => pref;

/**
 * A tracked letter-spacing, scaled by the theme.
 *
 * `track(.15)` is "the .15em this element was drawn with, as much of it as this theme wants". See
 * `type.track` in `tokens.ts` for why it is a multiplier and not a replacement.
 */
export const track = (em: number): string => `calc(var(--w-track, 1) * ${em}em)`;

/**
 * Write a theme onto the document.
 *
 * Called once at boot from `main.tsx` before React mounts, and again on every change. Everything it
 * touches is keyed by id and overwritten in place, so applying a theme twice is the same as
 * applying it once and there is no accumulation to clean up.
 */
export function apply(t: Theme): void {
  const root = document.documentElement;
  const vars = themeVars(t);
  for (const [name, value] of Object.entries(vars)) root.style.setProperty(name, value);
  // Not read by anything here — it is the hook a theme's own `css` block, or a future one-off rule,
  // uses to say "only under this theme" without needing a class on every element.
  root.setAttribute('data-theme', t.key);

  // The faces. Swapping the href rather than adding a second link means the previous theme's fonts
  // stop being fetched, which matters on the first load of a theme that uses none.
  let link = document.getElementById(FONT_LINK_ID) as HTMLLinkElement | null;
  if (t.fonts.href) {
    if (!link) {
      link = document.createElement('link');
      link.id = FONT_LINK_ID;
      link.rel = 'stylesheet';
      document.head.appendChild(link);
    }
    if (link.href !== t.fonts.href) link.href = t.fonts.href;
  } else {
    link?.remove();
  }

  // The theme's own sheet, if it has one. Torn down with the theme, so a rule one theme needed
  // cannot survive into the next.
  let extra = document.getElementById(EXTRA_CSS_ID);
  if (t.css) {
    if (!extra) {
      extra = document.createElement('style');
      extra.id = EXTRA_CSS_ID;
      document.head.appendChild(extra);
    }
    if (extra.textContent !== t.css) extra.textContent = t.css;
  } else {
    extra?.remove();
  }
}

/**
 * Settle a preference and tell the app.
 *
 * The one path every change goes through — Settings, another tab, and midnight. The event fires
 * whether or not the theme moved, because a preference can change without the look changing:
 * editing Thursday's rota entry on a Monday has to reach the panel drawing it, and reaches nothing
 * else.
 */
function refresh(next: ThemePref, persist: boolean): void {
  if (settle(next, persist)) apply(current);
  window.dispatchEvent(new Event(EVENT));
}

/**
 * The midnight clock.
 *
 * Random redraws at the start of each day and a schedule changes with the weekday, so a dashboard
 * left open overnight has to notice. One timer to just after the next local midnight, re-armed each
 * time it fires, rather than a poll: this happens once a day and the moment it happens is knowable.
 *
 * The visibility listener in `bootTheme` is the belt to that timer's braces. A `setTimeout` hours
 * out does not survive a laptop being shut — it fires late, or not until the tab is looked at again
 * — so returning to the tab re-resolves too. Both paths land in `refresh`, which repaints nothing
 * if the answer has not actually moved.
 *
 * Armed only for the modes that can move on their own. In manual mode there is no clock at all.
 */
let rollover: ReturnType<typeof setTimeout> | undefined;

function armRollover(): void {
  clearTimeout(rollover);
  rollover = undefined;
  if (!rollsOver(pref.mode)) return;
  const now = new Date();
  // Thirty seconds past, not on the stroke: a timer that lands a hair early would resolve against
  // yesterday's date and then sit there until tomorrow.
  const next = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 0, 0, 30);
  rollover = setTimeout(() => {
    refresh(pref, false);
    armRollover();
  }, next.getTime() - now.getTime());
}

/** Paint the settled preference and start its clock. Call before the first render. */
export function bootTheme(): void {
  apply(current);
  armRollover();
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden && rollsOver(pref.mode)) { refresh(pref, false); armRollover(); }
  });
}

/** Change how the theme is chosen: remember it, paint whatever it now means, and tell the app. */
export function setThemePref(next: ThemePref): void {
  refresh(next, true);
  armRollover();
}

/** Pick a theme outright, and go manual doing it. The shorthand the rest of the app uses. */
export const setTheme = (key: string): void => setThemePref({ ...pref, mode: 'manual', theme: key });

/**
 * The theme in force, tracked — including a change made in another tab, or at midnight.
 *
 * Mount this once, high enough that a change re-renders everything below it. `App` does, which is
 * what lets the rest of the app go on reading `currentTheme()` directly: by the time any of it runs
 * again, `current` has already been swapped.
 */
export function useTheme(): Theme {
  const [t, setT] = useState(current);
  useEffect(() => {
    // A change in another tab writes storage but not this tab's `pref`, so re-read rather than
    // trusting the variable — otherwise the two tabs would disagree until a reload.
    const sync = () => { refresh(read(), false); armRollover(); };
    const local = () => setT(current);
    window.addEventListener(EVENT, local);
    window.addEventListener('storage', sync);
    return () => {
      window.removeEventListener(EVENT, local);
      window.removeEventListener('storage', sync);
    };
  }, []);
  return t;
}

/** The preference itself, tracked the same way — for the Settings panel, which edits it. */
export function useThemePref(): ThemePref {
  const [p, setP] = useState(pref);
  useEffect(() => {
    const sync = () => setP(pref);
    window.addEventListener(EVENT, sync);
    return () => window.removeEventListener(EVENT, sync);
  }, []);
  return p;
}
