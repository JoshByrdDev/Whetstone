import type { CSSProperties } from 'react';
import type { BackdropLayer, Theme } from '../tokens';
import { Familiar } from '../../components/mascots/Familiar';
// A painting of a gothic city at night, vendored rather than hot-linked, for the reason the coast
// and the piste are: a local dashboard whose floor is a third party's CDN has no floor when the
// network is down. Imported rather than dropped in `public/` so the build emits it
// content-addressed next to the bundles — cached immutably, and impossible to leave behind if this
// theme ever goes.
//
// The one asset in the set that is not an Unsplash photograph, and this comment says so because the
// licence is not the same one. It is signed inside the frame — A. Rocha 2017, for Andreas Rocha —
// and was taken from a mirror rather than from the artist, so there is no grant behind it the way
// there is behind the other four. Whether that is acceptable is a decision for whoever ships this;
// what the file can do is make sure nobody later mistakes it for the free-for-commercial-use pile
// the rest came from. The signature is in the bottom-left of the picture and has been left there.
import city from './keep-city.jpg';

/**
 * KEEP — the same board, drawn as a city burning its lamps under a storm.
 *
 * The second theme, and the one that proves the first was a theme rather than the only look. Not a
 * line of layout moved to make it: every value below lands in the same slot a space value already
 * occupied, and the pages that read them do not know either theme exists.
 *
 * The idea the values serve: the space theme is lit from everywhere at once — a sky behind glass.
 * This one is lit from two torches and a hearth, so brightness is a statement about *distance from
 * the fire*. That is why the surface ramp warms as it lightens instead of just getting paler, why
 * the accent is the same gold the space theme uses but sitting on brown rather than blue, and why
 * the mascot's eyes glow instead of its body: the light here comes from inside things.
 *
 * That sentence outlived the picture it was written for. This theme was a photograph of a vaulted
 * undercroft for most of its life, and the swap to a painted city changed the scale of the claim
 * and nothing else: the glow still comes out of the building rather than falling on it, and it is
 * still the only warm thing in a cold frame. What the swap did change is that the backdrop can
 * finally carry depth — an interior three metres deep never could. See the cut in 'backdrop'.
 *
 * Taken from the Claude Design board mockup, `Board Theme.dc.html` option 1b. Two deliberate
 * departures from it are noted where they happen: the still/animated split in `backdrop`, and the
 * body face's size, which the mockup nudged and a theme is not allowed to.
 */

/**
 * The near ground, traced off the picture as a closed outline in its own pixels.
 *
 * Twenty-two points along the top of the causeway that runs up to the gate, from where it leaves
 * the right of the frame to where it disappears behind the city's foot, then carried left at a
 * flat height through the black and closed off below so the fill is the whole foreground.
 *
 * The two halves of that line are doing different jobs. From x=960 rightwards it is a real
 * silhouette — the hard shadowed edge where the ledge cuts across the lit plaza, the one boundary
 * in this picture with both high contrast and a shallow enough slope to cut on. Left of x=840 it
 * is not tracing anything: a column scan puts that whole band between fifteen and twenty-five out
 * of two hundred and fifty-five, so there is nothing there for two copies to disagree about, and
 * the line is drawn flat and low purely to reach the left edge of the frame. Reaching it is worth
 * doing for free — a region attached to three sides has one boundary instead of three, and a
 * single boundary whose normals all point roughly upward is what makes a plain vertical bias
 * enough below.
 *
 * The one thing in that black worth avoiding is the signature, which sits at x=40..190, y=1020
 * ..1060 and reads seventy where its surroundings read twenty. The line passes above it, so it
 * travels with the near plane in one piece rather than being sheared in half.
 */
const CAUSEWAY =
  'M-80 985L400 985L700 982L820 978L900 972L1000 950L1080 924L1150 913L1200 916'
  + 'L1250 908L1300 906L1350 913L1400 925L1450 935L1500 939L1550 936L1600 932L1650 930'
  + 'L1700 928L1800 930L2000 938L2000 1200L-80 1200Z';

/**
 * The outline above, packed into a `mask-image`. White on nothing: the mask reads the alpha.
 *
 * Blurred softer than the coast's cliff or the piste — five against seven and nine — and for once
 * that is not about hiding the cut. The ramp is a straight addition to the bias below, and the
 * bias is paid for in lit plaza covered up, so it is kept as narrow as the edge will take.
 */
const CAUSEWAY_MASK = `url("data:image/svg+xml,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1920 1080">'
  + '<filter id="f" x="-8%" y="-8%" width="116%" height="116%">'
  + '<feGaussianBlur stdDeviation="5"/></filter>'
  + `<path filter="url(#f)" fill="#fff" d="${CAUSEWAY}"/></svg>`,
)}")`;

/** One rising ember. Positions are the mockup's, read off its 1320px frame as percentages. */
const ember = (
  left: string, bottom: number, size: number, colour: string,
  drift: 1 | 2, seconds: number, delay: number,
): BackdropLayer => ({
  style: {
    inset: 'auto', left, bottom, width: size, height: size, borderRadius: '50%',
    background: colour,
    boxShadow: `0 0 ${size * 2.4}px ${size * 0.8}px rgba(255,150,50,.8)`,
    animation: `mv-ember${drift === 2 ? '2' : ''} ${seconds}s linear infinite ${delay}s`,
  },
});

/**
 * A torch in a bracket on the wall: the iron, the flame, and the light it throws.
 *
 * `y` is where it hangs, as a percentage of the viewport, so the two of them stay on the wall
 * rather than on a 1205px-tall card. Everything else is fixed px — a torch is a torch whatever
 * size the window is.
 */
const torch = (side: 'left' | 'right', y: string, delay: string): BackdropLayer[] => [
  {
    style: {
      inset: 'auto', [side]: 2, top: y, width: 40, height: 10,
      background: 'linear-gradient(180deg,#42311d,#130d07)', borderRadius: 2,
      boxShadow: '0 3px 6px rgba(0,0,0,.75)',
    },
  },
  {
    style: {
      inset: 'auto', [side]: 16, top: `calc(${y} - 26px)`, width: 11, height: 32,
      borderRadius: '50% 50% 40% 40%/66% 66% 34% 34%',
      background: 'linear-gradient(180deg,#fff6d0,#ffb03a 40%,#d4571a)',
      filter: 'blur(1.6px)', animation: `mv-flicker 1.1s ease-in-out infinite ${delay}`,
    },
  },
  {
    style: {
      inset: 'auto', [side]: 3, top: `calc(${y} - 50px)`, width: 38, height: 74,
      borderRadius: '50%',
      background: 'radial-gradient(circle,rgba(255,198,104,.85),rgba(255,150,40,0) 66%)',
      filter: 'blur(6px)', animation: `mv-flicker 1.7s ease-in-out infinite ${delay}`,
    },
  },
];

export const medieval: Theme = {
  key: 'medieval',
  name: 'Medieval',
  blurb: 'A gothic city at night: warm stone, gold leaf and ink. Cinzel capitals over Garamond.',

  fonts: {
    href:
      'https://fonts.googleapis.com/css2?family=Cinzel:wght@400;500;600;700' +
      '&family=EB+Garamond:ital,wght@0,400;0,500;0,600;1,400' +
      '&family=JetBrains+Mono:wght@400;600;700&display=swap',
    // Cinzel is Roman capitals cut in stone, which is exactly what the app already does with its
    // display face — every label is uppercase and tracked. Same treatment, different chisel.
    display: 'Cinzel, Georgia, serif',
    body: "'EB Garamond', Georgia, 'Times New Roman', serif",
    // The mono stays. Numbers are numbers in any century, and a serif figure column would be a
    // legibility loss for no gain in atmosphere.
    mono: "'JetBrains Mono', ui-monospace, SFMono-Regular, monospace",
  },

  type: {
    // As drawn. Cinzel is a capitals face and wants the tracking as much as the HUD sans did — the
    // mockup set every label at the same widths the space theme uses, to the em.
    track: 1,
    caps: 'uppercase',
  },

  color: {
    void: '#0d0906',

    // Warm as it lightens, not just paler: 950 is the back of the hall and 750 is a stone with a
    // torch on it. Alphas are the space theme's, untouched — they were tuned against this layout
    // and the mockup landed within a hundredth of every one of them anyway.
    surface: {
      950: { hex: '#080503', alpha: 0.92 },
      900: { hex: '#140e09', alpha: 0.6 },
      800: { hex: '#1e1710', alpha: 0.55 },
      750: { hex: '#2b2116', alpha: 0.6 },
      700: { hex: '#271e13', alpha: 0.55 },
      // The lightest fill, and what `text-gray-600` — the dimmest text in the app — resolves to.
      // Parchment at a tenth strength rather than a colour of its own, same as the space theme
      // uses its queue blue here.
      600: { hex: '#d6b882', alpha: 0.3 },
    },

    // Every step is a colour off the mockup. The bottom of the ramp is compressed and the top is
    // not, which is true of the space ramp too — 700 is a placeholder, 100 is a page title, and
    // the distance between them is not linear in either theme.
    text: {
      700: '#6e6252',
      600: '#8b7b62',
      500: '#9c8b70',
      400: '#b6a68a',
      300: '#cbbb9d',
      200: '#e4d8bf',
      100: '#f5ecd8',
    },

    // Gold leaf at a fourteenth. Every border in the app is this line; on stone it reads as the
    // gilding on a page edge rather than as a rule.
    hair: { hex: '#d6b882', alpha: 0.14 },
    hairsoft: { hex: '#d6b882', alpha: 0.08 },
    line: '#d6b882',

    panel: [{ hex: '#1c1409', alpha: 0.62 }, { hex: '#140e09', alpha: 0.44 }],
    sunk: { hex: '#070403', alpha: 0.55 },

    // The accent barely moved — the space theme's amber is already a gold. What changed is what it
    // is sitting on, which is most of why the two themes do not look like the same board.
    accent: '#d3a63c',
    accentText: '#e6c470',
    accentTextLit: '#f3dca4',
    onAccent: '#1a1207',
    accentFill: ['#e0b957', '#8a6418'],

    tier: {
      active: '#d3a63c',  // gold leaf — Claude is working it
      done: '#86ad55',    // moss
      blocked: '#d1553a', // rust
      gate: '#b493cf',    // an illuminator's violet
      queue: '#85a1bd',   // slate
      backlog: '#9c8f7c', // unworked stone
    },

    // The three berths: candle, verdigris, and a dye no guild could afford.
    agents: ['#e0b34a', '#5fb3a1', '#c47ba6'],
  },

  shape: {
    // The chamfers are the space theme's, unchanged, and that is a decision rather than an
    // oversight: a cut corner is a mitre as readily as it is a machined bevel, and the app's whole
    // silhouette — every panel, row, button and chip — is built on it. Changing it would be the
    // one thing a theme is not allowed to do, because it would change what fits on a line.
    cut: r => `polygon(${r}px 0,100% 0,100% 100%,0 100%,0 ${r}px)`,
    cutRow: r =>
      `polygon(${r}px 0,100% 0,100% calc(100% - ${r}px),calc(100% - ${r}px) 100%,0 100%,0 ${r}px)`,
    // The two glyphs that were never rectangles are where the century actually shows. A station
    // stops being a hex bolt and becomes a shield; a marker parked above one stops being a ship
    // and becomes a pennant on a staff, notched at the fly.
    hex: 'polygon(0 0,100% 0,100% 56%,50% 100%,0 56%)',
    // A ticket stays a diamond. It is a lozenge on a coat of arms as easily as a waypoint on a
    // chart, and it is drawn at 6px in nine columns of every row — there is nothing else it can be.
    diamond: 'polygon(50% 0,100% 50%,50% 100%,0 50%)',
    ship: 'polygon(0 0,100% 0,60% 35%,100% 70%,20% 70%,20% 100%,0 100%)',
    radius: { sm: '1px', DEFAULT: '2px', md: '3px', lg: '4px', xl: '6px', '2xl': '8px' },
  },

  backdrop: {
    base: '#0a0705',

    /**
     * The hall itself, painted whether or not the animation is on.
     *
     * Split differently from the mockup, which had no such setting: what stays here is everything
     * that does not move — the room, the shadow in its corners and the glow off the hearth. Switch
     * the animation off and you get a lit hall with the fire out of shot. Switch it on and the
     * torches arrive.
     */
    still: [
      /*
       * The city. A painting, where the first cut of this theme drew a wall with four
       * repeating-gradient passes faking courses of stone, and the second used a photograph of a
       * vaulted undercroft.
       *
       * The undercroft was the one photograph in the set that could never take the depth treatment
       * the other three got, and the reason is worth keeping: it is an *interior*. Its near column
       * is a clean enough silhouette to cut, but it stands on flagstones that run unbroken from its
       * own base to the far wall, and a near plane that slides off its own footing is a cut-out. An
       * interior has no distance to spend either — three metres against eight is not a ratio.
       *
       * This has both. Foreground ledge, rooftops, cathedral, far towers, cloud: five stops, and a
       * painter has already put the haze between them, which is the part the coast and the piste
       * had to have invented for them with a `filter`.
       *
       * The graded scrim over it is not optional dressing — it is what makes a picture usable as
       * the floor of a UI. A picture has its own contrast and its own focal point, and the app has
       * to be legible over every part of it. Both scrim and filter are much lighter here than they
       * were over the photograph, and that is the swap paying for itself rather than an oversight:
       * a photograph of a lit room has to be dragged down to become a surface, and this arrived as
       * one. Half brightness over an image whose corners are already near-black is not a scrim, it
       * is switching the picture off.
       *
       * `backgroundSize: cover` rather than an `<img object-fit:cover>` — same crop, and a
       * background that simply does not paint if the file is missing, where a broken `<img>` shows
       * the browser's icon.
       */
      {
        // The picture takes the smallest share of the pointer in this stack, which is the whole
        // grammar of the effect: what is furthest away moves least. Eight pixels at the edge of the
        // screen is barely a shift on its own — it is the gap between this and the twenty-six the
        // light in front of it takes that the eye reads as depth.
        parallax: 8,
        style: {
          backgroundImage: `url(${city})`,
          backgroundSize: 'cover',
          // Below centre, and by less than the photograph wanted. This frame is 16:9 where the rest
          // are 3:2, so on most windows almost all of it is on screen and this number is doing
          // little; what it does decide is which end gets cropped on a window taller than the
          // picture, and the answer is the empty top third of cloud rather than the city.
          backgroundPosition: '50% 58%',
          filter: 'saturate(.86) brightness(.82) contrast(1.04)',
        },
      },
      /*
       * The same picture again, masked to the ground in front of the city, travelling four times
       * as far.
       *
       * One cut, where the coast and the piste each got one and the court got one, and here that is
       * a limit rather than a budget. This frame has five depths in it and only one of them can be
       * separated. The city cannot: its silhouette is spires, and a spire is thin enough that any
       * mask grown wide enough to clear the travel would visibly fatten it — the hoop could take
       * that treatment because a hoop is not twenty pixels wide against the sky. The mid rooftops
       * cannot either, for the opposite reason: they are black on black, so a cut there is safe and
       * pointless. What is left is the causeway, which is the only boundary here that is both
       * visible and cuttable, and it happens to be the one the viewer is standing on.
       *
       * It costs almost nothing to be a second copy: same URL, so the browser hands back the same
       * decoded bitmap. A second *use* of the picture rather than a second picture.
       *
       * The bias is straight up, where the coast and the piste both went up *and out*. Those two
       * silhouettes were attached to one side of the frame and faced one way along their whole
       * length, so a diagonal cleared them. This one reaches both sides, and its normals tilt left
       * over the rise at x=1000 and right over the fall at x=1400, so a horizontal component buys
       * one of them at the price of the other and nets nothing.
       *
       * Straight up is also the one direction that is expensive here, and that is the whole problem
       * this layer had to solve. Outboard means *up into the glow*: there is a lit gateway directly
       * above the cut with a figure standing in it, twenty-six pixels of clear picture between his
       * feet and the ledge. Sized the usual way — the gap times the steepest slope, plus three
       * fifths of the gap for the vertical travel, plus the mask ramp — the bias comes out at
       * thirty-seven and swings between twenty-four and fifty as the pointer moves. Fifty erases
       * him. Not subtly: he is there at one corner of the screen and gone at another, which is the
       * one kind of artifact worse than no depth at all.
       *
       * So this layer spends its travel on the axis that is free. The vertical two thirds of it is
       * handed back — `--w-my` is multiplied by exactly what the wrapper took out, so this copy and
       * the one under it rise and fall together and only ever disagree sideways. That deletes the
       * three-fifths term *and* the swing: what is left is the gap times the steepest slope, which
       * with the left shoulder smoothed to a third is seven, plus thirteen of ramp and trace error.
       * Twenty, and it does not move. Twenty fits under his feet where a wobbling thirty-seven
       * never could.
       *
       * The honest cost is that this plane parallaxes horizontally and not vertically, which no
       * real ground does. It is worth it because the vertical cue was the small half to begin with
       * — the wrapper already scales it to three fifths, on the grounds that a window is wider than
       * it is tall and the pointer crosses it faster sideways — and because the alternative was not
       * a vertical cue, it was no cut at all.
       *
       * Not graded apart, deliberately, and for the reason the hoop is not: the bias means a
       * forty-four pixel band of this copy sits above where its own edge really is, and a band that
       * has been grown past its subject cannot carry a grade of its own without reading as a shadow
       * drawn along the top of it. The painting has aerial perspective in it already — that is what
       * a painter does that a photograph has to have done to it — so there is nothing to add.
       */
      {
        parallax: 30,
        style: {
          backgroundImage: `url(${city})`,
          backgroundSize: 'cover',
          backgroundPosition: '50% 58%',
          filter: 'saturate(.86) brightness(.82) contrast(1.04)',
          maskImage: CAUSEWAY_MASK,
          WebkitMaskImage: CAUSEWAY_MASK,
          maskSize: 'cover',
          WebkitMaskSize: 'cover',
          maskPosition: '50% 58%',
          WebkitMaskPosition: '50% 58%',
          maskRepeat: 'no-repeat',
          WebkitMaskRepeat: 'no-repeat',
          transform: 'translate3d(0,calc(-20px + var(--w-my,0) * 13.2px),0)',
        } as CSSProperties,
      },
      { style: { background: 'linear-gradient(180deg,rgba(24,17,10,.3) 0%,rgba(14,10,6,.2) 40%,rgba(9,6,4,.34) 78%,rgba(6,4,2,.6) 100%)' } },
      // Firelight put back into the frame, after the scrim took it out. Overlay rather than a plain
      // fill, so it lifts what the picture already has lit instead of laying a flat wash of orange
      // over the whole city. Moved off the middle to sit on the cathedral, which is where the light
      // in this frame actually comes from — the old position was the vault's apex in a photograph
      // that is no longer here.
      { style: { mixBlendMode: 'overlay', opacity: 0.5, background: 'radial-gradient(56% 46% at 64% 66%,rgba(255,196,116,.3),transparent 72%)' } },
      // The room falls away at the sides and overhead. Three fixed boxes in the mockup, because it
      // was 1320px wide and knew it; percentages here, because a window is not.
      {
        style: {
          background:
            'radial-gradient(22% 40% at 0% 40%,rgba(0,0,0,.62),rgba(0,0,0,.28) 52%,transparent 76%),' +
            'radial-gradient(22% 40% at 100% 46%,rgba(0,0,0,.62),rgba(0,0,0,.28) 52%,transparent 76%),' +
            'radial-gradient(40% 26% at 50% 0%,rgba(0,0,0,.42),rgba(0,0,0,.16) 58%,transparent 78%)',
        },
      },
      // The glow off the gate, off the bottom of the screen, where the hearth used to be. Same
      // two gradients, and they land on something now: the picture has its own light pooling at the
      // foot of the cathedral, and these sit under it rather than inventing one.
      { style: { background: 'radial-gradient(58% 34% at 50% 108%,rgba(232,132,36,.26),rgba(150,70,16,.1) 42%,transparent 72%)' } },
      { style: { background: 'radial-gradient(48% 40% at 50% 78%,rgba(226,148,52,.16),transparent 70%)' } },
      // Lighter and shorter than the photograph needed. The bottom of this frame is the near ledge and
      // the rooftops in front of the city — the one band that has to survive, because it is what the
      // near plane will be cut from. Nine tenths of black over 170px did not grade it, it deleted it.
      { style: { inset: 'auto', left: 0, right: 0, bottom: 0, height: 140, background: 'linear-gradient(0deg,rgba(7,4,3,.66),transparent)' } },
      // Vignette, then a grain over it. `zIndex` lifts both above the torches and the embers, which
      // are painted after them — the fire has to sit *inside* the room's falloff, not on top of it.
      { style: { zIndex: 1, background: 'radial-gradient(122% 82% at 52% 40%,transparent 30%,rgba(9,6,4,.3) 70%,rgba(7,5,3,.62) 100%)' } },
      { style: { zIndex: 1, mixBlendMode: 'overlay', background: 'repeating-linear-gradient(0deg,rgba(255,255,255,.012) 0 1px,transparent 1px 3px)' } },
    ],

    /**
     * The fire. Twenty CSS planes rather than a shader — no GPU work, no second canvas, and every
     * one of them animating opacity and transform only, which is the pair a compositor can do
     * without touching layout.
     *
     * This is the `layers` half of `Motion` earning its keep: the space theme needed a cloud field
     * that could only be a shader, and this one needs torches, which could only be worse as one.
     */
    motion: {
      kind: 'layers',
      layers: [
        // The two pools of light the torches throw, big and slow.
        {
          // Three times the photograph's travel, because this is the near half of the frame. The
          // pools are also the layer that can afford it: they are soft-edged radial gradients with
          // no detail to smear, so they can be moved a long way before anyone can see them move.
          parallax: 26,
          style: {
            inset: 'auto', left: '-18%', top: '6%', width: 760, height: 760,
            background: 'radial-gradient(circle,rgba(255,178,72,.34),rgba(216,116,30,.15) 32%,rgba(10,7,5,0) 66%)',
            animation: 'mv-flicker 3.1s ease-in-out infinite',
          },
        },
        {
          parallax: 26,
          style: {
            inset: 'auto', right: '-18%', top: '26%', width: 740, height: 740,
            background: 'radial-gradient(circle,rgba(255,166,60,.28),rgba(204,102,24,.12) 32%,rgba(10,7,5,0) 66%)',
            animation: 'mv-flicker 3.9s ease-in-out infinite .7s',
          },
        },
        // The torches themselves, offset in time so they never gutter together.
        ...torch('left', '41%', '0s'),
        ...torch('right', '57%', '.4s'),
        // Embers off the hearth, on two drift paths.
        ember('8.9%', 60, 4, '#ffb347', 1, 8.2, 2.6),
        ember('11.4%', 120, 5, '#ffd489', 1, 6.5, 0),
        ember('14.8%', 90, 4, '#ffc061', 2, 9, 1.2),
        ember('25%', 40, 5, '#ffd489', 2, 10.5, 0.5),
        ember('35.6%', 150, 4, '#ffc061', 1, 7.4, 3.4),
        ember('46.2%', 70, 5, '#ffb347', 2, 11.5, 1.8),
        ember('53%', 180, 4, '#ffd489', 1, 9.8, 4.2),
        ember('59.8%', 50, 4, '#ffc061', 2, 8.8, 2.1),
        ember('68.2%', 130, 5, '#ffb347', 1, 7.9, 0.9),
        ember('77.3%', 60, 4, '#ffd489', 2, 12, 3.7),
        ember('86.4%', 110, 5, '#ffc061', 1, 6.8, 1.4),
        ember('93.2%', 44, 4, '#ffb347', 2, 9.4, 2.9),
      ],
    },
  },

  mascot: Familiar,

  /**
   * The keyframes the backdrop's own layers animate on.
   *
   * They live here rather than in `index.css` because they are this theme's and no other's — they
   * are injected with it and torn down with it, so a theme that has no fire does not carry the
   * definition of one. Prefixed `mv-` for the same reason every rule in a theme sheet has to be
   * scoped: this string is a global while it is mounted.
   *
   * The reduced-motion block redefines rather than disables. A later `@keyframes` of the same name
   * wins, so an empty one leaves the torches at their own static styles, and a flat one parks the
   * embers out of sight — which is the honest answer, because an ember frozen mid-air is a bug.
   */
  css: `
@keyframes mv-flicker{0%,100%{opacity:.62;transform:scale(1) translateY(0)}28%{opacity:.95;transform:scale(1.09) translateY(-1px)}56%{opacity:.72;transform:scale(.97) translateY(1px)}}
@keyframes mv-ember{0%{transform:translate(0,0) scale(.7);opacity:0}10%{opacity:1}70%{opacity:.9}100%{transform:translate(34px,-420px) scale(.35);opacity:0}}
@keyframes mv-ember2{0%{transform:translate(0,0) scale(.6);opacity:0}14%{opacity:.95}72%{opacity:.75}100%{transform:translate(-26px,-560px) scale(.3);opacity:0}}
@media (prefers-reduced-motion: reduce){
@keyframes mv-flicker{}
@keyframes mv-ember{from,to{opacity:0}}
@keyframes mv-ember2{from,to{opacity:0}}
}`,
};
