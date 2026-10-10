import type { Theme } from '../tokens';

/**
 * A blank theme, with every decision listed and nothing decided.
 *
 * Copy this file, rename it, fill it in, then add it to `THEMES` in `../registry.ts`. That is the
 * whole checklist — there is no other place a theme has to be registered, and no page that has to
 * be told about it.
 *
 * The one rule, restated because it is the rule that makes this safe: **a theme changes how things
 * look, never where they are.** Nothing in this file can move an element, resize a panel, change a
 * grid or add a control. If a look seems to need one of those, it needs a change to the layout that
 * every theme then shares — not a token.
 *
 * Two things are worth knowing before filling it in:
 *
 *   * The surface ramp is *glass*. There is one backdrop behind the whole app and every panel sits
 *     on top of it, so a surface with `alpha: 1` will punch a hole in the background rather than
 *     sit on it. Keep 950-600 translucent unless you mean it.
 *   * The text ramp is not glass, and must not be. It is the half that has to stay legible over
 *     whatever the backdrop happens to be doing underneath it at that spot.
 */
export const template: Theme = {
  key: 'template',
  name: 'Template',
  blurb: 'One line, shown under the name in the Settings dropdown.',

  fonts: {
    // A Google Fonts stylesheet, or null to use only what the machine already has. Loaded while
    // this theme is active and dropped when it is not.
    href: null,
    display: 'system-ui, sans-serif', // headings, tab labels, buttons — labels, not prose
    body: 'system-ui, sans-serif',    // prose
    mono: 'ui-monospace, monospace',  // ids, counts, diffs, timestamps
  },

  type: {
    // A multiplier on the tracking every element was drawn with. 1 keeps it, 0 sets it solid.
    // A face with real capitals usually wants far less than the HUD sans the app was drawn in.
    track: 1,
    // 'none' if the display face should keep its own case rather than being shouted.
    caps: 'uppercase',
  },

  color: {
    // The one genuinely opaque colour: what a modal stands on so it occludes the backdrop.
    void: '#000000',

    // Surfaces, darkest to lightest. Translucent — see the note above.
    surface: {
      950: { hex: '#000000', alpha: 0.92 }, // the deepest well
      900: { hex: '#000000', alpha: 0.6 },  // the default panel fill
      800: { hex: '#000000', alpha: 0.55 }, // a raised panel
      750: { hex: '#000000', alpha: 0.6 },  // a hovered or selected row
      700: { hex: '#000000', alpha: 0.55 }, // a chip, a secondary control
      600: { hex: '#000000', alpha: 0.3 },  // the lightest fill, and a few visible borders
    },

    // Text, dimmest to brightest. Opaque. 700 and 600 are inline-style only — see `tokens.ts`.
    text: {
      700: '#000000', 600: '#000000', 500: '#000000', 400: '#000000',
      300: '#000000', 200: '#000000', 100: '#000000',
    },

    // The one hairline every border in the app is drawn with, and half of it for rules inside an
    // already-bordered surface.
    hair: { hex: '#000000', alpha: 0.13 },
    hairsoft: { hex: '#000000', alpha: 0.08 },
    // The hue those hairlines, the ghost buttons and the neutral glows are all mixed from.
    line: '#000000',

    // The two stops a panel's glass is graded between, lit corner first.
    panel: [{ hex: '#000000', alpha: 0.62 }, { hex: '#000000', alpha: 0.44 }],
    // Glass pressed in: code blocks, wells, table stripes.
    sunk: { hex: '#000000', alpha: 0.55 },

    // The one accent: primary actions, the live tab, anything the eye should land on first.
    accent: '#000000',
    // The accent as a label on dark glass — an active chip, the live tab — and hovered.
    accentText: '#000000',
    accentTextLit: '#000000',
    // Ink that sits on the accent — the primary button's own label.
    onAccent: '#ffffff',
    // The primary button's fill: lit stop, then deep stop.
    accentFill: ['#000000', '#000000'],

    // The six meanings a ticket can carry. Each one is used identically everywhere the ticket
    // appears — the row, its chevron on the track, the station node above it, the slide-over it
    // opens — so these six colours are most of what makes the board read as one object.
    tier: {
      active: '#000000',  // being worked right now
      done: '#000000',    // finished, cleared, healthy
      blocked: '#000000', // stopped, failed, destructive
      gate: '#000000',    // waiting on a human
      queue: '#000000',   // queued, informational
      backlog: '#000000', // not started, and not asking to be
    },

    // Identity hues for the agent berths, in slot order. Far enough apart in hue that two agents
    // working at once read as two agents.
    agents: ['#000000', '#000000', '#000000'],
  },

  shape: {
    // The silhouette everything is cut to. Return 'none' from both to opt out of clipping entirely
    // and let `radius` do the work instead — that is how a theme becomes rounded rather than
    // machined. `r` is how big the corner treatment should be, in px.
    cut: r => `polygon(${r}px 0,100% 0,100% 100%,0 100%,0 ${r}px)`,
    cutRow: r =>
      `polygon(${r}px 0,100% 0,100% calc(100% - ${r}px),calc(100% - ${r}px) 100%,0 100%,0 ${r}px)`,
    // Station cores on the arc, and the tier glyph on a slide-over.
    hex: 'polygon(50% 0,100% 28%,100% 72%,50% 100%,0 72%,0 28%)',
    // A ticket, wherever it appears on a track.
    diamond: 'polygon(50% 0,100% 50%,50% 100%,0 50%)',
    // The marker parked above a station node.
    ship: 'polygon(50% 0,100% 100%,50% 78%,0 100%)',
    // Tailwind's radius scale, which every `rounded-*` class resolves through.
    radius: { sm: '1px', DEFAULT: '2px', md: '3px', lg: '4px', xl: '6px', '2xl': '8px' },
  },

  backdrop: {
    // Painted always, animation on or off.
    base: '#000000',

    // Painted always, but cheap enough that switching the animation off does not remove it — a
    // paper grain, a stone wash, a fixed vignette. Omit for a theme with nothing static.
    still: [],

    // Painted only while "Animated background" is on in Settings.
    //
    //   null                                  nothing; the base colour and `still` are the whole
    //                                         background, and the toggle has nothing to switch
    //   { kind: 'layers', layers: [...] }     any stack of CSS planes — images, gradients,
    //                                         textures — each absolutely positioned over the last,
    //                                         each free to carry an animation class
    //   { kind: 'nebula', ... }               the WebGL cloud field, parameterised. See `space.ts`
    //
    // `layers` is the one to reach for first: it needs no GPU and no new code.
    motion: null,
  },

  // This theme's own agent mascot, or left out for the built-in bot.
  //
  // The one place a theme replaces a *drawing* rather than a value, because a creature is geometry
  // and a robot recoloured brown is still a robot. Leave it unset unless the shape is wrong for the
  // look: the bot is entirely token-driven and already wears the palette above.
  //
  // To fill it in, rename this file to .tsx, write a component against the `Mascot` contract in
  // `../mascot.ts`, and put it here. `components/mascots/Bot.tsx` is the worked example. The short
  // version: draw six visibly different moods inside a `size` square, scale every dimension off
  // `size` (it is called at 12px), and use the `palette` argument when it is given.
  //
  // mascot: Herald,

  // Anything no token covers — a border-image on panels, a texture on the body, a baseline nudge
  // this theme's face needs. Injected while the theme is active and torn down with it. Scope every
  // rule with `[data-theme="your-key"]`: this sheet is a global, and an unscoped selector in it
  // will follow you into every other theme.
  css: undefined,
};
