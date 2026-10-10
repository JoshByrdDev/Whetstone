import type { CSSProperties } from 'react';
import type { BackdropLayer, Theme } from '../tokens';
import { Bruin } from '../../components/mascots/Bruin';
// A photograph of a snowboarder on an open slope above Tignes, vendored rather than hot-linked, for
// the reason the keep's wall and the coast are: a local dashboard whose floor is a third party's CDN
// has no floor when the network is down. Robert Bye / Unsplash, free for commercial use under the
// Unsplash licence. Imported rather than dropped in `public/` so the build emits it
// content-addressed next to the bundles — cached immutably, and impossible to leave behind if this
// theme ever goes.
import slope from './snow-rider.jpg';

/**
 * SNOW — the same board again, drawn on a piste at altitude in flat winter light.
 *
 * The fourth theme, and the nearest neighbour of the third, which is why the differences between
 * them are the interesting part. Beach Mountain is a *warm* landscape seen from above: haze
 * carries the depth, the accent is the one saturated thing in the frame, and the only warm colour
 * in it is the alarm. Snow has no haze to work with. A slope in flat light is one enormous
 * near-white surface, so depth stops coming from how pale a thing is and starts coming from how
 * *blue* it is — which is why this ramp cools rather than lightens as it lifts, why the accent is
 * sky rather than water, and why the two lights in the frame are cold on both sides instead of cold
 * against warm.
 *
 * The other thing the snow decides is the weather. The coast's birds cross the frame and leave;
 * snow falls through it continuously and never arrives anywhere, so it is the one theme whose
 * motion has no direction to read — which is exactly what makes it safe to leave running behind a
 * board someone is trying to read.
 *
 * Taken from the Claude Design board mockup, `Board Theme.dc.html` option 3a, on background A
 * ("Snowboarder in the clouds") of `Snow Backgrounds.dc.html`. Three deliberate departures from it
 * are noted where they happen: the still/animated split in `backdrop`, the speed of the two light
 * pools, and the station glyph's snow cap.
 */

/**
 * The near piste, traced off the photograph as a closed outline in its own pixels.
 *
 * Fifteen points along the skyline where the slope under the rider meets the valley behind him,
 * running from the left edge down to where the rocky spine leaves the bottom of the frame, then
 * closed off below and to the left so the fill is the whole foreground and nothing else.
 *
 * Written in the file's own coordinates, which here are 1920 by 1439 and *not* the 1920 by 1280
 * the other two photographic themes are cut in. That is the one number in this file that has to be
 * checked rather than assumed: the mask is scaled `cover` onto the same box as the photograph, so a
 * viewBox of the wrong aspect ratio does not fail, it slides — the outline lands somewhere
 * plausible and wrong, and the only symptom is a cut that does not follow the ridge.
 *
 * Traced by measurement rather than by eye: a column-wise scan for the strongest light-to-dark step
 * within thirty pixels of a first guess. Worth the trouble, because eye-tracing a snow ridge is
 * harder than eye-tracing a cliff — there is no dark against bright to stop on — and the scan
 * caught a crest three visual passes had put twenty-five pixels low between x=100 and x=250.
 */
const PISTE =
  'M-80 878L0 890L100 908L200 924L300 947L400 1006L500 1042L600 1082L700 1108L800 1158'
  + 'L900 1222L1000 1282L1100 1330L1200 1382L1290 1439L1290 1520L-80 1520Z';

/**
 * The outline above, packed into a `mask-image`. White on nothing: the mask reads the alpha.
 *
 * Blurred harder than the coast's cliff — nine against seven — because half of this edge is snow
 * meeting snow rather than rock meeting sea. A hard cut between two whites a few per cent apart is
 * a drawn line; a soft ramp between them is a change of distance.
 */
const PISTE_MASK = `url("data:image/svg+xml,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1920 1439">'
  + '<filter id="f" x="-8%" y="-8%" width="116%" height="116%">'
  + '<feGaussianBlur stdDeviation="9"/></filter>'
  + `<path filter="url(#f)" fill="#fff" d="${PISTE}"/></svg>`,
)}")`;

/**
 * One falling flake.
 *
 * `left` is a percentage of the viewport rather than the mockup's pixels — its card was 1320px wide
 * and knew it, and a window is not. Everything else is the mockup's, to the tenth of a second: the
 * twelve flakes are on twelve different fall times and twelve different starting offsets, which is
 * what stops them reading as one curtain coming down on a loop.
 *
 * `drift` picks which of the two falls this one takes — `sn-fall` blows right, `sn-fall2` blows
 * left. Two directions rather than one is what makes it read as wind rather than as gravity.
 */
const flake = (
  left: string, d: number, drift: 1 | 2, dur: number, delay: number, hex: string, glow: boolean,
): BackdropLayer => ({
  style: {
    inset: 'auto', left, top: 0, width: d, height: d, borderRadius: '50%',
    background: hex,
    ...(glow ? { boxShadow: '0 0 6px rgba(230,244,255,.85)' } : {}),
    animation: `sn-fall${drift === 2 ? '2' : ''} ${dur}s linear infinite ${delay}s`,
  } as CSSProperties,
});

/**
 * One of the flake's three arms, turned `deg` degrees about the hub — see `shape.shipMask`.
 *
 * Three arms drawn through the centre rather than six drawn out of it, which is half the strokes
 * for the same drawing and, more usefully, keeps the hub a single crossing instead of six ends
 * piled on one point. The barbs sit at a quarter and a half of the way out, angled back toward the
 * hub the way a real dendrite's do.
 */
const ARM = (deg: number): string =>
  `<g transform="rotate(${deg} 12 12)"><path d="M12 1.6V22.4"/>` +
  '<path d="M12 5.6 9.1 2.8M12 5.6l2.9-2.8M12 11 9.6 8.8M12 11l2.4-2.2"/></g>';

export const snow: Theme = {
  key: 'snow',
  name: 'Snow',
  blurb: 'A slope at altitude: ice glass, a sky-blue accent and snow falling through it.',

  fonts: {
    // Outfit again, and for the same reason the coast picked it: one geometric sans with a wide,
    // even upper case carries both the tracked HUD labels and the prose under them. Two cold
    // landscapes sharing a face is not an oversight — a different face here would be saying that
    // these are different *kinds* of thing, and they are not.
    href:
      'https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700' +
      '&family=JetBrains+Mono:wght@400;600;700&display=swap',
    display: 'Outfit, system-ui, sans-serif',
    body: 'Outfit, system-ui, -apple-system, sans-serif',
    // The mono stays, for the fourth time. A figure column is a figure column in any weather.
    mono: "'JetBrains Mono', ui-monospace, SFMono-Regular, monospace",
  },

  type: {
    // As drawn. The mockup set every label at the widths the space theme uses, to the em.
    track: 1,
    caps: 'uppercase',
  },

  color: {
    void: '#060f18',

    // Colder as it lightens rather than paler: 950 is the shadow in a track, 750 is the lit face of
    // the slope. Alphas are the space theme's, untouched, as in both landscapes before this — they
    // were tuned against this layout, and the mockup's own panels landed on them.
    surface: {
      950: { hex: '#040c14', alpha: 0.92 },
      900: { hex: '#091420', alpha: 0.6 },
      800: { hex: '#10222f', alpha: 0.55 },
      750: { hex: '#1c3446', alpha: 0.6 },
      700: { hex: '#182f40', alpha: 0.55 },
      // The lightest fill, and what `text-gray-600` — the dimmest text in the app — resolves to.
      // The queue's own blue-grey at a third strength, the construction every other theme uses here.
      600: { hex: '#9db7c9', alpha: 0.3 },
    },

    // Every step from 600 up is a colour off the mockup. Compressed at the bottom and open at the
    // top, as in all three themes before it: 700 is a placeholder and 100 is a page title, and the
    // distance between them was never linear.
    text: {
      700: '#6b8194',
      600: '#8794a3',
      500: '#9db7c9',
      400: '#adc6d8',
      300: '#c7dbeb',
      200: '#e3eef8',
      100: '#f6fbff',
    },

    // Pale ice blue at a seventh. Every border in the app is this line; over a photograph of snow it
    // reads as the shadowed edge of a cut in the surface rather than as a rule drawn on top of one.
    hair: { hex: '#b0d6f0', alpha: 0.14 },
    hairsoft: { hex: '#b0d6f0', alpha: 0.08 },
    line: '#b0d6f0',

    panel: [{ hex: '#0a1622', alpha: 0.62 }, { hex: '#060f18', alpha: 0.44 }],
    sunk: { hex: '#050e18', alpha: 0.55 },

    // The accent is the sky. The coast accents with the water because the water is the only
    // saturated thing in it; here the ground is white and the one saturated thing overhead, so the
    // eye is sent up rather than down.
    accent: '#8ad4ff',
    accentText: '#a8e2ff',
    accentTextLit: '#d8f2ff',
    onAccent: '#0a1d2c',
    accentFill: ['#a8e2ff', '#245a80'],

    tier: {
      active: '#8ad4ff',  // sky — Claude is working it
      done: '#5fd4b0',    // meltwater under the ice
      blocked: '#ff7a5c', // piste marker orange, and the only warm thing in the frame
      gate: '#b8a6ff',    // the blue an afternoon shadow on snow actually goes
      queue: '#9db7c9',   // overcast
      backlog: '#8794a3', // exposed rock
    },

    // The three berths, straight off the mockup's dashed dockets: sky, meltwater, and a low sun.
    agents: ['#9adcff', '#5fd4b0', '#ffb27a'],
  },

  shape: {
    // The chamfers are the space theme's, unchanged, for the reason every theme keeps them: the
    // app's entire silhouette — panel, row, button, chip — is built on the cut corner, and changing
    // it would change what fits on a line, which is the one thing a theme may not do.
    cut: r => `polygon(${r}px 0,100% 0,100% 100%,0 100%,0 ${r}px)`,
    cutRow: r =>
      `polygon(${r}px 0,100% 0,100% calc(100% - ${r}px),calc(100% - ${r}px) 100%,0 100%,0 ${r}px)`,
    // A station is a summit, the same twin peaks with a saddle the coast cuts. The mockup drew the
    // top quarter of it white — a snow cap — and that part is not here: the core's fill is graded
    // from the tier colour inside `SectorArc`, and no token reaches it. The glyph is the shape only.
    hex: 'polygon(34% 0,55% 47%,70% 21%,100% 100%,0 100%)',
    // A ticket stays a diamond, for the fourth time. It is drawn at 6px in nine columns of every
    // row; there is nothing else it can be.
    diamond: 'polygon(50% 0,100% 50%,50% 100%,0 50%)',
    // Unused, and worth keeping as the record of what a clip path could do here. The mockup drew
    // the marker as three crossed strokes with barbs on them; one closed outline with no holes in
    // it cannot be three crossed strokes, so this is the same six points filled solid — and filled
    // solid, a snowflake is a star. Three of them over a station read as a rating.
    ship:
      'polygon(50% 0,62% 29.2%,93.3% 25%,74% 50%,93.3% 75%,62% 70.8%,' +
      '50% 100%,38% 70.8%,6.7% 75%,26% 50%,6.7% 25%,38% 29.2%)',
    // The flake as the mockup drew it, now that the marker can carry strokes: three arms crossed
    // through the hub, each with two pairs of barbs, and air between all of them. The barbs are the
    // whole difference — a six-pointed star is a star, and a six-pointed star with barbs is snow,
    // and it is the only detail in the set that changes what the thing *is* rather than how well
    // it is drawn.
    //
    // Stroked at 2.9 in a 24 box, which lands near one and a third pixels at the 11 this is fitted
    // to. Thicker closes the gaps between barb and arm and the flake silts up into the star again;
    // thinner and the barbs anti-alias away, which leaves the star as well. There is not much room
    // between those two, and this sits in the middle of it.
    shipMask:
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#fff"' +
      ' stroke-width="2.9" stroke-linecap="round">' +
      ARM(0) + ARM(60) + ARM(120) + '</svg>',
    radius: { sm: '1px', DEFAULT: '2px', md: '3px', lg: '4px', xl: '6px', '2xl': '8px' },
  },

  backdrop: {
    base: '#08131f',

    /**
     * The slope, painted whether or not the animation is on.
     *
     * Split the way the coast is, and differently from the mockup, which had no such setting:
     * everything that does not move is here — the photograph, the grading that makes it usable as a
     * floor, and the falloff at the edges. Switch the animation off and the weather stops; the
     * mountain stays.
     */
    still: [
      /*
       * The slope itself. The graded scrim over it is not dressing — it is what makes a photograph
       * usable as the floor of a UI: a picture has its own contrast and its own focal point, and the
       * app has to be legible over all of it. The filter does the same job upstream, turning a
       * photograph into a surface before anything is asked to sit on it.
       *
       * A brighter filter than the coast's, and deliberately: snow under a scrim built for a green
       * hillside goes grey, and grey snow is just a dirty screen.
       *
       * `backgroundSize: cover` rather than the mockup's `<img object-fit:cover>` — same crop, and a
       * background that simply does not paint if the file is missing, where a broken `<img>` shows
       * the browser's icon.
       */
      {
        // The photograph takes the smallest share of the pointer in this stack, which is the whole
        // grammar of the effect: what is furthest away moves least. Six pixels at the edge of the
        // screen is barely a shift on its own — it is the gap between this and the forty-two the
        // piste cut out of it takes that the eye reads as depth.
        //
        // What is left in this layer once that cut is made is the sky, the cloud deck and every
        // ridge on the far side of the valley: everything the distance has already flattened into
        // one plane. They can share a plane because at twenty kilometres out they *are* one.
        parallax: 6,
        style: {
          backgroundImage: `url(${slope})`,
          backgroundSize: 'cover',
          // Well down the frame, as the mockup crops it: the rider and the near slope belong under
          // the board, and the sky is the part there is most of and least to see in.
          backgroundPosition: '50% 82%',
          filter: 'saturate(.86) brightness(.78) contrast(1.06)',
        },
      },
      /*
       * The same photograph again, masked to the piste in front of it, travelling seven times as far.
       *
       * The widest gap between two planes cut from one picture anywhere in the app, and this frame
       * is the one that earns it. Every other photographic theme is looking *at* something across a
       * distance; this one is standing on the near plane. The snow under the rider is twenty metres
       * away and the ridge behind him is twenty kilometres, and that is a ratio no amount of grading
       * can state — a theme with no haze in it has to say distance with movement or not at all.
       *
       * It costs almost nothing to be a second copy: same URL, so the browser hands back the same
       * decoded bitmap. A second *use* of the picture rather than a second picture — no new file,
       * no second decode, no shader and no canvas. The depth is bought with a mask and a number.
       *
       * `transform` here is structural and cannot be dropped, for the reason the coast's cannot.
       * Both copies still contain the slope, so the moment the near one slides back across the far
       * one it uncovers the far one's own crest standing a few pixels off the first. The fix is the
       * coast's: bias this copy permanently outboard by more than the two can ever differ. This
       * silhouette is attached to the left and bottom edges and faces up and right along its whole
       * length, which is what makes a bias possible at all — and it also makes the sum a short one,
       * because biasing by the full horizontal gap and its three fifths vertically clears every
       * angle of such an outline at once. Thirty-six and twenty-two are therefore the floor — and it
       * is a floor that clears by exactly nothing: at the worst corner of the pointer the two edges
       * meet rather than pass. Fifty-two and thirty-five is that floor with somewhere to stand, and
       * the size of the margin is set by the mask rather than picked: a nine-pixel blur ramps over
       * some fourteen, a clearance smaller than that ramp is a half-transparent near edge with the
       * far crest legible through it, and this leaves sixteen to nineteen at every angle.
       *
       * It is also bounded from above, by the wrapper the whole stack is slid around in. Travel plus
       * bias is what has to fit inside `PARALLAX_BOX`, and this layer is the one that sets it.
       *
       * The price is a crest drawn some fifty pixels further up its own slope than the photograph
       * has it, and here that price is close to nothing: a ridge line moved along a frame with no
       * straight edge anywhere in it to measure against is a ridge line nobody can catch out.
       *
       * Graded apart from the copy underneath, in the direction this theme has argued for since its
       * first paragraph. There is no haze here to carry distance, so distance is carried by *blue* —
       * which makes the near thing the one that has not gone blue yet. Less saturation, not more,
       * which is the opposite of what the coast does to the same pair of layers, and it is the same
       * observation both times: take back out whatever the air has put in.
       *
       * It may be graded apart at all only because this mask hugs what it was cut from. A mask grown
       * *past* its subject may not — see the basketball hoop, where the grown band is sky, and a
       * darker grade on it reads as a shadow drawn round the whole assembly.
       */
      {
        parallax: 42,
        style: {
          backgroundImage: `url(${slope})`,
          backgroundSize: 'cover',
          backgroundPosition: '50% 82%',
          filter: 'saturate(.72) brightness(.84) contrast(1.12)',
          maskImage: PISTE_MASK,
          WebkitMaskImage: PISTE_MASK,
          maskSize: 'cover',
          WebkitMaskSize: 'cover',
          maskPosition: '50% 82%',
          WebkitMaskPosition: '50% 82%',
          maskRepeat: 'no-repeat',
          WebkitMaskRepeat: 'no-repeat',
          transform: 'translate3d(52px,-35px,0)',
        } as CSSProperties,
      },
      { style: { background: 'linear-gradient(180deg,rgba(6,16,26,.8) 0%,rgba(6,16,26,.66) 13%,rgba(7,17,27,.34) 26%,rgba(7,17,27,.32) 46%,rgba(6,16,26,.5) 60%,rgba(6,15,24,.54) 78%,rgba(5,11,18,.66) 100%)' } },
      // The glare off the snow, put back after the scrim took it out. Overlay rather than a plain
      // fill, so it lifts what the photograph already has lit instead of washing the slope flat.
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
      // Light coming back up off the piste, off the bottom of the screen.
      { style: { background: 'radial-gradient(58% 34% at 50% 108%,rgba(170,215,245,.16),rgba(90,150,180,.07) 42%,transparent 72%)' } },
      { style: { background: 'radial-gradient(48% 40% at 50% 78%,rgba(150,205,230,.12),transparent 70%)' } },
      { style: { inset: 'auto', left: 0, right: 0, bottom: 0, height: 170, background: 'linear-gradient(0deg,rgba(6,14,22,.78),transparent)' } },
      // Vignette, then a grain over it. `zIndex` lifts both above the flakes and the light pools,
      // which are painted after them — weather sits *inside* the frame's falloff, not on top of it.
      { style: { zIndex: 1, background: 'radial-gradient(118% 76% at 50% 24%,transparent 24%,rgba(6,14,22,.34) 70%,rgba(5,11,18,.7) 100%)' } },
      { style: { zIndex: 1, mixBlendMode: 'overlay', background: 'repeating-linear-gradient(0deg,rgba(255,255,255,.012) 0 1px,transparent 1px 3px)' } },
    ],

    /**
     * The weather. Fourteen CSS planes rather than a shader, for the reason the keep's fire and the
     * coast's birds are: no GPU work, no second canvas, and every one of them animating opacity and
     * transform only, which is the pair a compositor can do without touching layout.
     */
    motion: {
      kind: 'layers',
      layers: [
        /*
         * Two pools of light, both cold — one off the snow on the left, one off the sky on the
         * right. The coast puts a warm pool against a cold one because it has a sun in it; this has
         * flat overcast light and nothing to be warm.
         *
         * The mockup ran both on the keep's 3.1s and 3.9s torch flicker, which is the one value it
         * inherited rather than chose — a slope does not gutter. Same two pools, same positions, at
         * fourteen and eighteen seconds, which is the rate a cloud crosses the light at.
         */
        {
          // Between the two planes cut out of the photograph, which is where light hanging in a
          // valley belongs: in front of the far wall, behind the slope you are standing on. The
          // pools are also the layer that can most afford the travel — soft-edged radial gradients
          // with no detail to smear, so they can be moved a long way before anyone can see them
          // move.
          parallax: 26,
          style: {
            inset: 'auto', left: '-18%', top: '6%', width: 760, height: 760,
            background: 'radial-gradient(circle,rgba(150,215,240,.2),rgba(90,170,205,.09) 32%,rgba(8,18,28,0) 66%)',
            animation: 'sn-drift 14s ease-in-out infinite',
          },
        },
        {
          parallax: 26,
          style: {
            inset: 'auto', right: '-18%', top: '26%', width: 740, height: 740,
            background: 'radial-gradient(circle,rgba(160,210,245,.18),rgba(100,165,205,.08) 32%,rgba(8,18,28,0) 66%)',
            animation: 'sn-drift 18s ease-in-out infinite 3s',
          },
        },
        // Twelve flakes, spread across the frame and out of step with each other. Twelve is the
        // mockup's count over a 1320px card, kept rather than scaled up: this has to sit behind a
        // board somebody is reading, and the point at which snow stops being weather and starts
        // being a screensaver is a lot closer than it looks.
        //
        // None of them is parallaxed, and that is a cost decision rather than a depth one. A flake
        // falling past the lens is the nearest thing in the frame by a long way, and ought to take
        // a larger share of the pointer than anything else here — but every parallaxed layer is
        // given a wrapper the size of the viewport and promoted to a compositor buffer of its own,
        // and twelve full-screen buffers to move twelve four-pixel dots is not a trade this theme
        // can make. The fall already says which side of the glass they are on.
        flake('4.5%', 4, 1, 7.0, 0, '#eef7ff', true),
        flake('13.1%', 3, 2, 8.4, 1.3, '#d3e6f5', false),
        flake('21.7%', 5, 1, 9.8, 2.6, '#f4fbff', true),
        flake('30.2%', 3, 2, 11.2, 3.9, '#d3e6f5', false),
        flake('38.8%', 4, 1, 12.6, 5.2, '#eef7ff', true),
        flake('47.3%', 3, 2, 14.0, 6.5, '#d3e6f5', false),
        flake('55.9%', 5, 1, 7.0, 7.8, '#f4fbff', true),
        flake('64.5%', 4, 2, 8.4, 9.1, '#eef7ff', true),
        flake('73%', 3, 1, 9.8, 10.4, '#d3e6f5', false),
        flake('81.6%', 4, 2, 11.2, 11.7, '#eef7ff', true),
        flake('90.2%', 3, 1, 12.6, 13.0, '#d3e6f5', false),
        flake('9.3%', 5, 2, 14.0, 14.3, '#f4fbff', true),
      ],
    },
  },

  mascot: Bruin,

  /**
   * The keyframes this theme's own layers and its bear animate on.
   *
   * They live here rather than in `index.css` because they are this theme's and no other's — they
   * are injected with it and torn down with it, so a theme with no snow in it does not carry the
   * definition of a snowflake. Prefixed `sn-` for the same reason every rule in a theme sheet has
   * to be scoped: this string is a global while it is mounted. `Bruin.tsx` animates on `sn-nod`
   * too, and can, because it is only ever drawn under this theme.
   *
   * The two falls are written in `vh` rather than the mockup's pixels: they fell down a 1240px card,
   * and this has to fall past whatever the window is. They start above the top edge and end below
   * the bottom one, so a flake enters and leaves rather than appearing and vanishing.
   *
   * The reduced-motion block redefines rather than disables. A later `@keyframes` of the same name
   * wins, so an empty one leaves the light pools at their own static styles and the bear's head
   * straight, and a transparent one parks the flakes out of sight — which is the honest answer,
   * because a snowflake stopped in mid-air is a bug.
   */
  css: `
@keyframes sn-fall{0%{transform:translate(0,-30px);opacity:0}8%{opacity:1}92%{opacity:.9}100%{transform:translate(42px,112vh);opacity:0}}
@keyframes sn-fall2{0%{transform:translate(0,-30px);opacity:0}10%{opacity:.9}90%{opacity:.7}100%{transform:translate(-52px,112vh);opacity:0}}
@keyframes sn-drift{0%,100%{opacity:.7;transform:scale(1)}50%{opacity:1;transform:scale(1.07)}}
@keyframes sn-nod{0%,100%{transform:rotate(-4deg)}50%{transform:rotate(4deg)}}
@media (prefers-reduced-motion: reduce){
@keyframes sn-fall{from,to{opacity:0}}
@keyframes sn-fall2{from,to{opacity:0}}
@keyframes sn-drift{}
@keyframes sn-nod{}
}`,
};
