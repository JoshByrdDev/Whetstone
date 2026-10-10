import type { CSSProperties, ReactNode } from 'react';
import type { Phase, PhaseKind, Track } from '../types/tickets';
import { fade, TIER, DIAMOND } from './orbit';

// ---- Journey stations: the stops every ticket travels left→right ----
// Needs*/-ing phase pairs share a station so the track reads as one trip.
// Shared by the board (the sector arc and the per-row tracks) and the ticket drawer.

/** Which track a station belongs to. */
export type StationTrack = 'both' | 'delivery' | 'review';

/**
 * Where a station sits on the board's column grid.
 *
 * A number is a column of its own. A PAIR is a stop with NO column: it is drawn at the MIDPOINT
 * between those two columns.
 *
 * That is what lets the review track run a line the full width of the board without taking a pixel
 * of width from the delivery stations and without ever standing on top of one. Two lanes through
 * the same corridor with their stops interleaved, the way a transit map does it — and it is also
 * what makes the two tracks tell themselves apart in a list, since a row now draws only its own
 * track's stops and a review ticket is five diamonds where a delivery ticket is nine.
 */
export type StationAt = number | readonly [number, number];

export const STATIONS: {
  label: string; short: string; cls: string; track: StationTrack; at: StationAt;
}[] = [
  { label: 'Backlog', short: 'BKLG', cls: 'text-gray-500', track: 'both', at: 0 },
  { label: 'Spec', short: 'SPEC', cls: 'text-sky-400', track: 'delivery', at: 1 },
  { label: '✋ Approve', short: 'APPR', cls: 'text-violet-400', track: 'delivery', at: 2 },
  { label: 'Implement', short: 'IMPL', cls: 'text-amber-500', track: 'delivery', at: 3 },
  { label: '✋ Check', short: 'CHCK', cls: 'text-violet-400', track: 'delivery', at: 4 },
  { label: 'QA', short: 'QA', cls: 'text-amber-500', track: 'delivery', at: 5 },
  { label: 'Refine', short: 'RFNE', cls: 'text-amber-500', track: 'delivery', at: 6 },
  // The review pair, spread wide rather than bunched at the end: the queue sits under the
  // Implement/Check gap and the pass under the Refine/Review one, so the lower lane crosses most of
  // the board instead of being a stub in its last quarter. Order in this array is still the order a
  // ticket meets them, which is what STATION_OF below indexes into; `at` is only where they DRAW.
  { label: 'Peer queue', short: 'PEER', cls: 'text-sky-400', track: 'review', at: [3, 4] },
  // PRVW rather than RVWG, and the label is "Peer review" rather than "Reviewing" so the short is
  // still the label with its vowels squeezed out, the way every other one here is. RVWG sat beside
  // RVEW and was read as a typo of it - same three consonants, differing only in the order of the
  // last two. The leading P separates them, and ties this stop to the PEER it follows.
  { label: 'Peer review', short: 'PRVW', cls: 'text-amber-500', track: 'review', at: [6, 7] },
  { label: '✋ Review', short: 'RVEW', cls: 'text-violet-400', track: 'both', at: 7 },
  { label: 'Done', short: 'DONE', cls: 'text-emerald-400', track: 'both', at: 8 },
];

/**
 * One flat map covering both tracks — a dividend of splitting the review phases off rather than
 * reusing the QA pair. Had they shared QaTesting, this entry would have had to be 5 or 8 depending
 * on the ticket, and every reader of it would need the track to ask.
 */
export const STATION_OF: Record<Phase, number> = {
  Backlog: 0,
  NeedsSpeculation: 1,
  Speculating: 1,
  NeedsApproval: 2,
  NeedsImplementation: 3,
  Implementing: 3,
  NeedsImplementationReview: 4,
  NeedsQa: 5,
  QaTesting: 5,
  NeedsRefinement: 6,
  Refining: 6,
  NeedsPeerReview: 7,
  PeerReviewing: 8,
  NeedsReview: 9,
  Done: 10,
  Blocked: 0, // real position comes from phaseBeforeBlock
};

/** How many columns the grid lays out: one per station that owns one. */
export const COLUMNS = STATIONS.filter(s => typeof s.at === 'number').length;

/**
 * A station's position in COLUMN UNITS — 3.5 for one sitting between columns 3 and 4.
 *
 * The single number every part of the board measures a station by: the arc interpolates its node's
 * x from it, and a row decides which of its segments are behind the ticket by comparing against it.
 * Halves are why those comparisons are on positions rather than on station indices.
 */
export function stationX(i: number): number {
  const at = STATIONS[i].at;
  return typeof at === 'number' ? at : (at[0] + at[1]) / 2;
}

/** Station indices a ticket on this track can actually stand on. */
export function stationsOnTrack(track: Track): number[] {
  const want: StationTrack = track === 'Review' ? 'review' : 'delivery';
  return STATIONS.map((s, i) => (s.track === 'both' || s.track === want ? i : -1)).filter(i => i >= 0);
}

/**
 * The stations a board running these tracks draws — the union, so "all projects" with one review
 * project among them still shows the review lane. An empty list is read as delivery-only: a board
 * with no lane at all is never the answer anyone wanted.
 *
 * Only the ARC consults this. The column grid is the same nine either way, because the review
 * stops borrow their positions from the delivery columns rather than claiming any of their own —
 * so switching a track off changes what is drawn, never how wide anything is.
 */
export function stationsForTracks(tracks: readonly Track[]): ReadonlySet<number> {
  const shown = new Set<number>();
  for (const t of tracks.length ? tracks : (['Delivery'] as Track[]))
    for (const i of stationsOnTrack(t)) shown.add(i);
  return shown;
}

/**
 * Shared column template for every track row: name | one column per station that owns one |
 * status/actions. The count is derived from STATIONS so adding a station can never silently desync
 * the grid from the cells it lays out (that drift pushes the last station into the status column).
 *
 * The sector arc at the top of the board lays its nodes out on this same template, so a ticket's
 * chevron sits exactly under the station it is travelling to, at any board width.
 */
export const GRID: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: `300px repeat(${COLUMNS}, minmax(58px, 1fr)) 150px`,
  alignItems: 'center',
};

/**
 * Grid column (1-based) a station is DRAWN IN — column 1 is the ticket-name cell. A station with no
 * column of its own is drawn inside the one to its left, half a column further in.
 */
export const stationColumn = (i: number) => {
  const at = STATIONS[i].at;
  return (typeof at === 'number' ? at : at[0]) + 2;
};

/** True when the station has no column of its own and is drawn at that column's midpoint. */
export const stationIsMid = (i: number) => typeof STATIONS[i].at !== 'number';

/**
 * Width of the slot every chevron is centred in. Each station cell lays out as [dot][segment], so
 * the dot sits at the START of its column, not centred in it — anything aiming at the dot (the
 * agent's beam, the arc node above it) has to centre on this width at the column's left edge.
 */
export const ACTIVE_DOT_PX = 13;

export function stationOf(t: { phase: Phase; phaseBeforeBlock?: Phase | null }): number {
  if (t.phase === 'Blocked') return t.phaseBeforeBlock ? STATION_OF[t.phaseBeforeBlock] : 0;
  return STATION_OF[t.phase];
}

export type TrackState = 'active' | 'queued' | 'backlog' | 'gate' | 'blocked' | 'done';

export function trackState(phase: Phase, kind: PhaseKind | undefined): TrackState {
  if (phase === 'Blocked') return 'blocked';
  if (phase === 'Done') return 'done';
  switch (kind) {
    case 'active': return 'active';
    case 'gate': return 'gate';
    case 'backlog': return 'backlog';
    default: return 'queued';
  }
}

/** The track states map one-for-one onto tiers, so a chevron is always its row's colour. */
const TRACK_TIER: Record<TrackState, keyof typeof TIER> = {
  active: 'active',
  gate: 'gate',
  blocked: 'blocked',
  done: 'done',
  backlog: 'backlog',
  queued: 'queue',
};

export function ago(iso: string): string {
  const m = Math.floor((Date.now() - new Date(iso).getTime()) / 60000);
  if (m < 1) return 'just now';
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h`;
  return `${Math.floor(h / 24)}d`;
}

const CLEARED = 'rgb(var(--w-done-rgb) / .85)';
const AHEAD = 'rgb(var(--w-line-rgb) / .22)';

// The rail itself, dimmer than the chevrons standing on it. Named because the empty slots on a
// review row are drawn from these too, and a slot that used a different dim would read as a seam.
const RAIL_CLEARED = 'rgb(var(--w-done-rgb) / .5)';
const RAIL_AHEAD = 'rgb(var(--w-line-rgb) / .11)';

/**
 * One cell per COLUMN, placed straight into the parent's grid.
 *
 * A row draws only the stops its own track visits — nine diamonds for a delivery ticket, five for a
 * review one, and the review ones falling between the delivery columns rather than on them. That
 * shape difference is the point: two tracks in one list are told apart by the silhouette of the row
 * rather than by reading a chip on it.
 *
 * Every chevron — passed, current or still ahead — is centred on the same {@link ACTIVE_DOT_PX}
 * slot, which is what lets the arc's station nodes line up with the rows underneath them.
 */
export function TrackCells({ station, current, delay, track = 'Delivery' }: {
  station: number;
  current: TrackState;
  /** Retained for callers that distinguish card rows from list rows; ORBIT draws both alike. */
  size?: 'row' | 'card';
  delay?: string;
  /**
   * Which track's stations this row visits. Everything else is bare line — without this a review
   * ticket would show Spec, Approve, Implement and QA behind it as CLEARED, claiming it passed
   * through four phases it can never enter.
   */
  track?: Track;
}) {
  const tier = TIER[TRACK_TIER[current]].c;
  const here = stationX(station);

  // This ticket's own stops, indexed by the column they are drawn in: `head` on that column's own
  // dot, `mid` half a column further in. Everything not in one of these two is bare line.
  const head: (number | null)[] = Array(COLUMNS).fill(null);
  const mid: (number | null)[] = Array(COLUMNS).fill(null);
  for (const s of stationsOnTrack(track)) {
    const at = STATIONS[s].at;
    if (typeof at === 'number') head[at] = s;
    else mid[at[0]] = s;
  }

  // Centres a chevron smaller than the slot, so its middle lands on the column's chevron axis.
  const centre = (w: number) => ({ marginLeft: (ACTIVE_DOT_PX - w) / 2, marginRight: (ACTIVE_DOT_PX - w) / 2 });

  // One slot, always ACTIVE_DOT_PX wide whatever is in it — empty included. Keeping the empty ones
  // at full width is what holds every row's segments on the same axis as the arc's nodes above them.
  //
  // Empty is drawn as RAIL, not as blank: a review row skips four delivery columns, and leaving
  // those slots hollow chopped its line into five pieces with a hole where each diamond it does not
  // have would have stood. The stops still differ between the tracks — the line no longer does.
  // Position `at` is the column itself, so it clears exactly when the segment arriving at it does.
  const slot = (s: number | null, key: string, at: number): ReactNode => {
    if (s === null) {
      return (
        <span
          key={key}
          style={{
            width: ACTIVE_DOT_PX, height: 2, flex: 'none',
            background: at <= here ? RAIL_CLEARED : RAIL_AHEAD,
          }}
        />
      );
    }
    const x = stationX(s);

    if (x === here) {
      // The chevron a ticket is sitting on is the one thing on the track that glows, and a diamond
      // is a clip-path — which clips away any light it tries to throw past its own edge. The halo
      // behind it is what actually spills; the diamond just sits in it.
      return (
        <span key={key} style={{ position: 'relative', width: ACTIVE_DOT_PX, height: ACTIVE_DOT_PX, flex: 'none' }}>
          <span
            style={{
              position: 'absolute', left: -13, top: -13, width: 39, height: 39,
              background: `radial-gradient(circle,${fade(tier, 44)},transparent 64%)`,
              pointerEvents: 'none',
            }}
          />
          <span
            style={{
              position: 'absolute', inset: 0, background: tier, clipPath: DIAMOND,
              animation: current === 'active' || current === 'blocked'
                ? 'tierpulse 1.3s ease-in-out infinite' : 'none',
              animationDelay: delay,
            }}
          />
        </span>
      );
    }

    const cleared = x < here;
    const w = cleared ? 8 : 6;
    return (
      <span
        key={key}
        style={{
          width: w, height: w, flex: 'none',
          background: cleared ? CLEARED : AHEAD, clipPath: DIAMOND, ...centre(w),
        }}
      />
    );
  };

  // A segment is behind the ticket when its far end is, and shimmers when the ticket is standing on
  // its near end with an agent working. Stated in positions rather than station indices so a
  // half-column segment beside a midpoint stop answers exactly the way a full one does.
  const seg = (from: number, to: number, key: string): ReactNode => {
    let style: CSSProperties;
    if (from === here && current === 'active') {
      // Light sweeps along the segment Claude is currently pushing forward.
      style = {
        flex: 1, height: 2,
        backgroundImage: `linear-gradient(90deg,${tier},rgb(var(--w-accent-rgb) / .05))`,
        backgroundSize: '120px 100%',
        animation: 'track-shimmer 1.4s linear infinite',
        animationDelay: delay,
      };
    } else {
      style = { flex: 1, height: 2, background: to <= here ? RAIL_CLEARED : RAIL_AHEAD };
    }
    return <span key={key} style={style} />;
  };

  const cells: ReactNode[] = [];
  for (let c = 0; c < COLUMNS; c++) {
    const parts: ReactNode[] = [slot(head[c], 'h', c)];
    const last = c === COLUMNS - 1;

    if (mid[c] !== null) {
      // Two equally flexed halves either side of an equally sized slot put the midpoint stop's left
      // edge on exactly 50% of the column — the same x the arc gets by averaging its two measured
      // neighbours. Done with flex rather than a percentage so it holds at any board width without
      // either half of the board having to know the other's arithmetic.
      parts.push(seg(c, c + 0.5, 'a'), slot(mid[c], 'm', c + 0.5));
      if (!last) parts.push(seg(c + 0.5, c + 1, 'b'));
    } else if (!last) {
      parts.push(seg(c, c + 1, 's'));
    }

    cells.push(<div key={c} className="flex items-center min-w-0">{parts}</div>);
  }

  return <>{cells}</>;
}
