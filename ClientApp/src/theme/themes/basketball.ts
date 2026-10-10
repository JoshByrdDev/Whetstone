import type { CSSProperties } from 'react';
import type { BackdropLayer, Theme } from '../tokens';
import { Baller } from '../../components/mascots/Baller';
// A photograph of a park hoop in silhouette against an LA sunset, vendored rather than hot-linked,
// for the reason the keep's wall, the coast and the slope are: a local dashboard whose floor is a
// third party's CDN has no floor when the network is down. Marius Kriz / Unsplash, free for
// commercial use under the Unsplash licence. Imported rather than dropped in `public/` so the build
// emits it content-addressed next to the bundles — cached immutably, and impossible to leave behind
// if this theme ever goes.
//
// Cropped to 1920x1280 on the way down rather than shipped as the 2400x3844 portrait it is: the
// original is twice as tall as it is wide, and `cover` on a landscape window would have thrown away
// two thirds of the file's bytes on every load. The crop is centred, which is the band the mockup's
// `object-position: 50% 46%` was showing anyway — open sky, the backboard, and the pole coming up
// from the bottom left.
import court from './court-hoop.jpg';

/**
 * BASKETBALL — the same board again, drawn on an outdoor court at sunset.
 *
 * The fifth theme, and the first warm outdoor one. The two landscapes before it are cold and lit
 * from the sky: haze carries the depth on the coast, blue carries it on the slope, and in both of
 * them the accent is the one saturated thing in a frame that has no lamp in it. This has a lamp in
 * it — two, in fact, and a sun going down behind the hoop — so brightness goes back to meaning
 * *distance from a light*, the way it does in the keep, while the frame stays a landscape.
 *
 * That is what the ramp is built on. Surfaces are warm near-black, because the blacktop under a
 * floodlight is warm near-black and everything on this board is sitting on blacktop. Text is a
 * warm off-white rather than a cool one, because paper under a sodium light is. And the accent is
 * the ball: the one manufactured colour in a frame full of dusk, and the only thing in the picture
 * that somebody chose.
 *
 * The other thing an outdoor court decides is the weather. The coast's birds cross and leave, the
 * slope's snow falls and never arrives; the dust over a blacktop does neither — it comes across on
 * whatever the evening is doing, catches the floodlight and goes out. It has a direction and no
 * destination, which is the gentlest of the three behind a board somebody is trying to read.
 *
 * Taken from the Claude Design board mockup, `Board Theme.dc.html` option 4a, on background A
 * ("Hoop at sunset") of `Basketball Backgrounds.dc.html`. Three deliberate departures from it are
 * noted where they happen: the still/animated split in `backdrop`, the speed of the two floodlights,
 * and the crop of the photograph above.
 */

/**
 * One speck of dust crossing the court on the evening air.
 *
 * `top` is a percentage of the viewport rather than the mockup's pixels — its card was 1320px wide
 * and knew it, and a window is not. Everything else is the mockup's, to the tenth of a second: the
 * twelve motes are on twelve different crossing times and twelve different starting offsets, which
 * is what stops them reading as one curtain going past on a loop.
 *
 * They used to rise, straight up off the blacktop, which is what the mockup drew and what dust does
 * over a fire. This is not a fire — it is an open court at the end of the day, and what moves dust
 * there is the wind. So they cross instead, which is a gentler thing to have happening behind a
 * board somebody is reading: a rise has a destination and the eye follows it up the screen into the
 * text, and a crossing has neither.
 *
 * Wider than they are tall, and by more the faster they are going. A round speck moving sideways is
 * a round speck; a slightly drawn-out one is the same speck with the shutter left open, which is
 * what a light on a moving thing actually looks like and the cheapest way to say *travelling*.
 *
 * `drift` picks which of the two crossings this one takes — `bb-blow` lifts as it goes, `bb-blow2`
 * settles. Two directions rather than one is what makes it read as air moving rather than as a
 * conveyor.
 */
const mote = (
  top: string, d: number, drift: 1 | 2, dur: number, delay: number,
  hex: string, glow: boolean,
): BackdropLayer => ({
  style: {
    inset: 'auto', left: 0, top, width: d * 2.4, height: d, borderRadius: '50%',
    background: hex,
    ...(glow ? { boxShadow: '0 0 7px rgba(255,190,110,.75)' } : {}),
    animation: `bb-blow${drift === 2 ? '2' : ''} ${dur}s linear infinite ${delay}s`,
  } as CSSProperties,
});

/**
 * The outline of the hoop — pole, backboard, rim and net — in the photograph's own 1920x1280.
 *
 * Measured rather than drawn by eye, which the coast's cliff was not and suffered for. This frame
 * is a near-black assembly against a bright sunset, so a luminance threshold finds its edges
 * exactly, and three eyeballed attempts had the pole up to ninety pixels off before the scan
 * settled it. The same scan corrected something no amount of squinting had: the backboard is
 * *tilted*, its top edge dropping some hundred and twenty pixels from left to right, which every
 * hand-drawn version had as a level rectangle.
 *
 * The bottom two chains are the pole's upper and lower edges, sampled every thirty-two pixels down
 * the frame; between them the outline runs up the backboard's left edge, across its raked top, down
 * its right, and around the net hanging below. It is left open at the bottom of the frame — the
 * path runs past 1280 — so the blur has something to eat into off-screen rather than fading the
 * pole out before it reaches the edge.
 */
const HOOP =
  'M470 1300L483 1216L498 1184L514 1152L532 1120L550 1088L570 1056L591 1024L614 992L639 960'
  + 'L666 928L696 896L729 864L766 832L808 800L856 768L912 736L984 704L998 698L998 198L1590 318'
  + 'L1590 800L1540 795L1532 925L1394 925L1386 782L1100 745L1080 800L1017 832L967 864L924 896'
  + 'L888 928L856 960L827 992L800 1024L777 1056L757 1088L739 1120L722 1152L706 1184L692 1216'
  + 'L692 1300Z';

/**
 * The outline above, fattened by forty-eight pixels and then softened, as a `mask-image`.
 *
 * The fattening is the whole difference between this theme and the coast, and it is forced by the
 * shape. A cliff is attached to the edge of the frame, so its silhouette faces one way along its
 * whole length and a single push up and to the right keeps the near copy clear of the far one. A
 * hoop is an *object*: it has sky on every side, its silhouette faces outward in every direction at
 * once, and no translation on earth can be outboard of all of them — push it right and the far
 * copy's left edge walks out from behind it.
 *
 * So instead of moving the near copy clear, the mask is grown clear. `stroke` on a filled path is a
 * dilation: half the stroke width in every direction. Grow it by more than the two layers can ever
 * differ — thirty-four pixels across and three fifths of that down, so a little under forty all
 * told — and the near copy covers the far one's hoop from any pointer position, in any direction.
 * Forty-eight leaves margin.
 *
 * What that costs is a band of sky, up to forty-eight pixels wide, that travels with the hoop
 * instead of with the sky. It is invisible, and it is invisible for a reason worth stating: this
 * background is a smooth sunset gradient with nothing in it to register against. A strip of that
 * gradient offset by thirty-four pixels, cross-faded over the blur, differs from its neighbour by
 * less than the banding already in the JPEG. The same trick over the coast would have dragged a
 * visible chunk of sea along with the cliff.
 *
 * `round` on the joins because a dilated path with mitred corners grows spikes at every sharp
 * turn, and there are twenty-odd of them down the pole.
 */
const HOOP_MASK = `url("data:image/svg+xml,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1920 1280">'
  + '<filter id="f" x="-14%" y="-14%" width="128%" height="128%">'
  + '<feGaussianBlur stdDeviation="8"/></filter>'
  + `<path filter="url(#f)" fill="#fff" stroke="#fff" stroke-width="96" stroke-linejoin="round"`
  + ` stroke-linecap="round" d="${HOOP}"/></svg>`,
)}")`;

export const basketball: Theme = {
  key: 'basketball',
  name: 'Basketball',
  blurb: 'A park hoop at sunset: warm blacktop, court orange, floodlights and dust on the breeze.',

  fonts: {
    // Outfit again, and for the same reason the two landscapes picked it: one geometric sans with a
    // wide, even upper case carries both the tracked HUD labels and the prose under them. It is
    // also the face a scoreboard is set in, which is not why it was chosen and is worth having.
    href:
      'https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700' +
      '&family=JetBrains+Mono:wght@400;600;700&display=swap',
    display: 'Outfit, system-ui, sans-serif',
    body: 'Outfit, system-ui, -apple-system, sans-serif',
    // The mono stays, for the fifth time. A figure column is a figure column on any surface.
    mono: "'JetBrains Mono', ui-monospace, SFMono-Regular, monospace",
  },

  type: {
    // As drawn. The mockup set every label at the widths the space theme uses, to the em.
    track: 1,
    caps: 'uppercase',
  },

  color: {
    // The deepest floor: blacktop out of the light. Warm, because asphalt under a sodium lamp is —
    // the same near-black mixed cool would read as a screen switched off behind the app.
    void: '#0b0908',

    // Warmer and lighter together, which neither landscape does: on the coast and the slope the
    // ramp cools as it lifts because distance is what lifts it, and here the thing that lifts a
    // surface is a floodlight two courts away. Alphas are the space theme's, untouched, as in every
    // theme before this — they were tuned against this layout, and the mockup's own panels landed
    // on them.
    surface: {
      950: { hex: '#070605', alpha: 0.92 },
      900: { hex: '#12100e', alpha: 0.6 },
      800: { hex: '#201b16', alpha: 0.55 },
      750: { hex: '#372d24', alpha: 0.6 },
      700: { hex: '#2e261e', alpha: 0.55 },
      // The lightest fill, and what `text-gray-600` — the dimmest text in the app — resolves to.
      // The queue's own grey at a third strength, the construction every other theme uses here.
      600: { hex: '#a6a9ae', alpha: 0.3 },
    },

    // A warm off-white ramp: the mockup's own values from 500 up, extended downward. Compressed at
    // the bottom and open at the top, as in all four themes before it: 700 is a placeholder and 100
    // is a page title, and the distance between them was never linear.
    //
    // The mockup also uses #e2d8cc for meta lines and #fdf7f0 for ticket titles. Both land between
    // steps here and neither got a slot of its own — seven is the whole scale, and a ramp with two
    // near-identical rungs in it is a ramp nobody can pick a rung from.
    text: {
      700: '#8d8073',
      600: '#a89a89',
      500: '#bdae9c',
      400: '#cdbfae',
      300: '#ded2c4',
      200: '#f0e6da',
      100: '#fffaf4',
    },

    // Warm chalk at a seventh. Every border in the app is this line; over a photograph of a court it
    // reads as a painted line catching the last of the light rather than as a rule drawn on top of
    // one, which is exactly what the app's hairlines are pretending to be here.
    hair: { hex: '#e8d6c4', alpha: 0.14 },
    hairsoft: { hex: '#e8d6c4', alpha: 0.08 },
    line: '#e8d6c4',

    panel: [{ hex: '#151110', alpha: 0.62 }, { hex: '#0e0b0a', alpha: 0.44 }],
    sunk: { hex: '#0a0807', alpha: 0.55 },

    // The accent is the ball. Both landscapes accent with a piece of the scenery — the water, the
    // sky — because a landscape has nothing manufactured in it; a court has exactly one
    // manufactured thing in it, and it is orange, and it is what everybody in the frame is looking
    // at. There was no second candidate.
    accent: '#ff8c2e',
    accentText: '#ffc78e',
    accentTextLit: '#ffdcb8',
    onAccent: '#241408',
    accentFill: ['#ffb066', '#7a3b0e'],

    tier: {
      active: '#ff8c2e',  // the ball — Claude is working it
      done: '#64d08a',    // the one green thing on a blacktop, which is the grass past the fence
      blocked: '#ff4d5e', // a foul, and the only colour here that is louder than the ball
      gate: '#8fb4ff',    // the sky on the far side of the sunset, where the evening is already blue
      queue: '#a6a9ae',   // galvanised fence wire
      backlog: '#7d8590', // the pole and the backboard frame, out of the light
    },

    // The three berths, straight off the mockup's dashed dockets and its badge numerals: a warm
    // jersey orange, the green from the fence line, and a gold. The third is the mockup's badge
    // rather than its avatar, which was a second orange a shade off the first — two agents working
    // at once have to read as two agents, and those two did not.
    agents: ['#ff9d4a', '#64d08a', '#ffd166'],
  },

  shape: {
    // The chamfers are the space theme's, unchanged, for the reason every theme keeps them: the
    // app's entire silhouette — panel, row, button, chip — is built on the cut corner, and changing
    // it would change what fits on a line, which is the one thing a theme may not do.
    cut: r => `polygon(${r}px 0,100% 0,100% 100%,0 100%,0 ${r}px)`,
    cutRow: r =>
      `polygon(${r}px 0,100% 0,100% calc(100% - ${r}px),calc(100% - ${r}px) 100%,0 100%,0 ${r}px)`,
    // Unused, and the clearest case in the app for why artwork exists. A hoop is a rim you can see
    // through with a net hanging under it, and a clip path is one closed outline with nothing
    // showing through anywhere: filled solid, the rim became a bar and the net became a wedge under
    // the middle of it, and what a station actually drew was a capital T.
    hex: 'polygon(0 0,100% 0,100% 20%,74% 20%,66% 100%,50% 82%,34% 100%,26% 20%,0 20%)',
    // The rim and the net, and no backboard. The ellipse is the whole read — a ring seen at a slant
    // is a hoop and nothing else is, and it is exactly the shape that cannot survive being filled
    // in, which is why this is artwork and `hex` above is a record of a failure.
    //
    // The backboard went, and that is the change worth explaining. The first cut of this drew one,
    // because the mockup does and because a goal has one: a bar across the top, the rim slung under
    // it, the net under that. What it bought at 200px it took back at every size the app actually
    // draws — the bar is the widest thing in the box, so `contain` scales the whole glyph to fit
    // *it*, and the hoop that is supposed to be the subject ends up a third of the height with a
    // heavy lid over it. At the 12px a station in play draws this at, that is a shield with a
    // fringe. Cutting the bar lets the rim have the full width of the box, which is roughly twice
    // the ellipse for the same pixels, and the ball parked above the node says backboard-or-not
    // more clearly than nine units of bar ever did.
    //
    // Four strands rather than the eight a net has. Eight is what the mockup shows at 200px; at the
    // 13px a station in play draws this at, eight strands are a solid trapezoid again, and the
    // point of drawing a net is the gaps. Everything here is stroked heavy for the same reason.
    hexMask:
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" fill="none" stroke="#fff">' +
      '<ellipse cx="50" cy="26" rx="43" ry="14" stroke-width="12"/>' +
      '<g stroke-width="8.5" stroke-linecap="round">' +
      '<path d="M11 32 29 90"/><path d="M34 40 40 94"/>' +
      '<path d="M66 40 60 94"/><path d="M89 32 71 90"/>' +
      '<path d="M19 56C33 65 67 65 81 56"/><path d="M27 78C37 85 63 85 73 78"/>' +
      '</g></svg>',
    // A ticket stays a diamond, for the fifth time. It is drawn at 6px in nine columns of every row;
    // there is nothing else it can be, and the mockup agrees — its rows draw the same diamond.
    diamond: 'polygon(50% 0,100% 50%,50% 100%,0 50%)',
    // Unused. A circle, which is the right silhouette and, on its own, an orange dot — the mockup
    // draws seams across the ball and a clip path had nowhere to put them.
    ship: 'circle(40% at 50% 50%)',
    // The ball with its seams in it. One outline and four holes, punched with `evenodd`: the two
    // seams that cross at the middle, and the two that curve out to the sides. The horizontal is
    // bowed because a straight one across the equator is a latitude line.
    //
    // Tilted twenty-two degrees, and that is the load-bearing part. Upright, this drawing is a
    // globe: a pole at the top, a pole at the bottom, meridians between them and an equator across
    // the middle is a schoolroom wireframe of the Earth, and no amount of orange argues with it.
    // A ball is not mounted on an axis, so it has no reason to be square to the frame — and the
    // moment the seams sit at an angle, the only thing they can be is seams. It is the cheapest
    // change in this file and the one that fixed the icon.
    //
    // Seams at 1.9 units rather than the 1.3 they were drawn at first. This is painted at eleven
    // pixels and nowhere else, so a seam is either about a pixel wide or it is a grey suggestion
    // that anti-aliasing swallows; 1.9 of 24 is the narrowest that survives.
    //
    // Every hole is drawn strictly inside the circle, which is a constraint rather than a style.
    // `evenodd` counts crossings, so any part of a seam that overhangs the ball is inside one path
    // instead of two and comes back *filled* — a seam that ran past the edge would grow a spur off
    // it. Hence the 1.05 inset at the poles and the 1.2 at the sides. Rotating cannot break that:
    // a rotation about the ball's own centre leaves every point at the radius it was already at.
    shipMask:
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">' +
      '<g transform="rotate(22 12 12)">' +
      '<path fill="#fff" fill-rule="evenodd" d="M12 1a11 11 0 1 0 0 22 11 11 0 0 0 0-22Z' +
      'M11.05 1.1h1.9v21.8h-1.9Z' +
      'M1.2 11.05C8 13.65 16 13.65 22.8 11.05L22.8 12.95C16 15.55 8 15.55 1.2 12.95Z' +
      'M12 1.05C2.8 6 2.8 18 12 22.95C4.99 18 4.99 6 12 1.05Z' +
      'M12 1.05C21.2 6 21.2 18 12 22.95C19.01 18 19.01 6 12 1.05Z"/></g></svg>',
    radius: { sm: '1px', DEFAULT: '2px', md: '3px', lg: '4px', xl: '6px', '2xl': '8px' },
  },

  backdrop: {
    base: '#100d0c',

    /**
     * The court, painted whether or not the animation is on.
     *
     * Split the way both landscapes are, and differently from the mockup, which had no such
     * setting: everything that does not move is here — the photograph, the grading that makes it
     * usable as a floor, and the falloff at the edges. Switch the animation off and the floodlights
     * stop breathing and the dust settles; the hoop and the sunset stay.
     */
    still: [
      /*
       * The court itself. The graded scrim over it is not dressing — it is what makes a photograph
       * usable as the floor of a UI: a picture has its own contrast and its own focal point, and the
       * app has to be legible over all of it. The filter does the same job upstream, turning a
       * photograph into a surface before anything is asked to sit on it.
       *
       * The scrim is cool over a warm photograph, which looks like a mistake and is the mockup's
       * deliberate choice: a sunset knocked back with more sunset stays the same picture, only
       * darker, and the app then has an orange page to be orange on. Cooling it is what buys the
       * accent somewhere to be the warmest thing on screen.
       *
       * `backgroundSize: cover` rather than the mockup's `<img object-fit:cover>` — same crop, and a
       * background that simply does not paint if the file is missing, where a broken `<img>` shows
       * the browser's icon.
       */
      {
        // Six pixels, the least any layer in any theme takes, because once the hoop is cut out of
        // it what is left in here is *sky* — and a sunset has no distance at all, it is the thing
        // distance is measured against. It is not held at zero only because a sky that is perfectly
        // nailed down while everything else drifts reads as a painted backcloth.
        parallax: 6,
        style: {
          backgroundImage: `url(${court})`,
          backgroundSize: 'cover',
          // Slightly above centre, as the mockup crops it: the backboard is the one piece of
          // structure in the frame and belongs behind the nav rather than behind the board.
          backgroundPosition: '50% 46%',
          filter: 'saturate(.66) brightness(.6) contrast(1.1)',
        },
      },
      /*
       * The hoop, cut out of the sky behind it and given forty pixels of its own.
       *
       * The largest gap between two layers in any theme, and it should be: the two landscapes cut a
       * near ridge out of a far one, which is a ratio of a few miles to eight, while this cuts a
       * steel pole five metres away out of a sunset that is ninety million miles off. There is no
       * honest way to draw that except to let the hoop move and leave the sky where it is.
       *
       * It is also the frame that gains most from it. A coast has haze doing half the work already,
       * and a hoop against a flat gradient has nothing — no aerial perspective, no overlap, no
       * texture falling off with distance. The only depth cue available in this picture is motion,
       * which is exactly the cue a still photograph cannot carry and this can.
       *
       * Graded *identically* to the copy underneath, which is the opposite of what the coast does
       * and is forced by the dilation above. Darkening the near layer a step — the coast's trick,
       * and the obvious way to say "this one is closer" — put a visible shadow the width of the
       * dilation around the entire hoop, because out there the near layer is not hoop at all, it is
       * forty-eight pixels of sky wearing the near layer's grade. A mask that hugs its subject can
       * be graded apart from what it was cut from; a mask grown past its subject cannot, and the
       * band gives it away instantly against a flat sky.
       *
       * Which leaves motion carrying the depth on its own here, and that is the honest position
       * anyway: there is no haze between a pole and a sunset to grade against.
       */
      {
        parallax: 40,
        style: {
          backgroundImage: `url(${court})`,
          backgroundSize: 'cover',
          backgroundPosition: '50% 46%',
          filter: 'saturate(.66) brightness(.6) contrast(1.1)',
          maskImage: HOOP_MASK,
          WebkitMaskImage: HOOP_MASK,
          maskSize: 'cover',
          WebkitMaskSize: 'cover',
          maskPosition: '50% 46%',
          WebkitMaskPosition: '50% 46%',
          maskRepeat: 'no-repeat',
          WebkitMaskRepeat: 'no-repeat',
        } as CSSProperties,
      },
      { style: { background: 'linear-gradient(180deg,rgba(12,13,18,.82) 0%,rgba(12,13,18,.7) 13%,rgba(13,15,20,.42) 26%,rgba(13,15,20,.4) 46%,rgba(11,12,17,.56) 60%,rgba(10,11,15,.6) 78%,rgba(8,8,11,.72) 100%)' } },
      // The last of the daylight off the top of the court, put back after the scrim took it out.
      // Overlay rather than a plain fill, so it lifts what the photograph already has lit instead of
      // washing the whole sky pale.
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
      // Light coming back up off the blacktop, off the bottom of the screen. Warm at the centre and
      // cooling outward, which is what a floodlit surface does at the edge of its own pool.
      { style: { background: 'radial-gradient(58% 34% at 50% 108%,rgba(255,186,100,.15),rgba(90,150,180,.07) 42%,transparent 72%)' } },
      { style: { background: 'radial-gradient(48% 40% at 50% 78%,rgba(150,205,230,.12),transparent 70%)' } },
      { style: { inset: 'auto', left: 0, right: 0, bottom: 0, height: 170, background: 'linear-gradient(0deg,rgba(6,14,22,.78),transparent)' } },
      // Vignette, then a grain over it. `zIndex` lifts both above the dust and the floodlights,
      // which are painted after them — weather sits *inside* the frame's falloff, not on top of it.
      { style: { zIndex: 1, background: 'radial-gradient(118% 76% at 50% 24%,transparent 24%,rgba(6,14,22,.34) 70%,rgba(5,11,18,.7) 100%)' } },
      { style: { zIndex: 1, mixBlendMode: 'overlay', background: 'repeating-linear-gradient(0deg,rgba(255,255,255,.012) 0 1px,transparent 1px 3px)' } },
    ],

    /**
     * The evening. Fourteen CSS planes rather than a shader, for the reason the keep's fire, the
     * coast's birds and the slope's snow are: no GPU work, no second canvas, and every one of them
     * animating opacity and transform only, which is the pair a compositor can do without touching
     * layout.
     */
    motion: {
      kind: 'layers',
      layers: [
        /*
         * Two floodlights, both warm. Both landscapes have a cold pool in them somewhere because
         * both are lit by the sky; a court after sunset is lit by two lamps on poles and nothing
         * else, so there is no cold light left to balance them against — the cold in this frame is
         * the scrim, not a source.
         *
         * The mockup ran both on the keep's 3.1s and 3.9s torch flicker, which is the one value it
         * inherited rather than chose. A floodlight is not a torch: it does move, because a ballast
         * hums and the air over hot asphalt does not hold still, but it moves at the rate of the
         * air rather than the rate of a flame. Same two pools, same colours and positions, at nine
         * and eleven and a half seconds — slow enough not to read as fire, quick enough to read as
         * something electric rather than as a cloud going over.
         */
        {
          // Three times the photograph's travel, because this is the near half of the frame. The
          // pools are also the layer that can afford it: they are soft-edged radial gradients with
          // no detail to smear, so they can be moved a long way before anyone can see them move.
          parallax: 26,
          style: {
            inset: 'auto', left: '-18%', top: '13%', width: 760, height: 760,
            background: 'radial-gradient(circle,rgba(255,196,110,.18),rgba(230,140,50,.07) 32%,rgba(8,18,28,0) 66%)',
            animation: 'bb-flood 9s ease-in-out infinite',
          },
        },
        {
          parallax: 26,
          style: {
            inset: 'auto', right: '-18%', top: '25%', width: 740, height: 740,
            background: 'radial-gradient(circle,rgba(255,196,110,.2),rgba(230,140,50,.08) 32%,rgba(8,18,28,0) 66%)',
            animation: 'bb-flood 11.5s ease-in-out infinite 2s',
          },
        },
        // Twelve specks of dust, spread down the frame and out of step with each other. Twelve is
        // the mockup's count over a 1320px card, kept rather than scaled up: this has to sit behind
        // a board somebody is reading, and the point at which dust stops being air and starts being
        // a screensaver is a lot closer than it looks.
        //
        // Spread down the *top* two thirds rather than evenly, which is where they differ from the
        // rising version they replaced. Dust coming up off the blacktop belonged at the bottom of
        // the frame because that is where the blacktop is; dust already up and travelling belongs in
        // the open air above the board, where there is nothing for it to cross in front of.
        mote('7.5%', 3, 1, 13.0, 0, '#ffd9a5', true),
        mote('13.4%', 2, 2, 15.2, 1.3, '#f2c895', false),
        mote('19.3%', 2, 1, 17.4, 2.6, '#f2c895', false),
        mote('25.2%', 3, 2, 19.6, 3.9, '#ffd9a5', true),
        mote('31.1%', 2, 1, 21.8, 5.2, '#f2c895', false),
        mote('37%', 2, 2, 13.0, 6.5, '#f2c895', false),
        mote('42.9%', 3, 1, 15.2, 7.8, '#ffd9a5', true),
        mote('48.8%', 2, 2, 17.4, 9.1, '#f2c895', false),
        mote('54.7%', 2, 1, 19.6, 10.4, '#f2c895', false),
        mote('10.4%', 3, 2, 21.8, 11.7, '#ffd9a5', true),
        mote('16.3%', 2, 1, 13.0, 13.0, '#f2c895', false),
        mote('22.2%', 2, 2, 15.2, 14.3, '#f2c895', false),
      ],
    },
  },

  mascot: Baller,

  /**
   * The keyframes this theme's own layers and its player animate on.
   *
   * They live here rather than in `index.css` because they are this theme's and no other's — they
   * are injected with it and torn down with it, so a theme with no dust in it does not carry the
   * definition of a mote. Prefixed `bb-` for the same reason every rule in a theme sheet has to be
   * scoped: this string is a global while it is mounted. `Baller.tsx` animates on `bb-lean` too, and
   * can, because it is only ever drawn under this theme.
   *
   * The two crossings are written in `vw` and `vh` rather than the mockup's pixels: they climbed
   * a 1200px card, and this has to cross whatever the window is. The overshoot at each end is what
   * keeps a speck from appearing and vanishing at the edges instead of entering and leaving.
   *
   * The reduced-motion block redefines rather than disables. A later `@keyframes` of the same name
   * wins, so an empty one leaves the floodlights at their own static styles and the player standing
   * straight, and a transparent one parks the dust out of sight — which is the honest answer,
   * because a speck of dust stopped in mid-air is a bug.
   */
  css: `
@keyframes bb-blow{0%{transform:translate(-60px,0) scale(.7);opacity:0}10%{opacity:.9}82%{opacity:.55}100%{transform:translate(calc(100vw + 60px),-9vh) scale(.4);opacity:0}}
@keyframes bb-blow2{0%{transform:translate(-60px,0) scale(.6);opacity:0}13%{opacity:.8}80%{opacity:.45}100%{transform:translate(calc(100vw + 60px),7vh) scale(.35);opacity:0}}
@keyframes bb-flood{0%,100%{opacity:.62;transform:scale(1) translateY(0)}28%{opacity:.95;transform:scale(1.09) translateY(-1px)}56%{opacity:.72;transform:scale(.97) translateY(1px)}}
@keyframes bb-lean{0%,100%{transform:rotate(-4deg) translateY(0)}50%{transform:rotate(4deg) translateY(-1.5px)}}
@media (prefers-reduced-motion: reduce){
@keyframes bb-blow{from,to{opacity:0}}
@keyframes bb-blow2{from,to{opacity:0}}
@keyframes bb-flood{}
@keyframes bb-lean{}
}`,
};
