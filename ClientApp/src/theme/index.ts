/**
 * The theme system's front door.
 *
 * Import from `../theme`, not from the files inside it — the split between `css`, `runtime`,
 * `registry`, `preference`, `mix` and `mascot` is an implementation detail, and the one thing that
 * must not drift is which of these names the rest of the app is allowed to know about.
 */
export type { Theme, TierName, Glass, Backdrop, BackdropLayer, Motion, Shape, Vec3 } from './tokens';
export type { Mascot, MascotProps, MascotPalette, Mood } from './mascot';
export { agentPalette } from './mascot';
export { fade, deepen, lighten } from './mix';
export { THEMES, DEFAULT_THEME, resolveTheme } from './registry';
export type { ThemePref, ThemeMode, Weekday } from './preference';
export { WEEKDAYS, WEEKDAY_LABEL, weekOf, resolvePref, nextTheme } from './preference';
export {
  currentTheme, themePref, useTheme, useThemePref,
  setTheme, setThemePref, bootTheme, track,
} from './runtime';
