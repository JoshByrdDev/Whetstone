import type { CSSProperties } from 'react';
import type { Phase, PhaseKind } from '../types/tickets';
import { currentTheme } from '../theme';
// Re-exported, not defined here: these derive one colour from another and a theme's own files need
// them too, so they live in the theme module. The pages go on importing them from where they were.
export { fade, deepen, lighten } from '../theme';

/**
 * The design tokens the board and everything downstream of it are drawn from.
 *
 * Every ticket wears a tier, and the tier is derived from the phase it is in rather than stored:
 * one colour, one name, one wash, used identically by the row shell, its chevron on the track, the
 * station node on the arc above it and the slide-over it opens. Nothing else on the board picks a
 * colour for a ticket, so a phase can never disagree with itself between two places on screen.
 *
 * What used to be literal here is now a reference into the active theme, and the split between the
 * two forms below is not arbitrary:
 *
 *   * Anything with a fixed value — the six tier colours, the three faces, the three fixed
 *     silhouettes — is a `var()` string. The browser resolves it at paint time, so a theme swap
 *     repaints these without React rendering anything. That also means these stayed plain
 *     constants and not one call site had to change.
 *   * `chamfer` and `chamferRow` take a size, so no fixed variable can hold them. They read the
 *     theme at call time instead, which is safe because a theme change re-renders the tree from
 *     `App` down and nothing in this app is memoised.
 */

export type Tier = 'blocked' | 'gate' | 'active' | 'queue' | 'backlog' | 'done';

export interface TierToken {
  /** The one colour. Borders, glows, chevrons and the slide-over's edge all read this. */
  c: string;
  /** Translucent wash for the row's fill — the colour at the weight a background can carry. */
  bg: string;
}

/**
 * A tier is a colour, not a name. What the chip says is the phase the ticket is in — the tier only
 * decides what colour it says it in, so the row, its chevron and its panel stay visibly one object.
 */
export const TIER: Record<Tier, TierToken> = {
  blocked: { c: 'var(--w-blocked)', bg: 'var(--w-blocked-w)' },
  gate:    { c: 'var(--w-gate)',    bg: 'var(--w-gate-w)' },
  active:  { c: 'var(--w-active)',  bg: 'var(--w-active-w)' },
  queue:   { c: 'var(--w-queue)',   bg: 'var(--w-queue-w)' },
  backlog: { c: 'var(--w-backlog)', bg: 'var(--w-backlog-w)' },
  done:    { c: 'var(--w-done)',    bg: 'var(--w-done-w)' },
};

/** Blocked outranks the phase kind: a stopped ticket reads as stopped wherever it stopped. */
export function tierOf(phase: Phase, kind: PhaseKind | undefined): Tier {
  if (phase === 'Blocked') return 'blocked';
  switch (kind) {
    case 'gate': return 'gate';
    case 'active': return 'active';
    case 'terminal': return 'done';
    case 'backlog': return 'backlog';
    default: return 'queue';
  }
}

export const tierToken = (phase: Phase, kind: PhaseKind | undefined) => TIER[tierOf(phase, kind)];

/**
 * The tier's colour in channels — `224 86 47` — for the places that have to mix their own alpha.
 *
 * `rgb(var(--w-blocked-rgb) / .4)` is the only way to get four-tenths of a tier colour when the
 * colour itself is a variable: you cannot append an alpha to a `var()` that already resolved to a
 * whole colour, which is exactly the problem the three-variable emission in `theme/css.ts` exists
 * to solve.
 */
export const tierRgb = (tier: Tier) => `var(--w-${tier}-rgb)`;

/** The corner treatment everything on this board is cut from, at `r` px. */
export const chamfer = (r: number) => currentTheme().shape.cut(r);

/** Cut on the top-left and bottom-right — the row shell's silhouette. */
export const chamferRow = (r: number) => currentTheme().shape.cutRow(r);

/** Six-sided hex, used for station cores and the slide-over's tier glyph. */
export const HEX = 'var(--w-hex)';

/** The diamond a ticket is drawn as, everywhere it appears on a track. */
export const DIAMOND = 'var(--w-diamond)';

/** A ship: the arrowhead parked above a station node. */
export const SHIP = 'var(--w-ship)';

/**
 * How a glyph is fitted, for the themes that answer with artwork rather than with a clip path.
 *
 * Always `contain` and always centred, which is what makes a square drawing safe in an oblong box:
 * the marker's box is not square, and a mask stretched to fill it would hand every theme a slightly
 * squashed version of its own icon. Contained, the drawing keeps its proportions and the spare
 * millimetre goes to the box.
 */
const MASK_FIT: CSSProperties = {
  WebkitMaskSize: 'contain', maskSize: 'contain',
  WebkitMaskRepeat: 'no-repeat', maskRepeat: 'no-repeat',
  WebkitMaskPosition: 'center', maskPosition: 'center',
};

/**
 * The station glyph, as a style to spread onto whatever is painting it.
 *
 * Both properties every time, because only one of them is ever a value: a theme that clips leaves
 * the mask at `none`, a theme that masks leaves the clip path at `none`, and `none` does nothing
 * to either. So the site that draws a station never asks which kind of theme it is under.
 *
 * Spread it *before* the background — it names no colour, and a spread that came after would still
 * be a spread that could.
 */
export const HEX_GLYPH: CSSProperties = {
  clipPath: HEX,
  WebkitMaskImage: 'var(--w-hex-mask)', maskImage: 'var(--w-hex-mask)',
  ...MASK_FIT,
};

/** The marker above a station, on the same terms. */
export const SHIP_GLYPH: CSSProperties = {
  clipPath: SHIP,
  WebkitMaskImage: 'var(--w-ship-mask)', maskImage: 'var(--w-ship-mask)',
  ...MASK_FIT,
};

/**
 * The square the marker above a station is drawn in.
 *
 * Square, and a little larger than the arrowhead the board started with, because four of the five
 * themes now park a *thing* up there — a ball, a flake, a gust — rather than a chevron. An oblong
 * box shears a ball into an egg, and eleven pixels is the point at which a stroked drawing stops
 * breaking into dashes. Here rather than in a theme: it is the marker's box, and where things sit
 * is the layout's business.
 */
export const SHIP_PX = 11;

// Fonts. Display type is labels — headings, tabs, buttons; body is prose; mono is anything
// countable. Which faces those are is the theme's business.
export const DISPLAY = 'var(--w-font-display)';
export const BODY = 'var(--w-font-body)';
export const MONO = 'var(--w-font-mono)';

/**
 * `textTransform` for display type: shouted under a HUD face, left alone under one with capitals.
 *
 * Cast once, here, rather than at each of the forty places that used to write `'uppercase'`.
 * csstype types `textTransform` as a closed union of the keywords, which is right for a stylesheet
 * and wrong for a variable — every CSS property accepts `var()`, and the type has no way to say so.
 */
export const CAPS = 'var(--w-caps)' as CSSProperties['textTransform'];
