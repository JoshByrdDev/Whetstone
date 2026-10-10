import type { CSSProperties } from 'react';
import type { Mascot } from './mascot';

// Re-exported so a theme file has one place to import from: `../tokens` is the whole vocabulary.
export type { Mascot, MascotProps, MascotPalette, Mood } from './mascot';

/**
 * What a theme is allowed to change.
 *
 * The app was drawn once, as a sector map, and every colour and face in it was written where it
 * was used. That is fine for one look and impossible for two: a second skin would have meant
 * finding three hundred literals and hoping none were missed. So the look is lifted out into this
 * shape, and the pages are rewritten to ask for a token rather than to name a colour.
 *
 * The rule that decides what belongs here: **a theme may change what a thing looks like, never
 * where it is or what it does.** Colours, faces, tracking, the silhouette a surface is cut to, the
 * background behind the app — all fair game. Sizes, spacing, the grid, what is on which tab, which
 * control does what — never. A theme that could move things would be a second layout to maintain,
 * and the whole point is that there is one.
 *
 * Two halves, because the app needs them in two different ways:
 *
 *   * Everything expressible as a colour or a string becomes a CSS custom property on `:root`
 *     (see `css.ts`). Tailwind's classes and `index.css` both resolve through those variables, so
 *     a theme swap repaints without React knowing it happened.
 *   * The rest — the shape functions, the backdrop spec, palettes read by canvas code that has no
 *     stylesheet to consult — stays a plain object, read through `currentTheme()`.
 */

/** A colour that carries its own resting transparency. Most surfaces here are glass, not paint. */
export interface Glass {
  hex: string;
  /** 0-1. Applied when nothing asks for a different opacity — see the Tailwind config. */
  alpha: number;
}

/** The six meanings a ticket can carry. One colour each, used identically everywhere it appears. */
export type TierName = 'blocked' | 'gate' | 'active' | 'queue' | 'backlog' | 'done';

/** An RGB triple in 0-1, the form a shader wants. */
export type Vec3 = [number, number, number];

/** One painted plane in the backdrop stack, positioned `absolute inset-0` for you. */
export interface BackdropLayer {
  style: CSSProperties;
  /** An animation class from `index.css`, if this layer moves. */
  className?: string;
  /**
   * How far this plane slides against the pointer, in px at the edge of the screen.
   *
   * The space theme has parallax because its sky is a shader and the pointer is a uniform in it.
   * Every other theme paints its sky as flat planes, and a flat plane has no depth to read — so
   * depth is stated here instead: a number per layer, largest for whatever is nearest the viewer.
   * The photograph at the back takes a few px, the weather in front of it takes twenty-odd, and the
   * difference between the two is the entire effect.
   *
   * Costs one wrapper div and a transform. `Backdrop` eases the pointer into `--w-mx`/`--w-my`
   * once for the whole stack, so ten parallaxed layers are ten transforms reading one variable
   * rather than ten listeners. Omit it and the layer is nailed down, which is right for a vignette:
   * the frame's own falloff has to stay on the frame.
   */
  parallax?: number;
}

/**
 * The animated half of the background — drawn only while "Animated background" is on in Settings.
 *
 * `nebula` runs the WebGL cloud field the space theme is built on, parameterised rather than
 * hardcoded. `layers` is the escape hatch: any stack of CSS planes — images, gradients, textures,
 * whatever a theme needs — with no GPU work and no new component to write.
 */
export type Motion =
  | {
      kind: 'nebula';
      /** The three cloud colours, brightest first. */
      clouds: [Vec3, Vec3, Vec3];
      /** Reshuffles the noise field and the star positions. Any number; it just has to differ. */
      seed: number;
      /** Painted behind the canvas, and left showing on a machine with no WebGL at all. */
      under?: BackdropLayer[];
      /** Painted over the canvas: the planet, the vignette, the scanlines. */
      over?: BackdropLayer[];
    }
  | { kind: 'layers'; layers: BackdropLayer[] };

export interface Backdrop {
  /** Painted always, animation on or off. The floor the whole app stands on. */
  base: string;
  /** Painted always, but cheap — a paper grain, a stone wash. Omitted by a theme with none. */
  still?: BackdropLayer[];
  /** Painted only when the animated background is switched on. */
  motion: Motion | null;
}

/** Clip paths and radii — the silhouette everything on the page is cut to. */
export interface Shape {
  /** A cut top-left corner, `r` px across. The default silhouette for panels and buttons. */
  cut(r: number): string;
  /** Cut top-left *and* bottom-right: the ticket row, and the modal. */
  cutRow(r: number): string;
  /** Station cores on the arc, and the tier glyph on a slide-over. */
  hex: string;
  /**
   * The same glyph as SVG artwork, for a theme whose station is a thing rather than a shape.
   *
   * A clip path is one closed outline with no holes in it, which is exactly enough for a hex bolt
   * and nowhere near enough for a palm or a basket: fronds that do not separate read as a turbine,
   * and a net with no gaps in it reads as a bucket. So a theme may hand over an SVG instead, which
   * is composited as a mask — same single tier colour behind it, same box, but strokes, gaps and
   * counters all survive.
   *
   * Write the markup plainly and let `css.ts` encode it. Square `viewBox`, no `width`/`height`
   * (the mask is fitted with `contain`), and no colours — only the alpha is read, so paint
   * everything in one fill and use `fill-rule="evenodd"` for holes. Set this and `hex` is ignored.
   *
   * The sizes to draw for are 8px (a station nothing has reached), 13px (one that something has),
   * and 64px (the tier glyph on a slide-over). Eight is the hard one.
   */
  hexMask?: string;
  /** A ticket, wherever it appears on a track. */
  diamond: string;
  /** The marker parked above a station node. */
  ship: string;
  /** The marker as SVG artwork, on the same terms as `hexMask`. Drawn at 11px and nothing else. */
  shipMask?: string;
  /**
   * Tailwind's radius scale. The space theme cuts corners instead of rounding them, so these are
   * near-square; a theme that rounds says so here and returns `'none'` from `cut`.
   */
  radius: { sm: string; DEFAULT: string; md: string; lg: string; xl: string; '2xl': string };
}

export interface Theme {
  /** Stable id. Written to localStorage and to `data-theme` on `<html>`, so never rename one. */
  key: string;
  /** What the dropdown in Settings says. */
  name: string;
  /** One line under it, in the dropdown's description. */
  blurb: string;

  fonts: {
    /**
     * A stylesheet `<link href>` fetched while this theme is active, or null for system faces.
     * Owned by the theme rather than by index.html, so switching themes swaps the faces that get
     * downloaded instead of paying for every theme's on every load.
     */
    href: string | null;
    /** Headings, tab labels, buttons, section rules — anything that is a label rather than prose. */
    display: string;
    /** Prose. */
    body: string;
    /** Anything countable: ids, counts, diffs, timestamps. */
    mono: string;
  };

  /**
   * How display type is set. A wide-tracked uppercase sans reads as a readout; the same tracking
   * on a face with real capitals reads as a mistake. So the treatment travels with the face.
   */
  type: {
    /**
     * A multiplier on every tracked element's letter-spacing, not a value that replaces them.
     *
     * The page title, a tab label, the eyebrow above a heading and a section rule are tracked at
     * four different widths, and the difference between them is hierarchy — it says which of the
     * four you are meant to read first. One token that flattened them to a single width would be a
     * theme rewriting the layout, which is the one thing a theme may not do. So each element keeps
     * the tracking it was drawn with and this scales all of them together: `1` is as drawn, `0`
     * sets everything solid, and anything between keeps the rhythm and changes the amount.
     *
     * Use it through `track()` in `runtime.ts` rather than writing the `calc()` by hand.
     */
    track: number;
    /** `uppercase` for a HUD; `none` for a face that wants to keep its own case. */
    caps: 'uppercase' | 'none';
  };

  color: {
    /** The deepest floor: the ground a modal stands on, and the fallback behind everything. */
    void: string;
    /**
     * Surfaces, darkest to lightest. Glass on purpose — there is one background behind the whole
     * app, and a panel that paints itself opaque punches a hole in it.
     */
    surface: { 950: Glass; 900: Glass; 800: Glass; 750: Glass; 700: Glass; 600: Glass };
    /**
     * Text, dimmest to brightest. Opaque: this is the half that has to stay legible.
     *
     * Only 500-100 are reachable as Tailwind classes — `gray-600` and `gray-700` are surface slots
     * in that scale, and one name cannot be both a fill dark enough to sit under text and a text
     * colour. 700 and 600 exist for the inline styles that need something dimmer than `gray-500`:
     * placeholders, the eyebrow above a heading, the meta line beside a section rule.
     */
    text: { 700: string; 600: string; 500: string; 400: string; 300: string; 200: string; 100: string };
    /** The one hairline every border on every page is drawn with, and half of it. */
    hair: Glass;
    hairsoft: Glass;
    /** The hue hairlines, ghost buttons and neutral glows are all mixed from. */
    line: string;
    /** The two stops a panel's glass is graded between. */
    panel: [Glass, Glass];
    /** Glass that has been pressed in: code blocks, wells, table stripes. */
    sunk: Glass;
    /** The one accent. Primary actions, the live tab, anything the eye should land on first. */
    accent: string;
    /** The accent as a label on dark glass: an active chip, a live tab. */
    accentText: string;
    /** The same label with the pointer on it. */
    accentTextLit: string;
    /** Ink that sits *on* the accent — the primary button's label. */
    onAccent: string;
    /** The primary button's fill, lit stop then deep stop. */
    accentFill: [string, string];
    /** The six tiers. `active` is normally the accent; nothing requires it to be. */
    tier: Record<TierName, string>;
    /** Identity hues for the agent berths, in slot order. Three is the usual count. */
    agents: string[];
  };

  shape: Shape;
  backdrop: Backdrop;

  /**
   * The theme's own agent mascot, or nothing for the built-in bot.
   *
   * The one place a theme replaces a drawing rather than a value — see `mascot.ts` for why, and for
   * the contract. Optional on purpose: the bot is entirely token-driven and already wears whatever
   * palette a theme sets, so a look that a recoloured robot suits does not need to fill this in.
   */
  mascot?: Mascot;

  /**
   * Anything this theme needs that no token covers — a border-image on panels, a texture on the
   * body, a face-specific baseline nudge. Injected verbatim into a `<style>` while the theme is
   * active, and torn down with it. Scope every rule: this sheet is a global, and an unscoped
   * selector in it will leak into every other theme's idea of what a page looks like.
   */
  css?: string;
}
