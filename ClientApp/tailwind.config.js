/** @type {import('tailwindcss').Config} */

// Every colour, face and radius in this file is a reference, not a value.
//
// The app used to be one look, and this config held it: a retuned grey ramp, five tier colours, and
// accent families rebuilt from them. All of that still exists — it just lives in `src/theme` now,
// as data that can be swapped at runtime, and this file's job is reduced to pointing Tailwind's
// class names at the custom properties a theme writes onto `:root`. See `src/theme/css.ts` for
// what those properties are and why each colour is emitted three times.
//
// Nothing here is theme-specific any more, which is the point: adding a theme never touches this
// file, and a rebuild is never needed to change how the app looks.

/**
 * One themed colour, in the form Tailwind's opacity modifier can work with.
 *
 * The `var()` test is the important half, and it is the same trick this file used before the
 * variables existed. For a bare `bg-gray-900` Tailwind does not omit the opacity — it passes the
 * string `var(--tw-bg-opacity, 1)`, which would resolve to a fully opaque panel and blot out the
 * backdrop behind it. Only an explicit `/NN` arrives as a number, so anything else falls back to
 * the surface's own alpha, which the theme published as `--w-NAME-a`.
 *
 * That fallback is why these are still function values and not the plain `rgb(var(--x) /
 * <alpha-value>)` string the Tailwind docs suggest: that form has no way to say "and if nobody
 * asked, this surface is 55% glass".
 */
const themed = (name) => ({ opacityValue }) =>
  opacityValue === undefined || String(opacityValue).startsWith('var(')
    ? `rgb(var(--w-${name}-rgb) / var(--w-${name}-a, 1))`
    : `rgb(var(--w-${name}-rgb) / ${opacityValue})`;

/**
 * A whole Tailwind ramp pointed at one tier's variables.
 *
 * The shade numbers keep the meanings the pages already use — 100-400 tint toward white for text on
 * dark, 500 is the colour itself, 600 is a shade darker for a filled control, 700-950 are
 * translucent wells for tinted banner backgrounds. The arithmetic that produces them moved to
 * `themeVars` in `src/theme/css.ts`, because a ramp computed here would be frozen at build time.
 */
const ramp = (tier) => Object.fromEntries(
  [100, 200, 300, 400, 500, 600, 700, 800, 900, 950].map(step => [step, themed(`${tier}-${step}`)]),
);

export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      colors: {
        gray: {
          // Surfaces: glass, so text keeps its contrast over a bright patch of whatever is behind
          // the app. Borders were all routed to the one `hair` token, because a scale that has to
          // be dark enough to sit under text can't also be light enough to draw an edge on top of
          // it — `bg-gray-800` and `border-gray-800` wanted opposite colours.
          950: themed('s950'),
          900: themed('s900'),
          800: themed('s800'),
          750: themed('s750'),
          700: themed('s700'),
          600: themed('s600'),
          // Text and the few borders bright enough to be seen: opaque.
          500: themed('t500'),
          400: themed('t400'),
          300: themed('t300'),
          200: themed('t200'),
          100: themed('t100'),
        },
        /** The floor a modal stands on — the one genuinely opaque surface. */
        void: themed('void'),
        /** A sheet of glass. The default surface for grouped content. */
        panel: themed('panel-lit'),
        /** Glass that has been pressed in — code blocks, wells, table stripes. */
        sunk: themed('sunk'),
        /** The one hairline. Every border on every page is this or an accent. */
        hair: themed('hair'),
        /** Half a hairline, for rules inside an already-bordered surface. */
        hairsoft: themed('hairsoft'),
        /** The one accent, for the rare class that wants it by name rather than through a tier. */
        accent: themed('accent'),
        // Accent families, all pointed at the six tiers. A page reaching for `emerald` and a page
        // reaching for `green` end up saying the same thing, which is what stopped the board and
        // the other tabs disagreeing about what "done" is coloured. `extend` deep-merges, so any
        // shade not listed by `ramp` still falls through to Tailwind's own.
        amber: { ...ramp('active'), orbit: 'var(--w-accent)' },
        yellow: ramp('active'),
        emerald: ramp('done'),
        green: ramp('done'),
        teal: ramp('done'),
        lime: ramp('done'),
        red: ramp('blocked'),
        rose: ramp('blocked'),
        orange: ramp('blocked'),
        violet: ramp('gate'),
        purple: ramp('gate'),
        fuchsia: ramp('gate'),
        pink: ramp('gate'),
        blue: ramp('queue'),
        sky: ramp('queue'),
        indigo: ramp('queue'),
        cyan: ramp('queue'),
      },
      fontFamily: {
        display: 'var(--w-font-display)',
        sans: 'var(--w-font-body)',
        mono: 'var(--w-font-mono)',
      },
      // Tracking is scaled, not replaced — see `type.track` in src/theme/tokens.ts. The scale keeps
      // Tailwind's own values so `tracking-wider` still means what it always did; the multiplier is
      // how much of it a theme wants. Arbitrary `tracking-[...]` values carry the same calc inline.
      letterSpacing: {
        tighter: 'calc(var(--w-track, 1) * -0.05em)',
        tight: 'calc(var(--w-track, 1) * -0.025em)',
        normal: '0em',
        wide: 'calc(var(--w-track, 1) * 0.025em)',
        wider: 'calc(var(--w-track, 1) * 0.05em)',
        widest: 'calc(var(--w-track, 1) * 0.1em)',
      },
      borderRadius: {
        DEFAULT: 'var(--w-r)',
        sm: 'var(--w-r-sm)',
        md: 'var(--w-r-md)',
        lg: 'var(--w-r-lg)',
        xl: 'var(--w-r-xl)',
        '2xl': 'var(--w-r-2xl)',
      },
    },
  },
  plugins: [],
}
