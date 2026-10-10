/**
 * Colours derived from other colours.
 *
 * Every one of these used to be a second literal written next to the first: an amber, and beside it
 * the darker amber its edge was drawn in. That works until the amber is a token, at which point
 * there is nothing to hand-pick a darker version of — a `var()` is opaque to the file using it.
 *
 * `color-mix` derives it instead, which is also the only form that keeps working when a theme
 * changes the colour underneath it: an outline stays a darker version of whatever hue the thing is
 * now wearing, without anyone repicking it.
 *
 * They live in the theme module rather than beside the components because a theme's own files need
 * them too — a mascot, a backdrop layer, anything deriving a shade from one of its own tokens.
 * `components/orbit.ts` re-exports all three, so the pages go on importing them from where they
 * always did.
 */

/** The same colour, thinned to `pct` of itself. For rules and edges that fade into the backdrop. */
export const fade = (colour: string, pct: number) =>
  `color-mix(in srgb, ${colour} ${pct}%, transparent)`;

/** The colour taken toward the floor. `pct` is how much of it survives. */
export const deepen = (colour: string, pct: number) =>
  `color-mix(in srgb, ${colour} ${pct}%, var(--w-void))`;

/** The colour taken toward the brightest text. `pct` is how much of it survives. */
export const lighten = (colour: string, pct: number) =>
  `color-mix(in srgb, ${colour} ${pct}%, var(--w-t100))`;
