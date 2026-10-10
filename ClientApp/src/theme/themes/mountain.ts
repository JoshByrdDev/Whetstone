import type { CSSProperties } from 'react';
import type { BackdropLayer, Theme } from '../tokens';
import { Falcon } from '../../components/mascots/Falcon';
// A photograph of green mountains dropping into a turquoise sea, vendored rather than hot-linked,
// for the reason the keep's wall is: a local dashboard whose floor is a third party's CDN has no
// floor when the network is down. Meina Yin / Unsplash, free for commercial use under the Unsplash
// licence. Imported rather than dropped in `public/` so the build emits it content-addressed next
// to the bundles — cached immutably, and impossible to leave behind if this theme ever goes.
import coast from './coast-ridge.jpg';

/**
 * BEACH MOUNTAIN — the same board again, drawn as a coastline seen from altitude.
 *
 * The third theme, and the one that settles the question the second one only argued: the two skins
 * before this were both *rooms* — a sector map and a stone hall, each lit from a source inside the
 * frame. This one is outdoors at midday, and that is the whole idea the values serve. There is no
 * lamp to be near or far from, so brightness stops meaning distance from the fire and starts
 * meaning distance through air: the ramp cools and pales as it lifts, the way a ridge eight miles
 * out is paler than the one in front of it. That is why the surface ramp is a haze rather than a
 * set of fills, why the accent is the water rather than a light, and why the only things that move
 * are birds — nothing in a landscape flickers.
 *
 * Taken from the Claude Design board mockup, `Board Theme.dc.html` option 2a, on background B of
 * `Mountain Bliss Backgrounds.dc.html`. Two deliberate departures from it are noted where they
 * happen: the still/animated split in `backdrop`, and the speed of the two light pools.
 */

/**
 * One bird crossing the frame.
 *
 * `top` is a percentage of the viewport rather than the mockup's pixels — its card was 1205px tall
 * and knew it, and a window is not. Everything else is the mockup's, to the tenth of a second: the
 * twelve birds are on twelve different crossing times and twelve different wingbeats, which is what
 * stops them reading as one flock on a conveyor.
 *
 * The artwork is not here. A backdrop layer is a single styled box, and a bird needs two — one that
 * crosses and one that flaps, because an element has one transform. So the box below carries the
 * crossing and `.mb-bird::before` in this theme's sheet carries the wing, with the flap's timing
 * handed down as custom properties.
 */
const bird = (
  top: string, w: number, cross: number, delay: number,
  flap: number, flapDelay: number, opacity: number,
): BackdropLayer => ({
  className: 'mb-bird',
  style: {
    inset: 'auto', left: 0, top, width: w, height: Math.round(w * 0.54), opacity,
    animation: `mb-fly ${cross}s linear infinite ${delay}s`,
    ['--mb-flap']: `${flap}s`,
    ['--mb-flap-delay']: `${flapDelay}s`,
  } as CSSProperties,
});

/**
 * The outline of the cliff in front, in the photograph's own pixels — 1920 by 1280, the size the
 * file is.
 *
 * Traced, rather than derived. The obvious alternative was a depth map off Depth Anything or MiDaS,
 * and for this photograph it would have been the wrong tool twice over. A depth map buys a
 * *continuous* depth for every pixel, which is what a scene with no clean silhouette needs and this
 * one is not; and displacing by one is at its worst exactly where this frame is at its strongest —
 * a hard occlusion edge with nothing behind it to sample, which is the smear you see on every
 * three-dimensional-photograph toy. This picture has one dominant edge in it and that edge can
 * simply be drawn. It also costs nothing to ship, where a depth map is a second full-size image.
 *
 * Written in image pixels and masked `cover` at the photograph's own position, so the outline lands
 * on the rock at any window shape without a second set of numbers to keep in step with the first.
 * It runs off the left and bottom borders on purpose: a mask feathered at the frame edge is a cliff
 * that fades out before it reaches the corner.
 *
 * The blur is what makes an eyeballed trace good enough. Twenty-odd pixels of falloff turns a
 * tracing error into a slightly softer edge instead of a visible cut — and on a coast photographed
 * through this much haze, a soft edge is what the eye is expecting from the near thing anyway.
 */
const CLIFF =
  'M-60 154L0 154L54 173L78 192L100 218L134 256L160 294L174 320L188 346L202 384L216 422'
  + 'L238 448L265 486L292 512L326 550L357 576L394 614L419 640L438 666L476 704L509 742L547 768'
  + 'L586 794L634 832L660 870L676 896L692 922L716 960L740 998L752 1024L782 1062L808 1088'
  + 'L846 1126L872 1152L902 1190L922 1216L948 1242L982 1280L982 1340L-60 1340Z';

/** The outline above, packed into a `mask-image`. White on nothing: the mask reads the alpha. */
const CLIFF_MASK = `url("data:image/svg+xml,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1920 1280">'
  + '<filter id="f" x="-8%" y="-8%" width="116%" height="116%">'
  + '<feGaussianBlur stdDeviation="7"/></filter>'
  + `<path filter="url(#f)" fill="#fff" d="${CLIFF}"/></svg>`,
)}")`;

/**
 * One frond of the palm a station is drawn as, pointing right out of the crown and drooping.
 *
 * Written once and placed six times — see `shape.hexMask`. The outline runs along the top of the
 * leaf from the crown, arching up and then falling away to the tip, and returns underneath it on a
 * shallower curve, so the leaf is broad where it leaves the trunk and comes to a point at the end.
 * The two curves' control points are what set that taper; moving them together thins or fattens
 * the whole canopy at once.
 */
const FROND = 'M0 -5.6C20 -19 42 -15 50 1C40 -8.5 20 -4.5 0 5.6Z';

export const mountain: Theme = {
  key: 'mountain',
  name: 'Beach Mountain',
  blurb: 'A turquoise coastline from altitude: glacier glass, cool light and birds crossing.',

  fonts: {
    // One face for both roles, which neither of the first two themes does. Outfit is a geometric
    // sans with a wide, even upper case, so the same drawing carries the tracked HUD labels and the
    // prose under them; a second face here would only be a second face.
    href:
      'https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700' +
      '&family=JetBrains+Mono:wght@400;600;700&display=swap',
    display: 'Outfit, system-ui, sans-serif',
    body: 'Outfit, system-ui, -apple-system, sans-serif',
    // The mono stays, for the third time. A figure column is a figure column on any mountain.
    mono: "'JetBrains Mono', ui-monospace, SFMono-Regular, monospace",
  },

  type: {
    // As drawn. Outfit's capitals are round and open and take the app's tracking without crowding —
    // the mockup set every label at the widths the space theme uses, to the em.
    track: 1,
    caps: 'uppercase',
  },

  color: {
    void: '#050d14',

    // Cool as it lightens, and hazier rather than lighter: 950 is water at depth and 750 is the
    // near ridge. Alphas are the space theme's, untouched — they were tuned against this layout,
    // and the mockup's own panels landed within a hundredth of every one of them.
    surface: {
      950: { hex: '#030a10', alpha: 0.92 },
      900: { hex: '#071119', alpha: 0.6 },
      800: { hex: '#0f1f2b', alpha: 0.55 },
      750: { hex: '#1b3140', alpha: 0.6 },
      700: { hex: '#172c3a', alpha: 0.55 },
      // The lightest fill, and what `text-gray-600` — the dimmest text in the app — resolves to.
      // The queue's own haze at a third strength, the same construction both other themes use here.
      600: { hex: '#8fb8c9', alpha: 0.3 },
    },

    // Every step is a colour off the mockup. Compressed at the bottom and open at the top, as in
    // both other themes: 700 is a placeholder and 100 is a page title, and the distance between
    // them was never linear.
    text: {
      700: '#5f7c8c',
      600: '#7794a5',
      500: '#93b0c1',
      400: '#a8bfcd',
      300: '#bcd2df',
      200: '#dfeaf1',
      100: '#eef6fa',
    },

    // Pale glacier blue at a seventh. Every border in the app is this line; over a photograph it
    // reads as the light coming round the edge of a thing rather than as a rule drawn on it.
    hair: { hex: '#9ecde4', alpha: 0.14 },
    hairsoft: { hex: '#9ecde4', alpha: 0.08 },
    line: '#9ecde4',

    panel: [{ hex: '#091620', alpha: 0.62 }, { hex: '#061019', alpha: 0.44 }],
    sunk: { hex: '#04101a', alpha: 0.55 },

    // The accent is the water. Both earlier themes accent with a light — an amber lamp, a gold leaf
    // — and this one has no lamp in it, so the eye is sent to the one saturated thing in a
    // landscape of haze instead.
    accent: '#4fc3d9',
    accentText: '#8fdcea',
    accentTextLit: '#bfeaf3',
    onAccent: '#08222a',
    accentFill: ['#6fd0e2', '#1d6c80'],

    tier: {
      active: '#4fc3d9',  // meltwater — Claude is working it
      done: '#7fc08e',    // the treeline
      blocked: '#e08a5c', // the one warm thing in the frame, which is the point of it
      gate: '#9aa8e8',    // distance, where the ridges go blue
      queue: '#8fb8c9',   // haze
      backlog: '#8b9aa6', // bare rock
    },

    // The three berths: kingfisher, pine, and a kestrel's back.
    agents: ['#5ad0e6', '#7fc08e', '#d9a06b'],
  },

  shape: {
    // The chamfers are the space theme's, unchanged, for the reason the keep kept them: the app's
    // entire silhouette — panel, row, button, chip — is built on the cut corner, and changing it
    // would change what fits on a line, which is the one thing a theme may not do.
    cut: r => `polygon(${r}px 0,100% 0,100% 100%,0 100%,0 ${r}px)`,
    cutRow: r =>
      `polygon(${r}px 0,100% 0,100% calc(100% - ${r}px),calc(100% - ${r}px) 100%,0 100%,0 ${r}px)`,
    // Unused: the palm is drawn below, as artwork. Kept as the fallback a clip-path-only build
    // would fall to, and as the shape of the argument for why artwork was needed at all.
    hex: 'polygon(37% 100%,43% 30%,9% 45%,5% 36%,36% 23%,13% 8%,19% 1%,50% 16%,81% 1%,87% 8%,64% 23%,95% 36%,91% 45%,57% 30%,63% 100%)',
    // A station stops being a hex bolt and becomes a palm — the one tree on the coast, and the only
    // glyph in the set that says *beach* rather than *altitude*.
    //
    // It was a clip path first, and a clip path is one closed outline with no holes in it, so the
    // fronds had to be joined at the crown and the sky between them could not be drawn. What came
    // out was a wind turbine: two straight arms on a post, because that is the only thing a joined
    // crown *can* be. Six separate fronds and a trunk they overlap is the whole fix, and it needed
    // the parts to be allowed not to touch.
    //
    // Each frond is the same outline placed six times — three to a side, mirrored rather than
    // rotated through, so the droop stays a droop instead of curling up on the left. They arch up
    // out of the crown and fall away to a point, which is what a palm does and what the turbine
    // could not; the pair at −52° fill the notch overhead so the crown reads as a canopy rather
    // than as two wings. The trunk is deliberately heavy: at the 8px an unreached station draws
    // this at, a trunk drawn to scale is a hairline, and a palm with no trunk is a shrub.
    hexMask:
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" fill="#fff">' +
      '<path d="M40.5 100C42.5 74 44.5 56 48.5 41L58 42C55.4 58 53.4 76 57 100Z"/>' +
      '<g transform="translate(50 39)">' +
      '<path transform="rotate(16) scale(.96)" d="' + FROND + '"/>' +
      '<path transform="rotate(-16)" d="' + FROND + '"/>' +
      '<path transform="rotate(-52) scale(.86)" d="' + FROND + '"/>' +
      '<path transform="scale(-1 1) rotate(16) scale(.96)" d="' + FROND + '"/>' +
      '<path transform="scale(-1 1) rotate(-16)" d="' + FROND + '"/>' +
      '<path transform="scale(-1 1) rotate(-52) scale(.86)" d="' + FROND + '"/>' +
      '</g></svg>',
    // A ticket stays a diamond, for the third time. It is drawn at 6px in nine columns of every
    // row; there is nothing else it can be.
    diamond: 'polygon(50% 0,100% 50%,50% 100%,0 50%)',
    // Unused, and instructive. This is the gull the mockup's three-line gust glyph was flattened
    // into when the marker could only be a clip path: body in the middle, a wing either side, and
    // no way to draw the air between them. At the 11px it is painted at, three separated spikes
    // over a common base is not a bird — it is a lower-case w, and three of them over a station
    // spelled one out.
    ship: 'polygon(0 10%,32% 58%,50% 26%,68% 58%,100% 10%,96% 34%,70% 84%,50% 52%,30% 84%,4% 34%)',
    // So the marker goes back to being what the mockup drew: wind. Three strokes running with the
    // weather at three different lengths, and one of them curling back on itself at the end.
    // Strokes rather than a filled silhouette because that is what a gust is — a line the air took
    // — and because a mask can carry one where a clip path could not.
    //
    // One curl, not three. A curl is what says *air* rather than *dash*, and it is also the only
    // part of this that is not a straight line: at eleven pixels each additional one is a second
    // blob, and two blobs one above the other stop reading as weather and start reading as a
    // paragraph mark. The three unequal lengths do the rest of the work — a gust is not a stack.
    //
    // Heavier than it looks written down. Three and three fifths of a twenty-four unit box lands
    // near one and two thirds of a pixel once it is fitted into eleven, and anything finer
    // anti-aliases into dots against the sky behind it.
    shipMask:
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#fff"' +
      ' stroke-width="3.6" stroke-linecap="round">' +
      '<path d="M2.2 5.5h9a3.2 3.2 0 1 0-3.2-3.2"/>' +
      '<path d="M2.2 12.5h17"/><path d="M2.2 19.5h11"/></svg>',
    radius: { sm: '1px', DEFAULT: '2px', md: '3px', lg: '4px', xl: '6px', '2xl': '8px' },
  },

  backdrop: {
    base: '#08131b',

    /**
     * The view, painted whether or not the animation is on.
     *
     * Split differently from the mockup, which had no such setting: everything that does not move
     * is here — the coast, the grading that makes it usable as a floor, and the falloff at the
     * edges. Switch the animation off and you get a still, empty landscape. Switch it on and the
     * birds arrive.
     */
    still: [
      /*
       * The coast itself. The graded scrim over it is not dressing — it is what makes a photograph
       * usable as the floor of a UI: a picture has its own contrast and its own focal point, and
       * the app has to be legible over all of it. The filter does the same job upstream, turning a
       * photograph into a surface before anything is asked to sit on it.
       *
       * `backgroundSize: cover` rather than the mockup's `<img object-fit:cover>` — same crop, and
       * a background that simply does not paint if the file is missing, where a broken `<img>`
       * shows the browser's icon. The scrim over `base` still reads as a dark cool coast.
       */
      {
        // The photograph takes the smallest share of the pointer in this stack, which is the whole
        // grammar of the effect: what is furthest away moves least. Eight pixels at the edge of the
        // screen is barely a shift on its own — it is the gap between this and the thirty-two the
        // cliff cut out of it takes that the eye reads as depth.
        //
        // What is left in this layer once that cut is made is the sky, the sea, the far headland
        // and the town: everything the haze has already flattened into a single distance. They can
        // share a plane because at eight miles out they *are* one plane.
        parallax: 8,
        style: {
          backgroundImage: `url(${coast})`,
          backgroundSize: 'cover',
          // Off-centre downward: the horizon is the brightest band in the frame and belongs above
          // the nav rather than behind the middle of the board.
          backgroundPosition: '50% 42%',
          filter: 'saturate(.8) brightness(.62) contrast(1.05)',
        },
      },
      /*
       * The same photograph again, masked to the cliff in front of it, travelling four times as far.
       *
       * This is the only layer in the app that is a second copy of something already on screen, and
       * it costs almost nothing to be one: same URL, so the browser hands back the same decoded
       * bitmap. A second *use* of the picture rather than a second picture — no new file, no second
       * decode, no shader and no canvas. The depth is bought with a mask and a number.
       *
       * What it buys is the one thing a photograph on a plane cannot have. Every backdrop in this
       * app until now moved as a sheet: sea, headland and rock all slid together, and a frame that
       * slides together is a picture being waved about rather than a place with distance in it. Cut
       * the near cliff out, give it its own travel, and the two slide *against* each other. That is
       * the whole effect, and there is nothing else in it.
       *
       * `transform` here is structural and cannot be dropped. Both copies still contain the cliff,
       * so the moment the near one slides back across the far one it uncovers the far one's own edge
       * — a second, stationary cliff standing a few pixels off the first, against the brightest
       * thing in the frame. The fix is to bias this copy permanently outboard, up and to the right,
       * by more than the two can ever differ: the gap in `parallax` is twenty-four, the silhouette
       * faces up and right along its whole length, and thirty-eight with its three fifths clears
       * that at every corner with room over. The price is a cliff drawn thirty-eight pixels larger
       * than the photograph has it, which at this size is a fiftieth of the frame and reads as
       * nothing at all.
       *
       * Graded apart from the copy underneath, in the direction the rest of the theme already
       * argues for: a little more colour and contrast, a little less light. Distance here is carried
       * by haze, so the near thing is the one the haze has not got to yet — the same sentence the
       * surface ramp is built on, said with a filter instead of a hex code.
       */
      {
        parallax: 32,
        style: {
          backgroundImage: `url(${coast})`,
          backgroundSize: 'cover',
          backgroundPosition: '50% 42%',
          filter: 'saturate(.94) brightness(.55) contrast(1.14)',
          maskImage: CLIFF_MASK,
          WebkitMaskImage: CLIFF_MASK,
          maskSize: 'cover',
          WebkitMaskSize: 'cover',
          maskPosition: '50% 42%',
          WebkitMaskPosition: '50% 42%',
          maskRepeat: 'no-repeat',
          WebkitMaskRepeat: 'no-repeat',
          transform: 'translate3d(38px,-23px,0)',
        } as CSSProperties,
      },
      { style: { background: 'linear-gradient(180deg,rgba(6,16,26,.72) 0%,rgba(7,18,28,.6) 16%,rgba(8,18,28,.34) 45%,rgba(6,14,22,.5) 78%,rgba(5,11,18,.68) 100%)' } },
      // The glare off the water, put back after the scrim took it out. Overlay rather than a plain
      // fill, so it lifts what the photograph already has lit instead of washing the sea pale.
      { style: { mixBlendMode: 'overlay', opacity: 0.42, background: 'radial-gradient(58% 34% at 50% 78%,rgba(190,228,245,.26),transparent 72%)' } },
      // The frame falls away at the sides and overhead. Three fixed boxes in the mockup, because it
      // was 1320px wide and knew it; percentages here, because a window is not.
      {
        style: {
          background:
            'radial-gradient(22% 40% at 0% 40%,rgba(0,0,0,.62),rgba(0,0,0,.28) 52%,transparent 76%),' +
            'radial-gradient(22% 40% at 100% 46%,rgba(0,0,0,.62),rgba(0,0,0,.28) 52%,transparent 76%),' +
            'radial-gradient(40% 26% at 50% 0%,rgba(0,0,0,.42),rgba(0,0,0,.16) 58%,transparent 78%)',
        },
      },
      // Light coming up off the shallows, off the bottom of the screen.
      { style: { background: 'radial-gradient(58% 34% at 50% 108%,rgba(140,200,225,.16),rgba(90,150,180,.07) 42%,transparent 72%)' } },
      { style: { background: 'radial-gradient(48% 40% at 50% 78%,rgba(150,205,230,.12),transparent 70%)' } },
      { style: { inset: 'auto', left: 0, right: 0, bottom: 0, height: 170, background: 'linear-gradient(0deg,rgba(6,14,22,.78),transparent)' } },
      // Vignette, then a grain over it. `zIndex` lifts both above the birds and the light pools,
      // which are painted after them — weather sits *inside* the frame's falloff, not on top of it.
      { style: { zIndex: 1, background: 'radial-gradient(118% 76% at 50% 24%,transparent 24%,rgba(6,14,22,.34) 70%,rgba(5,11,18,.7) 100%)' } },
      { style: { zIndex: 1, mixBlendMode: 'overlay', background: 'repeating-linear-gradient(0deg,rgba(255,255,255,.012) 0 1px,transparent 1px 3px)' } },
    ],

    /**
     * The weather. Fourteen CSS planes rather than a shader, for the reason the keep's fire is:
     * no GPU work, no second canvas, and every one of them animating opacity and transform only,
     * which is the pair a compositor can do without touching layout.
     */
    motion: {
      kind: 'layers',
      layers: [
        /*
         * Two pools of light: cold off the water on the left, warm off the sun on the right.
         *
         * The mockup ran both on the keep's 3.1s and 3.9s torch flicker, which is the one value in
         * it that was carried over rather than chosen — a hillside does not gutter. Same two pools,
         * same colours and positions, at fourteen and eighteen seconds, which is the rate a cloud
         * crosses the sun at.
         */
        {
          // Five times the photograph's travel, and a little past the cliff's: light in the air is
          // the nearest thing in the frame, in front of the rock rather than on it. The pools are
          // also the layer that can afford to be — they are soft-edged radial gradients with no
          // detail to smear, so they can be moved a long way before anyone can see them move.
          parallax: 40,
          style: {
            inset: 'auto', left: '-18%', top: '6%', width: 760, height: 760,
            background: 'radial-gradient(circle,rgba(150,215,240,.2),rgba(90,170,205,.09) 32%,rgba(8,18,28,0) 66%)',
            animation: 'mb-drift 14s ease-in-out infinite',
          },
        },
        {
          parallax: 40,
          style: {
            inset: 'auto', right: '-18%', top: '26%', width: 740, height: 740,
            background: 'radial-gradient(circle,rgba(255,214,160,.16),rgba(220,170,110,.07) 32%,rgba(8,18,28,0) 66%)',
            animation: 'mb-drift 18s ease-in-out infinite 3s',
          },
        },
        // Twelve birds, spread down the top half of the frame and out of step with each other.
        bird('7.5%', 26, 26, 0, 1.0, 0, 0.85),
        bird('14.4%', 20, 34, 2.7, 1.22, 0.31, 0.7),
        bird('21.2%', 15, 30, 5.4, 1.44, 0.62, 0.55),
        bird('28.1%', 24, 28, 8.1, 1.66, 0.93, 0.8),
        bird('35%', 18, 36, 10.8, 1.0, 1.24, 0.68),
        bird('41.9%', 13, 32, 13.5, 1.22, 1.55, 0.5),
        bird('48.8%', 25, 25, 16.2, 1.44, 1.86, 0.9),
        bird('55.7%', 19, 33, 18.9, 1.66, 2.17, 0.72),
        bird('9.5%', 14, 29, 21.6, 1.0, 2.48, 0.58),
        bird('16.3%', 22, 31, 24.3, 1.22, 2.79, 0.78),
        bird('23.2%', 17, 27, 27, 1.44, 3.1, 0.65),
        bird('30.1%', 12, 35, 29.7, 1.66, 3.41, 0.52),
      ],
    },
  },

  mascot: Falcon,

  /**
   * The wing, and the keyframes this theme's own layers animate on.
   *
   * They live here rather than in `index.css` because they are this theme's and no other's — they
   * are injected with it and torn down with it, so a theme with no birds in it does not carry the
   * definition of one. Prefixed `mb-` for the same reason every rule in a theme sheet has to be
   * scoped: this string is a global while it is mounted. `mb-flap` is the backdrop birds' alone —
   * the mascot beat on it while it had a flying pose, and perches now.
   *
   * `mb-fly` is written in `vw` rather than the mockup's pixels: it crossed a 1320px card, and this
   * has to cross whatever the window is. The overshoot at each end is what keeps a bird from
   * appearing and vanishing at the edges instead of entering and leaving.
   *
   * The reduced-motion block redefines rather than disables. A later `@keyframes` of the same name
   * wins, so an empty one leaves the light pools at their own static styles and the wings held
   * open, and a transparent one parks the birds out of sight — which is the honest answer, because
   * a bird frozen mid-air is a bug.
   */
  css: `
.mb-bird::before{content:'';position:absolute;inset:0;transform-origin:50% 50%;
background:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 26' fill='%230c1c26'%3E%3Cpath d='M24 5.6c1.2 0 2 1 2.1 2.2l.6 9.6c.1 1.5-.5 2.8-1.4 3.7L24 22.4l-1.3-1.3c-.9-.9-1.5-2.2-1.4-3.7l.6-9.6c.1-1.2.9-2.2 2.1-2.2z'/%3E%3Cpath d='M23 9.2C17 7 9 4 1 .8c4.2 6.4 11.2 11.4 20.4 14z'/%3E%3Cpath d='M25 9.2c6-2.2 14-5.2 22-8.4-4.2 6.4-11.2 11.4-20.4 14z'/%3E%3C/svg%3E") center/contain no-repeat;
animation:mb-flap var(--mb-flap,1.3s) ease-in-out infinite var(--mb-flap-delay,0s)}
@keyframes mb-fly{0%{transform:translate(-140px,0)}50%{transform:translate(52vw,-30px)}100%{transform:translate(calc(100vw + 160px),12px)}}
@keyframes mb-flap{0%,100%{transform:scaleY(1)}50%{transform:scaleY(.4)}}
@keyframes mb-drift{0%,100%{opacity:.7;transform:scale(1)}50%{opacity:1;transform:scale(1.07)}}
@media (prefers-reduced-motion: reduce){
@keyframes mb-fly{from,to{opacity:0}}
@keyframes mb-flap{}
@keyframes mb-drift{}
}`,
};
