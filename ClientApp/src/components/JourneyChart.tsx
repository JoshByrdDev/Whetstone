import type { JourneyLeg, Phase, Track } from '../types/tickets';
import { PHASE_LABEL } from '../types/tickets';
import { STATION_OF, stationsOnTrack } from './JourneyTrack';

/**
 * The ticket's trip as a line: phases up the side, moves left to right.
 *
 * The shape IS the finding. A ticket that went straight through draws one clean diagonal up to the
 * right; a ticket QA kept sending back draws a saw-tooth, and the number of teeth is the number of
 * times the pipeline had to redo its own work. That is legible at a glance in a way a list of
 * timestamps never is, and it is the question someone opening a ticket in the Classroom actually
 * has: was this straightforward, or did it fight?
 *
 * Nothing here is model-written. Every point comes from a row in PhaseTransitions, so the chart
 * cannot be wrong about a trip the pipeline already recorded.
 */

// Bottom to top, both tracks in one list — the axis a chart draws is this filtered to one of them.
// Blocked sits BELOW Backlog rather than at its enum position (last), because a blocked ticket has
// fallen off the road — drawing it above Done would read as progress.
//
// The peer pair sits above Refining and below Review, which is where the review track meets them
// (see STATION_OF). Leaving them out entirely was worse than an ordering question: `rank` floors an
// unknown phase at 0, so every stop of a review ticket's trip was plotted on the Blocked row.
const ORDER: Phase[] = [
  'Blocked',
  'Backlog',
  'NeedsSpeculation',
  'Speculating',
  'NeedsApproval',
  'NeedsImplementation',
  'Implementing',
  'NeedsImplementationReview',
  'NeedsQa',
  'QaTesting',
  'NeedsRefinement',
  'Refining',
  'NeedsPeerReview',
  'PeerReviewing',
  'NeedsReview',
  'Done',
];

/**
 * The rows this ticket's chart has: the phases its track can reach, plus Blocked, which any ticket
 * can fall into.
 *
 * Derived from the board's own station map rather than restated here, so a phase can never belong
 * to a track on the board and not in the chart. Nine delivery rows of untouchable grey above a
 * review ticket's four-stop trip was the whole complaint — an axis mostly made of phases the ticket
 * is not eligible for reads as work skipped rather than work never asked for.
 */
function rowsFor(track: Track): Phase[] {
  const stops = new Set(stationsOnTrack(track));
  return ORDER.filter(p => p === 'Blocked' || stops.has(STATION_OF[p]));
}

/** The three phases where a person, not the pipeline, is the one holding the ticket. */
const GATES = new Set<string>(['NeedsApproval', 'NeedsImplementationReview', 'NeedsReview']);

const GUTTER = 152;   // label column
const ROW_H = 20;
const COL_W = 30;
const PAD_T = 14;
const PAD_B = 16;
const PAD_R = 26;

export function JourneyChart({ legs, track = 'Delivery' }: { legs: JourneyLeg[]; track?: Track }) {
  if (legs.length === 0) return null;

  // Points are phases the ticket OCCUPIED: where it started, then the destination of every move.
  const points = [legs[0].from, ...legs.map(l => l.phase)];
  const visited = new Set(points);

  // This track's rows, plus any phase the trip actually stood on. The second half is not paranoia:
  // a ticket whose track was switched under it really did make those stops, and without a row of
  // its own `rank` floors each one onto Blocked — the chart would then report a clean trip as a
  // ticket that fell over. History gets a row; the empty phases of the other track do not.
  const onTrack = new Set(rowsFor(track));
  const rows = ORDER.filter(p => onTrack.has(p) || visited.has(p));
  const rank = (p: string) => Math.max(rows.indexOf(p as Phase), 0);
  const top = rows.length - 1;

  const width = GUTTER + Math.max(points.length - 1, 1) * COL_W + PAD_R;
  const height = PAD_T + top * ROW_H + PAD_B;

  const x = (i: number) => GUTTER + i * COL_W;
  const y = (phase: string) => PAD_T + (top - rank(phase)) * ROW_H;

  // Time spent shows up as dot size, so replacing the old bar chart loses nothing. Square-rooted so
  // a 3-hour queue reads as bigger than a 3-minute one without swallowing the line.
  const longest = Math.max(...legs.map(l => l.minutes ?? 0), 1);
  const radius = (m: number | null) => (m == null ? 4 : 2.4 + 3.6 * Math.sqrt(m / longest));

  // Rendered at its natural size and left to scroll inside the container when that does not fit,
  // rather than scaled down to it. Scaling would take the 10px phase labels with it, and a trip
  // long enough to need shrinking is exactly the trip whose labels you most want to read.
  return (
    <div className="overflow-x-auto">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        width={width}
        height={height}
        role="img"
        aria-label={`Phase trip: ${points.length - 1} moves, ${legs.filter(l => l.backward).length} backward`}
      >
        {/* Phase rows. Unvisited ones stay faint so the static axis gives the shape somewhere to sit
            without competing with it. */}
        {rows.map(p => {
          const yy = y(p);
          const seen = visited.has(p);
          return (
            <g key={p}>
              <line
                x1={GUTTER - 6} y1={yy} x2={width - PAD_R + 6} y2={yy}
                stroke="currentColor"
                className={seen ? 'text-gray-800' : 'text-gray-900'}
                strokeWidth={1}
              />
              <text
                x={GUTTER - 12} y={yy + 3.5} textAnchor="end"
                className={`text-[10px] ${
                  !seen ? 'fill-gray-700'
                    : p === 'Blocked' ? 'fill-rose-400/90'
                    : GATES.has(p) ? 'fill-violet-300/90'
                    : 'fill-gray-400'
                }`}
              >
                {GATES.has(p) ? `✋ ${PHASE_LABEL[p]}` : PHASE_LABEL[p]}
              </text>
            </g>
          );
        })}

        {/* One segment per move. Backward moves are drawn in amber — the whole point of the chart is
            that you can count them without reading anything. */}
        {legs.map((l, i) => (
          <line
            key={`s${i}`}
            x1={x(i)} y1={y(points[i])}
            x2={x(i + 1)} y2={y(points[i + 1])}
            stroke="currentColor"
            className={l.backward ? 'text-amber-500' : 'text-indigo-400'}
            strokeWidth={l.backward ? 2 : 1.5}
            strokeLinecap="round"
            opacity={l.backward ? 0.95 : 0.75}
          />
        ))}

        {points.map((p, i) => {
          const leg = i === 0 ? null : legs[i - 1];
          const held = leg?.minutes ?? null;
          const last = i === points.length - 1;
          return (
            <circle
              key={`p${i}`}
              cx={x(i)} cy={y(p)} r={radius(held)}
              className={
                p === 'Blocked' ? 'fill-rose-500'
                  : last ? 'fill-sky-400'
                  : leg?.backward ? 'fill-amber-500'
                  : 'fill-indigo-400'
              }
              stroke="currentColor"
              strokeWidth={last ? 2 : 0}
              opacity={0.95}
            >
              <title>
                {PHASE_LABEL[p as Phase] ?? p}
                {leg ? ` — entered ${new Date(leg.enteredAt).toLocaleString()} (${leg.actor})` : ' — start'}
                {held == null ? (last ? ' · still here' : '') : ` · stayed ${duration(held)}`}
              </title>
            </circle>
          );
        })}
      </svg>
    </div>
  );
}

function duration(m: number): string {
  if (m < 1) return '<1m';
  if (m < 60) return `${Math.round(m)}m`;
  if (m < 1440) return `${(m / 60).toFixed(1)}h`;
  return `${(m / 1440).toFixed(1)}d`;
}
