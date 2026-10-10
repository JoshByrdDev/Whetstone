import type { Theme } from '../tokens';

/**
 * ORBIT — the board drawn as a sector map, which is what the app looked like before there was such
 * a thing as a theme.
 *
 * Every value here was lifted out of the place it used to be written: the Tailwind config's retuned
 * grey ramp, the five tier colours off the board, `ui.tsx`'s accent constants, the nebula's cloud
 * uniforms. Nothing was redesigned on the way out, so this file is also the reference for what a
 * theme has to answer — a second theme is this file with different values in it, and nothing else.
 *
 * The idea the values are all serving: one sky behind the whole app, and every surface in front of
 * it made of glass so the sky shows through. That is why the surface ramp is translucent and the
 * text ramp is not, and why `void` is the only genuinely opaque colour in the set — modals are the
 * one thing that has to occlude what is under it.
 */
export const space: Theme = {
  key: 'space',
  name: 'Space',
  blurb: 'A nebula sky, cool blue glass and one amber accent. Corners are cut, not rounded.',

  fonts: {
    href:
      'https://fonts.googleapis.com/css2?family=Chakra+Petch:wght@400;500;600;700' +
      '&family=Barlow:wght@400;500;600;700&family=JetBrains+Mono:wght@400;600;700&display=swap',
    display: "'Chakra Petch', Barlow, system-ui, sans-serif",
    body: 'Barlow, system-ui, -apple-system, sans-serif',
    mono: "'JetBrains Mono', ui-monospace, SFMono-Regular, monospace",
  },

  type: {
    // As drawn. This theme's whole voice is the wide tracking, so there is nothing to scale back.
    track: 1,
    caps: 'uppercase',
  },

  color: {
    void: '#05080f',
    surface: {
      950: { hex: '#04070e', alpha: 0.92 },
      900: { hex: '#090e1a', alpha: 0.6 },
      800: { hex: '#141d30', alpha: 0.55 },
      750: { hex: '#202d46', alpha: 0.6 },
      700: { hex: '#1e2a42', alpha: 0.55 },
      600: { hex: '#7aa7de', alpha: 0.3 },
    },
    text: {
      700: '#56698c',
      600: '#61789b',
      500: '#6f88b0',
      400: '#8ea3c4',
      300: '#a9bfdc',
      200: '#cddcf2',
      100: '#eef3fb',
    },
    hair: { hex: '#96b4e6', alpha: 0.13 },
    hairsoft: { hex: '#96b4e6', alpha: 0.08 },
    line: '#96b4e6',
    panel: [{ hex: '#0c1220', alpha: 0.62 }, { hex: '#060a14', alpha: 0.44 }],
    sunk: { hex: '#050912', alpha: 0.55 },

    accent: '#ceae33',
    accentText: '#e6cf72',
    accentTextLit: '#f3e3a4',
    onAccent: '#141005',
    accentFill: ['#d8b93c', '#8f7414'],

    tier: {
      active: '#ceae33',  // amber — Claude is working it
      done: '#6dc04a',    // green — finished, cleared, healthy
      blocked: '#e0562f', // orange-red — stopped, failed, destructive
      gate: '#b18fd6',    // violet — waiting on a human
      queue: '#7aa7de',   // blue — queued, informational
      backlog: '#8b93a3', // grey — not started, and not asking to be
    },

    // The three agent berths. Far enough apart in hue that two bots working at once are two bots.
    agents: ['#fbbf24', '#22d3ee', '#e879f9'],
  },

  shape: {
    // The chamfer, not the pill. Radii are near-square so a rounded panel reads as machined rather
    // than soft; the real chamfers are the clip paths, applied where an element is big enough to
    // carry one.
    cut: r => `polygon(${r}px 0,100% 0,100% 100%,0 100%,0 ${r}px)`,
    cutRow: r =>
      `polygon(${r}px 0,100% 0,100% calc(100% - ${r}px),calc(100% - ${r}px) 100%,0 100%,0 ${r}px)`,
    hex: 'polygon(50% 0,100% 28%,100% 72%,50% 100%,0 72%,0 28%)',
    diamond: 'polygon(50% 0,100% 50%,50% 100%,0 50%)',
    ship: 'polygon(50% 0,100% 100%,50% 78%,0 100%)',
    radius: { sm: '1px', DEFAULT: '2px', md: '3px', lg: '4px', xl: '6px', '2xl': '8px' },
  },

  backdrop: {
    base: '#04060c',
    motion: {
      kind: 'nebula',
      clouds: [
        [0.26, 0.11, 0.44], // violet
        [0.05, 0.25, 0.44], // deep blue
        [0.46, 0.32, 0.09], // amber
      ],
      seed: 5.6,
      under: [
        {
          // The same three glows in the same places, for a machine with no WebGL to give.
          //
          // Deliberately not parallaxed, which is the one place in this theme where the right depth
          // is no depth. The canvas over it is opaque — `alpha: false` — so on every machine that
          // can draw the sky, this layer is painted and then completely covered. Giving it a
          // `parallax` would promote a full-screen plane to its own compositor buffer for the
          // lifetime of the app in order to move something nobody can see.
          style: {
            backgroundImage:
              'radial-gradient(58% 48% at 22% 20%,rgba(96,34,120,.5),transparent 70%),' +
              'radial-gradient(52% 44% at 80% 28%,rgba(18,78,128,.5),transparent 72%),' +
              'radial-gradient(66% 54% at 55% 90%,rgba(160,104,28,.26),transparent 70%)',
          },
        },
      ],
      over: [
        {
          /*
           * The planet the app orbits: a lit crescent bleeding off the top-right corner.
           *
           * The one thing in this sky with a distance of its own. Everything behind it is drawn by
           * the shader, which carries its own depth — the clouds are sampled at three rates and the
           * stars at three more, so the field already reads as deep. What it could not do is put
           * anything *in front of* itself: the canvas is one plane, and the planet was painted onto
           * it as flatly as the vignette.
           *
           * So the planet comes off the sky and onto its own. Forty-four pixels is the largest
           * travel in any theme, because a body you are in orbit around is the nearest thing in the
           * frame by an enormous margin — the stars behind it are the furthest, and the whole point
           * of moving it is that the gap between the two is the only thing here that can state a
           * scale. It can also afford the travel: this is a soft radial gradient with no edge
           * anywhere in it, so there is nothing to smear, and forty-four is a fourteenth of its own
           * diameter.
           *
           * The vignette and the scanlines below stay nailed down, as they are in every theme. They
           * are the frame the sky is seen through, not part of the view, and a vignette that slides
           * about is a lens rattling in its mount.
           */
          parallax: 44,
          style: {
            inset: 'auto',
            right: '-9%',
            top: '-11%',
            width: 640,
            height: 640,
            borderRadius: '50%',
            background:
              'radial-gradient(circle at 34% 32%,rgba(206,174,51,.16),rgba(48,32,72,.36) 46%,rgba(4,6,14,0) 68%)',
            boxShadow: 'inset -36px -28px 96px rgba(0,0,0,.85)',
          },
        },
        {
          // Vignette, then scanlines: the sky has to sit behind text without competing with it.
          style: {
            background:
              'radial-gradient(120% 74% at 50% 16%,transparent 26%,rgba(3,5,12,.62) 78%,rgba(3,5,12,.9) 100%)',
          },
        },
        {
          style: {
            mixBlendMode: 'overlay',
            background: 'repeating-linear-gradient(0deg,rgba(255,255,255,.018) 0 1px,transparent 1px 3px)',
          },
        },
      ],
    },
  },
};
