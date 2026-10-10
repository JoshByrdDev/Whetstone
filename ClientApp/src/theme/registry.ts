import type { Theme } from './tokens';
import { basketball } from './themes/basketball';
import { medieval } from './themes/medieval';
import { mountain } from './themes/mountain';
import { snow } from './themes/snow';
import { space } from './themes/space';

/**
 * Every theme the app knows about.
 *
 * Adding one is this file plus the theme itself: import it, put it in the list. The Settings
 * dropdown is built from `THEMES` rather than from a second hand-written list, so a theme that is
 * here is offered and a theme that is not cannot be selected.
 *
 * Order is the order they appear in the dropdown. The default goes first.
 */
export const THEMES: Theme[] = [space, medieval, mountain, snow, basketball];

export const DEFAULT_THEME = space;

/**
 * The theme a stored key names, or the default.
 *
 * Falling back rather than throwing is the point: a key can outlive the theme it named — a theme
 * removed from the build, or a preference synced from a newer version — and the honest answer to
 * "the theme you asked for is gone" is the default look, not a blank page.
 */
export function resolveTheme(key: string | null | undefined): Theme {
  return THEMES.find(t => t.key === key) ?? DEFAULT_THEME;
}
