import type { Glass, Theme, TierName } from './tokens';

/**
 * A theme flattened into CSS custom properties.
 *
 * Everything the pages read goes through here, which is what makes a swap instant: the variables
 * are rewritten on `:root` and the browser repaints. React is not involved, Tailwind's classes
 * follow along because the config points them at these same names, and `index.css` follows because
 * its rules do too. Nothing has to be re-rendered for the colours to change; only the handful of
 * things a stylesheet cannot express — the clip paths that take an argument, the backdrop — need a
 * render, and those come off the theme object instead.
 *
 * Every colour is emitted three times under one name:
 *
 *   --w-NAME-rgb   the channels, space-separated: `206 174 51`
 *   --w-NAME-a     its resting alpha, when that is not 1
 *   --w-NAME       the usable colour, `rgb(206 174 51 / .16)`
 *
 * The split looks redundant and is not. `--w-NAME` is what an inline style wants, and reads as a
 * colour where it is used. `--w-NAME-rgb` is what Tailwind's opacity modifier needs, because
 * `bg-amber-500/20` has to be able to substitute its own alpha into the middle of the colour — and
 * it cannot do that to a string it did not build. `--w-NAME-a` is how a surface keeps its own
 * transparency when nobody asks for a different one.
 */

const CH = (hex: string): string => {
  const h = hex.replace('#', '');
  const w = h.length === 3 ? h.replace(/./g, c => c + c) : h;
  return `${parseInt(w.slice(0, 2), 16)} ${parseInt(w.slice(2, 4), 16)} ${parseInt(w.slice(4, 6), 16)}`;
};

/** The `--w-*` name for one colour, in the three-variable form described above. */
function emit(out: Record<string, string>, name: string, hex: string, alpha = 1): void {
  const rgb = CH(hex);
  out[`--w-${name}-rgb`] = rgb;
  if (alpha !== 1) out[`--w-${name}-a`] = String(alpha);
  out[`--w-${name}`] = alpha === 1 ? `rgb(${rgb})` : `rgb(${rgb} / ${alpha})`;
}

const emitGlass = (out: Record<string, string>, name: string, g: Glass) => emit(out, name, g.hex, g.alpha);

/**
 * A whole Tailwind ramp generated from one tier colour.
 *
 * This is the same construction the Tailwind config used to do at build time, moved to runtime so
 * the tier colours can change without a rebuild. The shade numbers keep the meanings the pages
 * already rely on: 100-400 tint toward white for text on a dark surface, 500 is the colour itself,
 * 600 is a shade darker for a pressed or filled control, and 700-950 are translucent wells for the
 * tinted banner backgrounds — dark enough to sit under text, transparent enough to show the
 * backdrop through.
 *
 * The glass end matters more than it looks. A `bg-red-900` that came out opaque would cut a hole in
 * the background behind it, and there is exactly one background behind the whole app.
 */
function ramp(out: Record<string, string>, name: string, hex: string): void {
  const [r, g, b] = CH(hex).split(' ').map(Number);
  const lift = (t: number) => `#${[r, g, b].map(c => Math.round(c + (255 - c) * t).toString(16).padStart(2, '0')).join('')}`;
  const drop = (t: number) => `#${[r, g, b].map(c => Math.round(c * (1 - t)).toString(16).padStart(2, '0')).join('')}`;
  emit(out, `${name}-100`, lift(0.78));
  emit(out, `${name}-200`, lift(0.6));
  emit(out, `${name}-300`, lift(0.4));
  emit(out, `${name}-400`, lift(0.18));
  emit(out, `${name}-500`, hex);
  emit(out, `${name}-600`, drop(0.16));
  emit(out, `${name}-700`, hex, 0.3);
  emit(out, `${name}-800`, hex, 0.22);
  emit(out, `${name}-900`, hex, 0.16);
  emit(out, `${name}-950`, hex, 0.1);
}

/**
 * How heavy each tier's row wash is.
 *
 * Not a theme token: these are corrections for the colours themselves, not a stylistic choice. A
 * warm blocked orange fills a row harder than a cool queue blue at the same alpha, and backlog has
 * to sit under the others whatever hue it is wearing. A theme picks the six colours; the weights
 * that keep them reading as one set stay here.
 */
const WASH: Record<TierName, number> = {
  blocked: 0.1, gate: 0.1, active: 0.1, queue: 0.08, backlog: 0.06, done: 0.07,
};

const TIERS = Object.keys(WASH) as TierName[];

/** The two chamfer sizes `index.css` cuts on its own — the modal, and the off-board action button. */
const CLIP_MODAL = 18;
const CLIP_BTN = 9;

/**
 * A theme's glyph artwork, packed into a `mask-image` value.
 *
 * Encoded here rather than in the theme files so a theme can write readable SVG markup and never
 * think about percent-escaping. `encodeURIComponent` rather than base64 for the same reason:
 * the variable stays greppable in devtools, and an unescaped `#` or `"` in a fill would otherwise
 * end the URL early and silently blank the glyph.
 */
const svgMask = (svg: string | undefined): string =>
  svg ? `url("data:image/svg+xml,${encodeURIComponent(svg.replace(/\s+/g, ' ').trim())}")` : 'none';

/** Every custom property this theme sets, ready to be written onto an element's style. */
export function themeVars(t: Theme): Record<string, string> {
  const v: Record<string, string> = {};

  // --- Surfaces and text ----------------------------------------------------
  emit(v, 'void', t.color.void);
  for (const step of [950, 900, 800, 750, 700, 600] as const) emitGlass(v, `s${step}`, t.color.surface[step]);
  for (const step of [700, 600, 500, 400, 300, 200, 100] as const) emit(v, `t${step}`, t.color.text[step]);
  emitGlass(v, 'hair', t.color.hair);
  emitGlass(v, 'hairsoft', t.color.hairsoft);
  emit(v, 'line', t.color.line);
  emitGlass(v, 'panel-lit', t.color.panel[0]);
  emitGlass(v, 'panel-deep', t.color.panel[1]);
  emitGlass(v, 'sunk', t.color.sunk);

  // --- The accent -----------------------------------------------------------
  emit(v, 'accent', t.color.accent);
  emit(v, 'accent-text', t.color.accentText);
  emit(v, 'accent-text-lit', t.color.accentTextLit);
  emit(v, 'on-accent', t.color.onAccent);
  emit(v, 'accent-lit', t.color.accentFill[0]);
  emit(v, 'accent-deep', t.color.accentFill[1]);

  // --- The six tiers, each as a bare colour, a wash, and a full Tailwind ramp ---
  for (const tier of TIERS) {
    const hex = t.color.tier[tier];
    emit(v, tier, hex);
    // The wash a row fills itself with: the tier at the weight a background can carry.
    emit(v, `${tier}-w`, hex, WASH[tier]);
    ramp(v, tier, hex);
  }

  // --- Agent identity hues, by slot ----------------------------------------
  t.color.agents.forEach((hex, i) => { emit(v, `agent-${i + 1}`, hex); ramp(v, `agent-${i + 1}`, hex); });
  v['--w-agents'] = String(t.color.agents.length);

  // --- Type -----------------------------------------------------------------
  v['--w-font-display'] = t.fonts.display;
  v['--w-font-body'] = t.fonts.body;
  v['--w-font-mono'] = t.fonts.mono;
  v['--w-track'] = String(t.type.track);
  v['--w-caps'] = t.type.caps;

  // --- Shape ----------------------------------------------------------------
  // The fixed-size cuts, so a stylesheet rule can be silhouetted without calling a function.
  //
  // Two variables per glyph rather than one, because a theme answers with either a clip path or a
  // mask and the element that draws it carries both properties unconditionally. Whichever the theme
  // did not fill in resolves to `none`, which is the identity for both — so the drawing site stays
  // three lines of style with no branch in it, and a theme changes technique without changing code.
  v['--w-hex'] = t.shape.hexMask ? 'none' : t.shape.hex;
  v['--w-hex-mask'] = svgMask(t.shape.hexMask);
  v['--w-diamond'] = t.shape.diamond;
  v['--w-ship'] = t.shape.shipMask ? 'none' : t.shape.ship;
  v['--w-ship-mask'] = svgMask(t.shape.shipMask);
  v['--w-clip-modal'] = t.shape.cutRow(CLIP_MODAL);
  v['--w-clip-btn'] = t.shape.cut(CLIP_BTN);
  v['--w-r-sm'] = t.shape.radius.sm;
  v['--w-r'] = t.shape.radius.DEFAULT;
  v['--w-r-md'] = t.shape.radius.md;
  v['--w-r-lg'] = t.shape.radius.lg;
  v['--w-r-xl'] = t.shape.radius.xl;
  v['--w-r-2xl'] = t.shape.radius['2xl'];

  // --- The backdrop's floor, so `html` has something to paint before React mounts ---
  v['--w-ground'] = t.backdrop.base;

  return v;
}
