import { DEFAULT_THEME, THEMES } from './registry';

/**
 * Which theme is in force, and how that gets decided.
 *
 * Four answers, and the difference between them is only *when* the question is asked:
 *
 *   manual     you picked one; it stays picked
 *   random     one is drawn on entering the mode, and again at the start of each day
 *   cycle      the next one in the registry's order, at the start of each day
 *   schedule   the day of the week decides
 *
 * All four end at a theme key, which is the only thing the rest of the app ever sees. That is
 * deliberate: `runtime.ts` applies a theme and knows nothing about why it is that one, and this
 * file decides which one and knows nothing about how a theme is painted.
 *
 * The other thing worth stating plainly: three of the four modes change on their own, at midnight,
 * with the app possibly sitting open. So resolving is a function of the preference *and the current
 * time*, and it can hand back a preference that has changed — random and cycle both remember which
 * day the theme showing was chosen for, so moving on is a write as well as a read.
 */

export type ThemeMode = 'manual' | 'random' | 'cycle' | 'schedule';

/** The modes a stored preference may name. Anything else in storage reads as manual. */
const MODES: readonly ThemeMode[] = ['manual', 'random', 'cycle', 'schedule'];

/** Sunday first: the order `Date.getDay()` returns, and the order a week is read in. */
export const WEEKDAYS = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'] as const;
export type Weekday = (typeof WEEKDAYS)[number];

export const WEEKDAY_LABEL: Record<Weekday, string> = {
  sun: 'Sunday', mon: 'Monday', tue: 'Tuesday', wed: 'Wednesday',
  thu: 'Thursday', fri: 'Friday', sat: 'Saturday',
};

export interface ThemePref {
  mode: ThemeMode;
  /**
   * The theme most recently in force, whichever mode put it there.
   *
   * Manual writes it when you pick. Random writes it when it draws. Schedule writes it as the rota
   * rolls, and cycle writes it each time it steps. Keeping it true in every mode rather than letting
   * it go stale outside manual is what makes two other things correct without either having to ask:
   * switching modes keeps the look you were already on, and a draw knows what it has to avoid.
   */
  theme: string;
  /**
   * Random and cycle: the local day `theme` was chosen for. A different day means choose again.
   *
   * One field for both because they want the same thing from it — "has the day turned over since
   * this was decided" — and differ only in what they do about it. Absent means the mode has not
   * decided yet, which is how entering cycle starts where you already are.
   */
  drawnOn?: string;
  /** Schedule only: one theme per day of the week. */
  week?: Record<Weekday, string>;
}

/**
 * The local calendar day, as `YYYY-MM-DD`.
 *
 * Built by hand rather than sliced off `toISOString`, which converts to UTC first and would roll
 * the day over at the wrong moment for anyone not on it — a redraw at 7pm is not "the start of a
 * day" in any sense the person looking at the screen would recognise.
 */
export const dayKey = (d: Date): string =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/**
 * Draw a theme, avoiding the one already showing.
 *
 * Uniform over everything else rather than over everything, because a daily shuffle that can land
 * on the same theme two days running is a daily shuffle that spends half its days doing nothing —
 * with two themes installed, a fair coin means the look changes every other day at best. Excluding
 * the current pick is what makes the mode do the thing it is for. With one theme installed there is
 * nothing to exclude, and random is a no-op.
 */
export function drawTheme(exclude?: string): string {
  const pool = THEMES.length > 1 ? THEMES.filter(t => t.key !== exclude) : THEMES;
  if (pool.length === 0) return DEFAULT_THEME.key;
  return pool[Math.floor(Math.random() * pool.length)].key;
}

/**
 * The theme after `key` in the registry's order, wrapping at the end.
 *
 * A key that names no installed theme starts the cycle at the beginning rather than stalling — the
 * same reasoning as `resolveTheme`'s fallback: a preference can outlive the theme it named, and the
 * honest answer to "the theme you were on is gone" is the first one, not no movement forever.
 *
 * With one theme installed this returns that theme and the mode is a no-op, exactly as random is.
 */
export function nextTheme(key: string): string {
  if (THEMES.length === 0) return DEFAULT_THEME.key;
  const i = THEMES.findIndex(t => t.key === key);
  return THEMES[i < 0 ? 0 : (i + 1) % THEMES.length].key;
}

/** Every day of the week set to one theme. The shape a schedule starts life in. */
export const weekOf = (key: string): Record<Weekday, string> =>
  Object.fromEntries(WEEKDAYS.map(d => [d, key])) as Record<Weekday, string>;

/** The preference a fresh machine starts with. */
export const defaultPref = (): ThemePref => ({ mode: 'manual', theme: DEFAULT_THEME.key });

/**
 * What theme this preference means right now, and the preference as it stands afterwards.
 *
 * `pref` comes back changed when a day-driven mode has moved on, or when a rota's answer differs
 * from what was last in force; the caller persists it in those cases and in no other. Returning it
 * rather than writing here keeps this function pure enough to reason about — the same preference
 * and the same instant always give the same answer.
 */
export function resolvePref(pref: ThemePref, now: Date): { pref: ThemePref; key: string } {
  switch (pref.mode) {
    case 'random': {
      const today = dayKey(now);
      if (pref.drawnOn === today) return { pref, key: pref.theme };
      const key = drawTheme(pref.theme);
      return { pref: { ...pref, theme: key, drawnOn: today }, key };
    }
    case 'cycle': {
      const today = dayKey(now);
      if (pref.drawnOn === today) return { pref, key: pref.theme };
      // No day recorded means the mode has just been entered. The cycle starts where you already
      // are and moves tomorrow, rather than stepping the moment you pick it: unlike random there is
      // nothing to shuffle away from, so a jump on entry would be a change with no purpose. It also
      // makes the order legible — the panel can say what today is and what tomorrow will be, and
      // both stay true.
      if (!pref.drawnOn) return { pref: { ...pref, drawnOn: today }, key: pref.theme };
      // Exactly one step, however many days were missed. A cycle derived from the calendar instead
      // would skip past themes nobody was there to see, and could land on the same one you left
      // days ago; stepping once guarantees the look has changed every time you come back on a new
      // day, which is the whole of what the mode is for.
      const key = nextTheme(pref.theme);
      return { pref: { ...pref, theme: key, drawnOn: today }, key };
    }
    case 'schedule': {
      const key = pref.week?.[WEEKDAYS[now.getDay()]] ?? pref.theme;
      // Write the rota's answer back into `theme`, so the field goes on meaning "what is showing"
      // outside manual mode too. Without this it holds whatever was picked before the rota was set
      // up, and the two things that read it both get the wrong answer: switching to manual would
      // jump to a theme you have not seen in days, and a random draw would try to avoid it while
      // happily drawing the one actually on screen.
      return key === pref.theme ? { pref, key } : { pref: { ...pref, theme: key }, key };
    }
    default:
      return { pref, key: pref.theme };
  }
}

/** Whether this mode's answer can change on its own while the app sits open. */
export const rollsOver = (mode: ThemeMode): boolean => mode !== 'manual';

/** Parse whatever is in storage, including the bare theme key this used to be. */
export function parsePref(raw: string | null): ThemePref {
  if (!raw) return defaultPref();
  // Before there were modes, this key held nothing but a theme key. Read it as a manual choice
  // rather than dropping the setting and putting a machine back on the default look.
  if (!raw.startsWith('{')) return { mode: 'manual', theme: raw };
  try {
    const p = JSON.parse(raw) as Partial<ThemePref>;
    const mode: ThemeMode = MODES.includes(p.mode as ThemeMode) ? p.mode as ThemeMode : 'manual';
    return {
      mode,
      theme: typeof p.theme === 'string' ? p.theme : DEFAULT_THEME.key,
      drawnOn: typeof p.drawnOn === 'string' ? p.drawnOn : undefined,
      week: p.week && typeof p.week === 'object' ? p.week as Record<Weekday, string> : undefined,
    };
  } catch {
    return defaultPref(); // hand-edited, half-written, or from a version that stored something else
  }
}
