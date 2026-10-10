import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { STATIONS, COLUMNS, GRID, ACTIVE_DOT_PX, stationsForTracks } from './JourneyTrack';
import { fade, CAPS, TIER, HEX_GLYPH, SHIP_GLYPH, SHIP_PX, DISPLAY, MONO } from './orbit';
import type { Tier } from './orbit';
import type { DashboardState } from '../types/events';
import type { Track } from '../types/tickets';
import { track } from '../theme';

/**
 * The sector arc: the stations of the pipeline, drawn as destinations on a flight path.
 *
 * This is the board's column header. Its nodes are not laid out on their own scale — they are
 * measured off the very grid the rows below use, so a station's node sits exactly above that
 * station's chevron on every row, at any board width. Get that wrong and the whole metaphor breaks:
 * the arc stops being a header and becomes decoration that happens to have nodes on it.
 */

/**
 * Height of each station above the band's floor, by STATION index. Hand-tuned into a flight path,
 * not a formula.
 *
 * The peer-review pair (7, 8) sits well below the rest: a second approach path rather than a detour
 * off the main one. They own no column — each is drawn at the midpoint of the two delivery columns
 * it falls between — so the delivery arc runs its own nine stops undisturbed and the lower lane
 * crosses beneath it without a single node of the two lanes ever sharing an x.
 */
const ARC_Y = [168, 128, 100, 88, 92, 110, 134, 206, 188, 112, 62];

/** The stations each track's path actually passes through. */
const DELIVERY_PATH = [0, 1, 2, 3, 4, 5, 6, 9, 10];
/**
 * Backlog, the two review stations, and then the gate — where the last point is station 9, the SAME
 * point the delivery arc lands on. An identical endpoint is what makes this read as a merge into
 * the gate rather than a second line running alongside it, which is exactly what the two tracks do:
 * both finish by handing the work to a person at NeedsReview.
 */
const REVIEW_PATH = [0, 7, 8, 9];

/**
 * The review approach standing alone, when a project runs peer review and nothing else: with no
 * delivery arc to merge into, it has to carry on to Done itself.
 */
const REVIEW_ONLY_PATH = [0, 7, 8, 9, 10];

/**
 * Tall enough for the band's lowest node plus the room a label needs under it — the same 46px of
 * slack it has always kept under Backlog.
 *
 * Two values, because the peer-review pair sits below everything on the delivery arc. This is NOT
 * the "hide the lane when nobody is using it" rule that was tried and rejected: a project that RUNS
 * the review track always draws it, however empty, exactly as Approve and QA draw stone cold at
 * zero. Only a project that does not run the track at all gets those 38px back.
 */
const BAND_FULL = 252;
const BAND_DELIVERY_ONLY = 214;

/**
 * How far a node may swell, and the room kept above the arc for the biggest one to swell into.
 *
 * The board's scroller is `overflow-x: auto`, and CSS will not let one axis scroll while the other
 * stays visible - it is quietly clipping the top edge too. Done sits highest on the flight path and
 * is the one station whose count only ever climbs, so it was the first to grow its halo straight
 * through that edge and come out with a razor-cut top.
 *
 * So the node saturates rather than scales: near-identical to the old linear ramp over the first
 * few tickets, where the difference between one and three is the thing worth reading, and easing
 * to a ceiling past that, where the difference between twenty and forty is not. A bounded node is
 * what turns the headroom below into a fixed number instead of a bet on how much work finishes.
 */
const NODE_MIN = 15;
const NODE_MAX = 34;
/** The tallest thing a node paints above its centre is the ship row, at `size + 24` plus its glow;
    with Done at y=62 that leaves this much to find above the band for the largest node to clear. */
const HEADROOM = 18;

/** Smooth cubic through the points, with horizontally-flat tangents at each station. */
function smoothPath(pts: [number, number][]): string {
  if (pts.length < 2) return '';
  let d = `M ${pts[0][0]} ${pts[0][1]}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const [x0, y0] = pts[i];
    const [x1, y1] = pts[i + 1];
    const cx = (x0 + x1) / 2;
    d += ` C ${cx} ${y0}, ${cx} ${y1}, ${x1} ${y1}`;
  }
  return d;
}

/**
 * How the lane is drawn, by what the fleet is actually doing.
 *
 * The crawl was decoration for as long as it ran at one speed: it said "this is a pipeline" and
 * nothing else, including while nothing at all was moving through it. Bound to the heartbeat it
 * becomes the board's largest status light, readable before you have read a single row. Speed
 * carries the state and the colour is left alone between the two live modes, so a resting fleet
 * still looks like the same lane running slowly rather than a different lane.
 */
type LaneMode = 'working' | 'resting' | 'stopped';

const LANE: Record<LaneMode, { stroke: string; dur: string; width: number; glow: string }> = {
  // What the arc has always looked like. The other two are departures from this, not variants.
  working: { stroke: 'var(--w-accent)', dur: '6s',  width: 1.75, glow: 'drop-shadow(0 0 5px rgb(var(--w-accent-rgb) / .65))' },
  // Same lane, same yellow, a third of the pace: the fleet is up, it just is not carrying anything.
  resting: { stroke: 'var(--w-accent)', dur: '19s', width: 1.7,  glow: 'drop-shadow(0 0 5px rgb(var(--w-accent-rgb) / .5))' },
  // Nothing is coming through. Held still and dimmed to a cold ochre - the route is still drawn,
  // because the pipeline still exists; it is simply not running.
  stopped: { stroke: 'var(--w-active-700)', dur: '0s',  width: 1.5,  glow: 'none' },
};

/**
 * `error` rests rather than stops: the heartbeat process is still up - a stopped one is reported as
 * `stopped`, reconciled against process liveness on the server - and freezing the lane for a failed
 * cycle would claim the fleet is down when it is about to try again.
 */
function laneMode(status: DashboardState['status']): LaneMode {
  if (status === 'stopped') return 'stopped';
  if (status === 'running') return 'working';
  return 'resting';
}

export interface StationLoad {
  /** Tier of every ticket parked at this station, in board order. One ship each. */
  tiers: Tier[];
  /** Whether an agent is holding that ticket right now, by the same index. Drives ship drift. */
  claimed: boolean[];
}

/**
 * An agent has just said something while working at this station.
 *
 * `key` is a counter for the same reason the arrival burst's is: two lines in a row from the same
 * agent are two events, and a flag already standing at true would have nothing to say about the
 * second one. The colour is the agent's own, so a station lighting up says which of the three is
 * standing there without anything having to be read.
 */
export interface StationPulse {
  key: number;
  colour: string;
}

/**
 * The flash itself, kept in its own component so that each node owns the clock for its own pulse.
 *
 * Mounted by the pulse rather than toggled on a permanent element, and keyed on the counter: a
 * flash still on screen when the next line lands is replaced by a fresh one starting from zero,
 * which is what lets a chatty agent read as a rapid stutter instead of one long smear.
 */
function NodeFlash({ pulse, size }: { pulse?: StationPulse; size: number }) {
  const [shot, setShot] = useState<StationPulse | null>(null);
  useEffect(() => {
    if (!pulse?.key) return;
    setShot(pulse);
    const h = setTimeout(() => setShot(null), 430);
    return () => clearTimeout(h);
  }, [pulse?.key, pulse?.colour]);

  if (!shot) return null;
  return (
    <span
      key={shot.key}
      className="node-flash"
      style={{
        position: 'absolute', left: -size * 1.9, top: -size * 1.9, width: size * 3.8, height: size * 3.8,
        borderRadius: '50%',
        background: `radial-gradient(circle,${fade(shot.colour, 80)},${fade(shot.colour, 13)} 45%,transparent 68%)`,
        pointerEvents: 'none',
      }}
    />
  );
}

export function SectorArc({ load, tracks, status = 'idle', pulses, insetLeft = 0, insetRight = 0, onHoverStation, selected, onToggleStation, burst }: {
  load: StationLoad[];
  /**
   * Which tracks this board runs, from the per-project setting. Decides which lanes and which nodes
   * get drawn — never how wide anything is, since the review stops borrow their positions from the
   * delivery columns rather than claiming any of their own.
   */
  tracks: readonly Track[];
  /**
   * The latest heartbeat line at each station, by station index. The arc's answer to "is anything
   * actually happening": the lane says the fleet is up, the ships say what is parked where, and
   * this is the only thing on the header that moves because a specific agent just did something.
   */
  pulses?: ReadonlyMap<number, StationPulse>;
  /** What the heartbeat is doing. The lane's pace and colour are read off it. */
  status?: DashboardState['status'];
  /**
   * A ticket has just arrived at a station. `key` is a counter rather than a flag, and that is
   * what makes this an event: the same station can take two deliveries in a row, and a boolean
   * already standing at true would have nothing to say about the second one.
   */
  burst?: { station: number; key: number } | null;
  /**
   * Called with the station under the pointer, and null when it leaves. The board uses it to pick
   * the rows out of the list below — a count on a node says how many are there, and this is what
   * answers the question it immediately provokes: which ones.
   */
  onHoverStation?: (station: number | null) => void;
  /**
   * Stations currently held in the board's stage filter. Hovering asks "which ones"; clicking
   * keeps the answer, and this is the set that has been kept.
   */
  selected?: ReadonlySet<number>;
  /** Click, or Enter/Space: add this station to the filter, or drop it if it is already in. */
  onToggleStation?: (station: number) => void;
  /** Left inset of a row's grid within this band: the board gutter plus the row shell's own
      padding and border. Without it the probe resolves wider columns than the rows do and every
      node drifts a little further right than the chevron it is supposed to sit above. */
  insetLeft?: number;
  insetRight?: number;
}) {
  const band = useRef<HTMLDivElement>(null);
  // Which approaches there are to draw. A project can switch a track off entirely, so the arc
  // cannot assume there are two lanes — or even that delivery is one of them.
  const shown = useMemo(() => stationsForTracks(tracks), [tracks]);
  const reviewShown = STATIONS.some((s, i) => s.track === 'review' && shown.has(i));
  const deliveryShown = STATIONS.some((s, i) => s.track === 'delivery' && shown.has(i));
  const bandH = reviewShown ? BAND_FULL : BAND_DELIVERY_ONLY;

  const probes = useRef<(HTMLSpanElement | null)[]>([]);
  const [xs, setXs] = useState<number[]>([]);
  const [width, setWidth] = useState(0);

  // The probe row is a zero-height copy of the row grid. Reading the real columns beats
  // recomputing them from the template: gaps, padding and the horizontal scroll all move the
  // columns, and a duplicated formula would drift from the rows the first time any of that changed.
  //
  // Measured once and on resize, with no dependency on which tracks are shown: the columns are the
  // same nine whatever a project runs, because the review stops are drawn BETWEEN them rather than
  // in columns of their own. Switching a track off changes what is painted, never where.
  useLayoutEffect(() => {
    const measure = () => {
      const box = band.current?.getBoundingClientRect();
      if (!box) return;
      setWidth(box.width);
      setXs(probes.current.map(p => (p ? p.getBoundingClientRect().left - box.left + ACTIVE_DOT_PX / 2 : 0)));
    };
    measure();
    const ro = new ResizeObserver(measure);
    if (band.current) ro.observe(band.current);
    window.addEventListener('resize', measure);
    return () => { ro.disconnect(); window.removeEventListener('resize', measure); };
  }, []);

  // An arrival is a moment, not a state. The counter says another ship has come in; this holds the
  // node it came in at lit for as long as the shockwave takes to leave, then lets it go cold again.
  const [live, setLive] = useState(0);
  useEffect(() => {
    const key = burst?.key ?? 0;
    if (!key) return;
    setLive(key);
    const h = setTimeout(() => setLive(0), 760);
    return () => clearTimeout(h);
  }, [burst?.key]);

  const measured = xs.length === COLUMNS;
  // A station's x on the band: its own measured column, or - for one that owns no column - the
  // midpoint of the two it falls between. The same arithmetic the rows do with two equally flexed
  // half-segments, which is what keeps a node above the chevron it belongs to at any board width.
  const nodeX = (i: number) => {
    const at = STATIONS[i].at;
    return typeof at === 'number' ? xs[at] : (xs[at[0]] + xs[at[1]]) / 2;
  };
  const trace = (stops: number[]) => smoothPath(stops.map(i => [nodeX(i), ARC_Y[i]] as [number, number]));
  // The main lane is the delivery arc — unless this board does not run delivery at all, in which
  // case the review approach is the only route through and has to carry the crawl itself.
  const path = !measured ? ''
    : deliveryShown ? trace(DELIVERY_PATH)
    : reviewShown ? trace(REVIEW_ONLY_PATH)
    : '';
  // The second approach, drawn only when there is a main lane for it to merge into.
  const spurPath = measured && deliveryShown && reviewShown ? trace(REVIEW_PATH) : '';
  const mode = laneMode(status);
  const lane = LANE[mode];

  return (
    <div ref={band} className="relative" style={{ height: bandH, marginTop: HEADROOM }}>
      {/* Column probe — invisible, unclickable, and the only thing that knows where a column is.
          One span per COLUMN, not per station: the two review stops have no column to probe and
          take their x from the average of the two spans either side of them instead. */}
      <div
        style={{ ...GRID, position: 'absolute', inset: '0 0 auto 0', height: 0, paddingLeft: insetLeft, paddingRight: insetRight, visibility: 'hidden', pointerEvents: 'none' }}
        aria-hidden
      >
        <span />
        {Array.from({ length: COLUMNS }, (_, i) => (
          <span key={i} ref={el => { probes.current[i] = el; }} style={{ width: ACTIVE_DOT_PX }} />
        ))}
        <span />
      </div>

      {path && (
        <svg
          viewBox={`0 0 ${width} ${bandH}`}
          width={width}
          height={bandH}
          style={{ position: 'absolute', left: 0, top: 0 }}
          aria-hidden
        >
          {/* The peer-review approach, drawn FIRST so the main lane paints over the junction: the
              two share their last point, and whichever is drawn second owns how the join looks.
              Narrower and quieter throughout — it is the shorter path and the rarer one, and a
              second lane at equal weight would read as the pipeline having split in two. */}
          {spurPath && (
            <>
              <path d={spurPath} fill="none" stroke="rgb(var(--w-line-rgb) / .10)" strokeWidth={16} strokeLinecap="round" />
              <path d={spurPath} fill="none" stroke="rgb(var(--w-line-rgb) / .16)" strokeWidth={1} />
            </>
          )}
          {/* Three passes over one path: the lane, its edge, and the crawl that says it flows. */}
          <path d={path} fill="none" stroke="rgb(var(--w-line-rgb) / .13)" strokeWidth={24} strokeLinecap="round" />
          <path d={path} fill="none" stroke="rgb(var(--w-line-rgb) / .2)" strokeWidth={1.25} />
          {/* The crawl, wearing the fleet's state. The transition is what keeps a status change from
              reading as a glitch: the lane eases between paces and colours instead of jumping, so
              waking up looks like the lane picking up rather than the header re-rendering. */}
          <path
            d={path}
            fill="none"
            stroke={lane.stroke}
            strokeWidth={lane.width}
            strokeDasharray="6 14"
            className="anim-arcdash"
            data-flow={mode}
            style={{
              filter: lane.glow,
              ['--arcdur' as string]: lane.dur,
              transition: 'stroke .8s ease, stroke-width .8s ease, filter .8s ease',
            }}
          />
        </svg>
      )}

      {STATIONS.map((st, i) => {
        // A station on a track this board does not run: its column is zero-wide, so a node here
        // would stack on top of its neighbour rather than sit in a gap of its own.
        if (!shown.has(i)) return null;
        const tiers = load[i]?.tiers ?? [];
        const claimed = load[i]?.claimed ?? [];
        // The node wears the tier of whatever is parked there, so an empty station reads as cold.
        const c = TIER[tiers[0] ?? 'backlog'].c;
        const size = tiers.length ? NODE_MAX - (NODE_MAX - NODE_MIN) * Math.exp(-tiers.length / 5.4) : 10;
        // The two ends of the line, where a ticket is not under way: it has not left Backlog, and
        // it is not going anywhere from Done. Both are holding pens rather than stages, and both
        // fill up without limit, which is what makes them the wrong places to draw a fleet.
        const terminus = i === 0 || i === STATIONS.length - 1;
        const x = nodeX(i);
        if (x == null) return null;

        const on = !!selected?.has(i);
        const landing = live !== 0 && burst?.station === i;
        // Dimmed only while some *other* station is holding the filter. With nothing filtered the
        // arc is at rest and every node reads the same, which is what makes the first click legible.
        const off = !!selected?.size && !on;

        return (
          <div
            key={st.label}
            className="orbit-node"
            style={{ position: 'absolute', left: x, top: ARC_Y[i], width: 0, height: 0, cursor: 'pointer' }}
            title={`${st.label} — ${tiers.length} ticket${tiers.length === 1 ? '' : 's'} · ${on ? 'click to drop from the filter' : 'click to keep only this stage'}`}
            data-on={on ? '1' : undefined}
            data-off={off ? '1' : undefined}
            // A node taking a delivery is lit the way a hovered one is, because it is making the
            // same statement: this is the station you are being told about.
            data-burst={landing ? '1' : undefined}
            onMouseEnter={() => onHoverStation?.(i)}
            onMouseLeave={() => onHoverStation?.(null)}
            // Focus is treated as hover for the same reason the rows do it: the spotlight is the
            // only thing that says which tickets a node stands for, and tabbing here without it
            // would leave you toggling a filter you cannot see the subject of. React's onFocus
            // bubbles, so the button below reaches this without repeating the handler.
            onFocus={() => onHoverStation?.(i)}
            onBlur={() => onHoverStation?.(null)}
            // The toggle is caught here rather than on the button because the button is the
            // bottom-most child: the ships, the rings, the core and the label are all painted over
            // it, and a click landing on the core hex would otherwise fall through to nothing.
            // Everything inside a node bubbles to here, the button's own Enter and Space included.
            onClick={() => onToggleStation?.(i)}
          >
            {/* The hit area, and the only child of a node that is a shape rather than a picture.
                A node draws itself as a ring around a point, so its real ink is a thin outline with
                a hole in it and a label sitting well below — aiming at any of that is a game. This
                covers the whole neighbourhood, ships to count, so pointing anywhere near a station
                counts as pointing at it.

                It is also the button, for the same reason: the node itself is a 0×0 anchor with
                everything painted around it, and a control with no box is one a pointer, a screen
                reader's hit test and a focus ring all have to guess at. */}
            <button
              type="button"
              aria-pressed={on}
              aria-label={`${st.label}, ${tiers.length} ticket${tiers.length === 1 ? '' : 's'}`}
              style={{
                position: 'absolute', left: -50, top: -(size + 30),
                width: 100, height: size * 2 + 66, borderRadius: 8,
                cursor: 'pointer',
              }}
            />
            {/* A line from the agent working here. Below the ships and the core so it is painted
                behind them: it is the station lighting up from underneath, not a wash over it. */}
            <NodeFlash pulse={pulses?.get(i)} size={size} />
            {/* An arrival, announced twice over: a shockwave opening past the ships parked around
                the node, and one fast turn of a second ring outside the slow one the node always
                wears. Both are mounted by the burst rather than toggled on a permanent element,
                which is what restarts them — a station can take two deliveries in a row, and an
                animation already running would swallow the second.

                Below the ships and the core in source order so it is painted behind them: the
                node's own furniture is what the wave is leaving from, and it should not be
                washed over by it. */}
            {landing && (
              <>
                <span
                  key={`shock${live}`}
                  className="node-shock"
                  style={{
                    position: 'absolute', left: -size, top: -size, width: size * 2, height: size * 2,
                    borderRadius: '50%', border: `1.5px solid ${c}`, pointerEvents: 'none',
                  }}
                />
                <span
                  key={`whip${live}`}
                  className="node-whip"
                  style={{
                    position: 'absolute', left: -(size + 5), top: -(size + 5),
                    width: (size + 5) * 2, height: (size + 5) * 2,
                    borderRadius: '50%', border: '1.5px solid transparent', borderTopColor: c,
                    filter: `drop-shadow(0 0 6px ${c})`, pointerEvents: 'none',
                  }}
                />
              </>
            )}
            {/* Ships at anchor: one arrowhead per ticket, in that ticket's own tier colour.
                Not at the two ends. A ship says "this one is under way, and here is how far it has
                got", which is worth saying at every station a ticket passes through and nothing at
                all at the ones it sits at either side of the journey. They are also the two that
                fill without limit, so they are where the row is guaranteed to end up an unreadable
                thicket - and the count under the node already answers it better. */}
            {!terminus && (
            <div style={{ position: 'absolute', left: -46, top: -size - 24, width: 92, display: 'flex', justifyContent: 'center', gap: 4 }}>
              {/* The glow goes on a wrapper, not on the arrowhead. An arrowhead is a clip-path, and
                  clip-path discards anything painted past the shape — but a filter on the parent is
                  applied to the child's already-clipped output, so drop-shadow traces the silhouette
                  instead of haloing a box around it. Each ship keeps its own tier colour, which a
                  single shared halo could not do for a station holding a mix. */}
              {/* Drift is the pace of the work, not decoration on it. A ship an agent is holding
                  rides the swell at the old rate and burns at full brightness; one nobody has slows
                  to a quarter of it and dims — still moving, because the ticket is still alive and a
                  frozen ship would read as a dead one, but visibly becalmed.

                  Amplitude and colour are left alone, so the dimming is only ever "less lit", never
                  a different tier or a different motion: a station of idle ships still reads as the
                  same fleet, turned down. The glow shrinks with the brightness rather than just
                  fading with it, because a wide halo at half opacity still pulls the eye across the
                  arc — which is exactly the claim an idle ship should not be making. */}
              {tiers.map((t, j) => {
                const held = claimed[j];
                return (
                <span
                  key={j}
                  style={{
                    filter: `drop-shadow(0 0 ${held ? 4 : 2}px ${TIER[t].c})`,
                    opacity: held ? 1 : 0.45,
                    animation: `shipdrift ${((2.4 + j * 0.4) * (held ? 1 : 4)).toFixed(1)}s ease-in-out infinite`,
                    // An agent picking the ticket up is a thing to notice, not to catch by chance:
                    // the ship comes up to full over half a second instead of snapping on.
                    transition: 'opacity .5s ease, filter .5s ease',
                  }}
                >
                  <span style={{ display: 'block', width: SHIP_PX, height: SHIP_PX, ...SHIP_GLYPH, background: TIER[t].c }} />
                </span>
                );
              })}
            </div>
            )}
            {/* Held. A hovered node and a filtered one are both at full brightness — brightness is
                the answer to "what am I pointing at", and it has to keep saying that — so the
                filter states itself in a shape instead: a second ring, complete and still, outside
                the spinning one. Present at rest too, scaled down to nothing, so it grows out of
                the node on the click rather than blinking into existence beside it. */}
            <span
              style={{
                position: 'absolute', left: -(size + 7), top: -(size + 7),
                width: (size + 7) * 2, height: (size + 7) * 2,
                borderRadius: '50%', border: `1px solid ${c}`,
                opacity: on ? 0.9 : 0,
                transform: `scale(${on ? 1 : 0.72})`,
                boxShadow: on ? `0 0 12px -3px ${c}, inset 0 0 12px -6px ${c}` : undefined,
                transition: 'opacity .22s ease, transform .24s cubic-bezier(.2,.9,.3,1.4)',
                pointerEvents: 'none',
              }}
            />
            <div
              style={{
                position: 'absolute', left: -size, top: -size, width: size * 2, height: size * 2,
                borderRadius: '50%', border: `1px solid ${fade(c, 23)}`, borderTopColor: c, borderRightColor: 'transparent',
                animation: `ringspin ${8 + i}s linear infinite`,
                opacity: tiers.length ? 'calc(.5 + var(--nv,0) * .5)' : .28,
              }}
            />
            {/* The core's glow is a halo behind it rather than a shadow on it: the core is cut to
                a hex, and clip-path clips everything an element paints, so light thrown past the
                shape's own edge is composited and then discarded. The halo is unclipped, so it
                can actually spill — and it grows with --nv the way the core does. */}
            <span
              style={{
                position: 'absolute', left: -size * 1.6, top: -size * 1.6, width: size * 3.2, height: size * 3.2,
                background: `radial-gradient(circle,${c}${tiers.length ? '66' : '2a'},transparent 66%)`,
                opacity: 'calc(.55 + var(--nv,0) * .45)',
                transform: 'scale(calc(1 + var(--nv,0) * .35))',
                transition: 'transform .22s cubic-bezier(.2,.8,.3,1),opacity .22s ease',
                pointerEvents: 'none',
              }}
            />
            <div
              style={{
                position: 'absolute', left: -size / 2.4, top: -size / 2.4, width: size / 1.2, height: size / 1.2,
                ...HEX_GLYPH,
                background: tiers.length ? `linear-gradient(160deg,${c},${fade(c, 33)})` : 'rgb(var(--w-line-rgb) / .2)',
                transform: 'scale(calc(1 + var(--nv,0) * .2))',
                transition: 'transform .22s cubic-bezier(.2,.8,.3,1)',
              }}
            />
            <div
              style={{
                position: 'absolute', left: -50, top: size + 7, width: 100, textAlign: 'center',
                font: `700 9px ${DISPLAY}`, letterSpacing: track(.24), textTransform: CAPS,
                color: 'rgb(var(--w-t200-rgb) / calc(.5 + var(--nv,0) * .5))',
                textShadow: `0 0 calc(var(--nv,0) * 12px) ${c}`,
                transition: 'color .2s ease,text-shadow .2s ease',
              }}
            >
              {st.short}
            </div>
            {tiers.length > 0 && (
              <div
                style={{
                  position: 'absolute', left: -50, top: size + 20, width: 100, textAlign: 'center',
                  font: `700 10.5px ${MONO}`, color: c,
                }}
              >
                {String(tiers.length).padStart(2, '0')}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
