import { Fragment, useMemo, useState, useRef, useEffect, useLayoutEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { usePop } from '../hooks/usePop';
import type { CSSProperties, ReactNode } from 'react';
import { useTickets } from '../hooks/useTickets';
import { useNarrow } from '../hooks/useNarrow';
import { PHASE_LABEL, DEFAULT_PRIORITY, PRIORITY_COLOR, PRIORITY_HINT, PRIORITY_LEVEL, PRIORITY_MARK } from '../types/tickets';
import type { LabelRef, Phase, PhaseInfo, PhaseKind, TicketSummary, Track } from '../types/tickets';
import type { DashboardState, HeartbeatEvent } from '../types/events';
import { transitionTicket } from '../api/tickets';
import { TicketDetail } from './TicketDetail';
import { CreateTicketModal } from './CreateTicketModal';
import { ProjectGitBar } from './ProjectGitBar';
import { HeartbeatProjectStrip } from './HeartbeatProjectFilter';
import { HeartbeatPriorityStrip } from './HeartbeatPriorityFilter';
import { STATIONS, STATION_OF, stationOf, trackState, TrackCells, ago, GRID, stationsForTracks } from './JourneyTrack';
import { AgentDock, AgentHover, AgentMascot, agentAccent } from './AgentMascot';
import { TicketGitActions } from './TicketGitActions';
import { LabelChips } from './LabelEditor';
import { SectorArc } from './SectorArc';
import type { StationLoad, StationPulse } from './SectorArc';
import { FEED_HIDDEN } from './eventText';
import { fade, CAPS, TIER, tierOf, tierToken, chamfer, chamferRow, HEX_GLYPH, DISPLAY, BODY, MONO } from './orbit';
import { PageHead, Chip, PrimaryButton } from './ui';
import type { AgentState } from '../types/events';
import { track } from '../theme';

// The gate cards re-present existing transitions with decision-flavored labels.
// Filtered against the phase's real nextPhases before rendering, so they can
// never offer a move the phase machine wouldn't allow.
const GATE_ACTIONS: Partial<Record<Phase, { to: Phase; label: string }[]>> = {
  NeedsApproval: [
    { to: 'NeedsImplementation', label: 'Approve →' },
    { to: 'NeedsSpeculation', label: 'Send back' },
  ],
  NeedsImplementationReview: [
    { to: 'NeedsQa', label: 'To QA →' },
    { to: 'NeedsImplementation', label: 'Changes' },
  ],
  NeedsReview: [
    { to: 'Done', label: 'Done →' },
  ],
};

/** Shared empty list for the rows wearing no labels — the common case, and not worth an allocation. */
const NO_LABELS: LabelRef[] = [];

/**
 * Board gutter. The arc, the section rules and every row share it, so the sky shows through evenly.
 * Padding reads it as `--board-gutter`, which the board sets from this at a desk and from
 * {@link GUTTER_NARROW} on a phone, where 26px either side is a seventh of the screen.
 */
const GUTTER = 26;
const GUTTER_NARROW = 4;
/** Row shell padding. The arc's probe grid sits inside the same inset, which is what aligns them. */
const ROW_PAD_L = 20;
const ROW_PAD_R = 16;

/**
 * How far back Completed reaches.
 *
 * Finished work is reference, not workload, and it only ever accumulates — left unbounded, a board
 * a few months old opens with every ticket it has ever closed folded behind a count nobody reads.
 * Recent by default, with the whole pile a click away.
 */
type DoneWindow = '30d' | 'all';
const DONE_WINDOWS: { key: DoneWindow; label: string }[] = [
  { key: '30d', label: 'Last 30 days' },
  { key: 'all', label: 'All time' },
];
const DONE_WINDOW_MS = 30 * 24 * 60 * 60 * 1000;

/**
 * The four zones the board sorts every ticket into, top to bottom. Named here rather than decided
 * inline because two things now ask the question — the split that builds the lists, and the watch
 * that notices a ticket crossing into 'done' — and they have to agree on the answer.
 */
type Zone = 'waiting' | 'flight' | 'backlog' | 'done';

/**
 * Where a ticket stood the last time the board looked at it.
 *
 * Held for the length of a landing, and it has to carry the phase as well as the zone. Keeping a
 * finished row in the right list was not enough on its own: every order on this board is derived
 * from the phase, and a ticket that has reached Done sorts past every gate there is — so the row
 * stayed in Awaiting your call and still jumped to the bottom of it, then played its exit from a
 * place it had never actually been.
 */
interface Standing {
  zone: Zone;
  phase: Phase;
  /** stationOf needs this too: a blocked ticket's place comes from what it was blocked out of. */
  phaseBeforeBlock?: Phase | null;
}

function zoneOf(t: TicketSummary, kindOf: ReadonlyMap<Phase, PhaseKind>): Zone {
  const kind = kindOf.get(t.phase);
  if (kind === 'gate' || t.phase === 'Blocked') return 'waiting';
  if (kind === 'terminal') return 'done';
  if (kind === 'backlog') return 'backlog';
  return 'flight';
}

export function Board({ state, wakeAt, events }: { state: DashboardState; wakeAt: Date | null; events: HeartbeatEvent[] }) {
  const [projectFilter, setProjectFilter] = useState<string | null>(null);
  const narrow = useNarrow();
  const { tickets, phases, projects, labels, loading, error, refreshTickets } = useTickets(projectFilter);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [creating, setCreating] = useState(false);
  const [menuFor, setMenuFor] = useState<number | null>(null);
  // Collapsible sections, keyed by title. Absent = collapsed, so Backlog and Done start folded.
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  // How far back Completed reaches. The choice is offered inside the strip once it is open — shut,
  // the strip is a count, and a count that can silently mean two different things needs the window
  // said next to it, which is what the note on the header is.
  const [doneWindow, setDoneWindow] = useState<DoneWindow>('30d');
  const [actionError, setActionError] = useState<string | null>(null);
  // Station under the pointer on the arc, or null. Held in state rather than done in CSS because
  // the answer depends on comparing two numbers — a row's station against the hovered one — and a
  // stylesheet cannot compare. Nine nodes, so a hover here is nowhere near as hot as a hover on a
  // row, which is still pure CSS.
  const [hoverStation, setHoverStation] = useState<number | null>(null);
  // Stations kept by clicking the arc. Hovering a node asks which tickets are at that stage;
  // clicking keeps the answer and throws the rest of the board away. A set rather than a single
  // station because the questions people actually ask of this board span stages — "everything
  // waiting on me", "the whole implement/QA stretch" — and one node at a time cannot say that.
  const [stationFilter, setStationFilter] = useState<ReadonlySet<number>>(() => new Set());
  const filtering = stationFilter.size > 0;

  const toggleStation = useCallback((s: number) => {
    setStationFilter(prev => {
      const next = new Set(prev);
      if (!next.delete(s)) next.add(s);
      return next;
    });
  }, []);

  const showProjectBadge = projects.length > 1 && !projectFilter;

  // Which tracks this board draws, from the project SETTING rather than from what happens to be on
  // the board. Filtered to one project it is that project's answer; across all of them it is the
  // union, so one review project among five still shows the review lane. Deliberately not "does
  // anything sit there right now": the arc is the board's legend, and a legend that only lists the
  // stages currently occupied cannot tell you the empty ones exist.
  const shownTracks = useMemo<Track[]>(() => {
    const from = projectFilter ? projects.filter(p => p.key === projectFilter) : projects;
    const all = new Set<Track>();
    for (const p of from) for (const t of p.tracks ?? []) all.add(t);
    // Nothing loaded yet, or a filter naming a project that has gone: draw delivery, which every
    // project has and which is the board this was before there was a second track.
    return all.size ? [...all] : ['Delivery'];
  }, [projects, projectFilter]);
  const shownStations = useMemo(() => stationsForTracks(shownTracks), [shownTracks]);

  // A stage filter survives a project switch, and it can name a station the new board does not
  // draw — which would hold the list down to nothing behind a node nobody can see to click off.
  useEffect(() => {
    setStationFilter(prev => {
      const next = new Set([...prev].filter(s => shownStations.has(s)));
      return next.size === prev.size ? prev : next;
    });
  }, [shownStations]);

  // The heartbeat moves tickets out from under us - agents claim them, walk them through phases and
  // tick off work items - but ticket data is only fetched on mount. Without this the board keeps
  // rendering the phase a ticket had when the page loaded, so starting the heartbeat from the app
  // bar left the track (and its in-progress glow) stale until you navigated away and back.
  // Re-pull whenever the fleet reports a change, and poll while it is actually running to
  // catch the mutations its status alone doesn't announce. Keyed on every agent, not just the
  // first: with three of them working, only one changing is the normal case.
  const beat = `${state.status}|${state.agents.map(a => `${a.slot}:${a.ticketId ?? ''}:${a.phase ?? ''}`).join(',')}`;
  useEffect(() => { refreshTickets(); }, [beat, refreshTickets]);
  useEffect(() => {
    if (state.status !== 'running') return;
    const id = setInterval(refreshTickets, 8000);
    return () => clearInterval(id);
  }, [state.status, refreshTickets]);

  const kindOf = useMemo(() => new Map(phases.map(p => [p.name, p.kind])), [phases]);
  const nextOf = useMemo(() => new Map(phases.map(p => [p.name, p.nextPhases])), [phases]);

  // Tickets that have just reached Done, each still held in the zone it finished in. Everything
  // the completion animates hangs off this: the row files itself away from where it was, the arc
  // bursts at the station it arrived at, and Completed only claims it once it has gone.
  const { landing, touchdown, filed } = useLanding(tickets, kindOf, phases.length > 0);

  // Four zones, top to bottom: tickets waiting on a human (gates + blocked, ordered by how
  // far along the pipeline they are, blocked last); what Claude is working or has queued,
  // actives first so the live rows sit at the top; the untouched backlog; and finished work,
  // most recently finished first. Finished and not-yet-started tickets get their own sections
  // so they stop diluting the "what's moving" zone.
  const { waiting, flight, backlog, done, doneTotal } = useMemo(() => {
    const w: TicketSummary[] = [];
    const f: TicketSummary[] = [];
    const b: TicketSummary[] = [];
    const d: TicketSummary[] = [];
    for (const t of tickets) {
      // A ticket that has just finished stays in the zone it finished in until its row has left.
      // Sorted into Completed on the frame it turned Done, it would unmount before the exit had a
      // single frame to play — which is what "it just disappears" was. Not a missing animation:
      // a row that was no longer there to animate.
      const zone = landing.get(t.id)?.zone ?? zoneOf(t, kindOf);
      if (zone === 'waiting') w.push(t);
      else if (zone === 'done') d.push(t);
      else if (zone === 'backlog') b.push(t);
      else f.push(t);
    }
    // Both zones sort a landing ticket by where it was rather than where it has just got to, so
    // the row holds its exact place in the list while it leaves. Sorting it by Done would send it
    // to the end of Awaiting your call and to the back of In flight, and the jump would land
    // before the exit rather than instead of it — which reads as the board losing the row and
    // then finding it somewhere else.
    const stood = (t: TicketSummary): { phase: Phase; phaseBeforeBlock?: Phase | null } =>
      landing.get(t.id) ?? t;
    w.sort((a, b2) => {
      const x = stood(a), y = stood(b2);
      const ab = x.phase === 'Blocked' ? 1 : 0;
      const bb = y.phase === 'Blocked' ? 1 : 0;
      return ab !== bb ? ab - bb : stationOf(x) - stationOf(y);
    });
    f.sort((a, b2) =>
      (kindOf.get(stood(a).phase) === 'active' ? 0 : 1) - (kindOf.get(stood(b2).phase) === 'active' ? 0 : 1));
    d.sort((a, b2) => b2.updatedAt.localeCompare(a.updatedAt));
    // The window is applied here rather than at the strip, so the count on the header, the rows
    // under it and the auto-expand below are all reading one list. `updatedAt` is what a finished
    // ticket has for a completion date — the sort directly above already treats it as one.
    //
    // A ticket mid-landing is kept whatever the window says: it finished seconds ago, and the one
    // thing that must not happen on the beat a row files itself away is the strip refusing it.
    const cut = Date.now() - DONE_WINDOW_MS;
    const within = doneWindow === 'all'
      ? d
      : d.filter(t => landing.has(t.id) || new Date(t.updatedAt).getTime() >= cut);
    return { waiting: w, flight: f, backlog: b, done: within, doneTotal: d.length };
  }, [tickets, kindOf, landing, doneWindow]);

  // Which agent is on which ticket. Several at once now, so this is a lookup rather than a single
  // id: a row asks "is anyone working me, and who", and the dock asks the mirror question of
  // whether an agent's ticket is on screen at all (the project filter can hide it).
  const agentByTicket = useMemo(() => {
    const m = new Map<number, AgentState>();
    if (state.status !== 'running') return m;
    for (const a of state.agents) {
      const id = a.ticketId ? Number(a.ticketId.replace(/\D/g, '')) : NaN;
      if (!Number.isNaN(id)) m.set(id, a);
    }
    return m;
  }, [state.status, state.agents]);

  // What is parked at each station, for the arc. Every ticket on the board is a ship somewhere,
  // including the ones folded away in Backlog and Done — the arc is the whole sector, not just
  // the part currently scrolled into view.
  //
  // Each ship also carries whether an agent is holding it — the same question the rows answer by
  // standing a mascot over one, asked of the header. It is what lets a station tell work in hand
  // from cargo waiting at the dock without adding a second mark to read.
  const load = useMemo<StationLoad[]>(() => {
    const l: StationLoad[] = STATIONS.map(() => ({ tiers: [], claimed: [] }));
    for (const t of tickets) {
      const at = l[stationOf(t)];
      at.tiers.push(tierOf(t.phase, kindOf.get(t.phase)));
      at.claimed.push(agentByTicket.has(t.id));
    }
    return l;
  }, [tickets, kindOf, agentByTicket]);

  // The mirror of that: which station each agent is standing at. The berths quote an agent's last
  // line by slot, and this is what lets the arc put the same event on the map.
  //
  // The agent's own phase is preferred over the ticket's: both come from the same heartbeat, but
  // the ticket list is refetched a beat later, so during that gap the ticket still reads as the
  // phase the agent has already moved on from. The ticket is the fallback, and the one that can
  // answer for a blocked agent — Blocked is not a place on the arc, and only the ticket remembers
  // which station it was blocked out of.
  const stationByAgent = useMemo(() => {
    const m = new Map<number, number>();
    if (state.status !== 'running') return m;
    const byId = new Map(tickets.map(t => [t.id, t]));
    for (const a of state.agents) {
      const id = a.ticketId ? Number(a.ticketId.replace(/D/g, '')) : NaN;
      const t = Number.isNaN(id) ? undefined : byId.get(id);
      const phase = a.phase as Phase | undefined;
      const at = phase && phase !== 'Blocked' && phase in STATION_OF ? STATION_OF[phase]
               : t ? stationOf(t)
               : null;
      if (at != null) m.set(a.slot, at);
    }
    return m;
  }, [state.status, state.agents, tickets]);

  const pulses = useStationPulse(events, stationByAgent);

  const flightIds = useMemo(() => new Set(flight.map(t => t.id)), [flight]);

  // Everything the stage filter lets through. Recomputed from the ticket list rather than stored,
  // so a ticket that moves stage under the heartbeat leaves or joins the filtered board on its own.
  const visibleIds = useMemo(
    () => new Set(tickets.filter(t => !filtering || stationFilter.has(stationOf(t))).map(t => t.id)),
    [tickets, filtering, stationFilter],
  );
  const rowFx = useRowFade(visibleIds);
  /**
   * Rows to render: what the filter allows, plus what it has just dropped and is still fading,
   * plus what has just finished. A completion outranks the stage filter — a ticket that lands at
   * a stage you have filtered past would otherwise be taken off the board silently, on the one
   * beat the board had something to say about it.
   */
  const shown = useCallback(
    (rows: TicketSummary[]) =>
      rows.filter(t => visibleIds.has(t.id) || rowFx.get(t.id) === 'out' || landing.has(t.id)),
    [visibleIds, rowFx, landing],
  );
  /** ...and the honest count, which is only the first half of that. */
  const kept = useCallback(
    (rows: TicketSummary[]) => rows.reduce((n, t) => n + (visibleIds.has(t.id) ? 1 : 0), 0),
    [visibleIds],
  );

  // What the manifest line counts: the work, not the archive. Finished tickets only ever
  // accumulate, so counting them here made the number a running total of everything the board had
  // ever seen — it went up and never down, and said nothing about how much was actually in hand.
  //
  // Summed from the three open zones rather than filtered out of `tickets`, so it agrees with the
  // rows on screen on both counts: a ticket mid-landing is still standing in the zone it finished
  // in, and Completed's date window cannot move a number that never reads that list.
  const openCount = waiting.length + flight.length + backlog.length;
  const openVisible = kept(waiting) + kept(flight) + kept(backlog);

  // A folded strip cannot fade rows it is not rendering, so a filter that only matches tickets in
  // Backlog or Completed would read as a board that had emptied itself. Open the strips that hold
  // matches when the filter changes — once, on the change, so collapsing one again afterwards
  // sticks rather than being undone by the next heartbeat refresh.
  const folded = useRef({ backlog, done });
  folded.current = { backlog, done };
  useEffect(() => {
    if (stationFilter.size === 0) return;
    const hit = (rows: TicketSummary[]) => rows.some(t => stationFilter.has(stationOf(t)));
    setExpanded(e => {
      const n = { ...e };
      if (!n.Backlog && hit(folded.current.backlog)) n.Backlog = true;
      if (!n.Completed && hit(folded.current.done)) n.Completed = true;
      return n.Backlog === e.Backlog && n.Completed === e.Completed ? e : n;
    });
  }, [stationFilter]);

  const selected = useMemo(() => tickets.find(t => t.id === selectedId) ?? null, [tickets, selectedId]);

  // Escape closes the inspect panel, the way it closes every other overlay on this board.
  useEffect(() => {
    if (selectedId == null) return;
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setSelectedId(null); };
    document.addEventListener('keydown', esc);
    return () => document.removeEventListener('keydown', esc);
  }, [selectedId]);

  // ...and with nothing open, it drops the stage filter. A filter you set from a node at the top
  // of the page is one you can easily scroll away from, so it needs an exit that is not "find the
  // arc again" — this and the clear button on the manifest line are the two.
  useEffect(() => {
    if (selectedId != null || !filtering) return;
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setStationFilter(new Set()); };
    document.addEventListener('keydown', esc);
    return () => document.removeEventListener('keydown', esc);
  }, [selectedId, filtering]);

  async function move(t: TicketSummary, to: Phase) {
    setMenuFor(null);
    setActionError(null);
    let reason: string | undefined;
    if (to === 'Blocked') {
      reason = window.prompt('Reason for blocking?') ?? undefined;
      if (reason === undefined) return; // cancelled
    }
    // Unblocking asks for the answer, because the run that picks the ticket up is shown it. Optional
    // (the fix is often an edit to the ticket itself), but asked for here rather than left to be
    // remembered — an unblock with nothing attached tells the next worker only that it may continue.
    if (t.phase === 'Blocked') {
      const answer = window.prompt(
        `Anything Claude should know before it picks this back up?\n\nBlocked: ${t.blockedReason ?? '(no reason recorded)'}`,
        '',
      );
      if (answer === null) return; // cancelled
      reason = answer.trim() || undefined;
    }
    try {
      await transitionTicket(t.id, to, reason);
      await refreshTickets();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Transition failed');
    }
  }

  // The hovered station, but only when it is one the filter would actually spotlight. Without this
  // guard, pointing at a station you have filtered *out* dims every row on the board to answer a
  // question about tickets that are not on it — the board would go dark and say nothing.
  const spotStation = hoverStation != null && (!filtering || stationFilter.has(hoverStation)) ? hoverStation : null;

  const rowProps = (t: TicketSummary) => {
    const at = stationOf(t);
    return {
      t,
      // Pull request or TFVC check-in: decides which git buttons the row offers.
      repoType: projects.find(p => p.key === t.projectKey)?.repoType,
      mergeInsteadOfPr: projects.find(p => p.key === t.projectKey)?.mergeInsteadOfPr,
      // Your own filing, from its own request — see useTickets. Rows wearing nothing get the shared
      // empty array rather than a fresh one, so an unlabelled row's props stay reference-stable.
      labels: labels[String(t.id)] ?? NO_LABELS,
      kind: kindOf.get(t.phase),
      nextPhases: nextOf.get(t.phase) ?? [],
      showProjectBadge,
      menuOpen: menuFor === t.id,
      onToggleMenu: () => setMenuFor(menuFor === t.id ? null : t.id),
      onOpen: () => setSelectedId(t.id),
      onMove: move,
      selected: selectedId === t.id,
      // Pointing at a station picks its tickets out of the list and pushes everything else back.
      spotlit: spotStation === at,
      dimmed: spotStation != null && spotStation !== at,
      // Filing away outranks a filter fade: both would take the row off the board, and only one
      // of them is the reason it is going.
      fx: landing.has(t.id) ? 'file' as const : rowFx.get(t.id),
    };
  };

  /** The two folded zones. Reference, not workload — they sit as strips until you ask for them. */
  const foldSection = (o: {
    title: string; rows: TicketSummary[]; tier: keyof typeof TIER; empty: string;
    /** Arrivals so far, for the strip a finished row is filing itself into. Absent elsewhere. */
    landed?: number;
    /** What the count is counting, when the strip only holds part of its pile. */
    note?: string;
  }) => {
    const open = !!expanded[o.title];
    const c = TIER[o.tier].c;
    // A station's tickets can be folded away in here, where dimming the rows around them says
    // nothing. The strip answers for them: it reports its own share of the hovered station rather
    // than leaving a count on the arc that appears to point at an empty board.
    const here = spotStation == null ? 0 : o.rows.filter(t => stationOf(t) === spotStation).length;
    const spotlit = here > 0;
    const dimmed = spotStation != null && !spotlit;
    // Open, the rows below carry the filter themselves. Shut, the strip is all there is, so it has
    // to say how much of its pile survived rather than how big the pile is.
    const count = filtering ? kept(o.rows) : o.rows.length;
    return (
      <button
        onClick={() => setExpanded(e => ({ ...e, [o.title]: !open }))}
        aria-expanded={open}
        className="relative flex-1 flex items-center gap-2.5 text-left"
        style={{
          clipPath: chamfer(10),
          background: spotlit
            ? `linear-gradient(100deg,${TIER[o.tier].bg},rgb(var(--w-line-rgb) / .07))`
            : open ? `linear-gradient(100deg,${TIER[o.tier].bg},rgb(var(--w-panel-deep-rgb) / .55))` : 'rgb(var(--w-panel-deep-rgb) / .4)',
          backdropFilter: 'blur(6px)',
          border: `1px solid ${spotlit ? `${fade(c, 67)}` : open ? `${fade(c, 33)}` : 'rgb(var(--w-line-rgb) / .1)'}`,
          boxShadow: spotlit ? `inset 0 0 26px -9px ${c}` : undefined,
          padding: '9px 16px',
          opacity: dimmed ? 0.3 : 1,
          filter: dimmed ? 'saturate(.35)' : undefined,
          transition: 'background .16s ease,border-color .16s ease,box-shadow .16s ease,opacity .18s ease,filter .18s ease',
        }}
      >
        {/* The seam arriving. Keyed on the arrival count rather than toggled by a flag, which is
            what lets it replay: two tickets finishing a second apart are two deliveries, and a
            flag already standing at true would show the second one nothing. */}
        {!!o.landed && <span key={o.landed} className="strip-land" aria-hidden />}
        <span style={{ font: `700 10px ${DISPLAY}`, letterSpacing: track(.3), textTransform: CAPS, color: c }}>
          {o.title}
        </span>
        {/* Ticks on the same beat, for the same reason — and the Backlog strip, which passes no
            arrival count, holds a key that never changes and so never ticks. */}
        <span
          key={o.landed ?? 0}
          className={o.landed ? 'strip-count' : undefined}
          style={{ font: `700 12px ${MONO}`, color: 'var(--w-t200)' }}
        >
          {String(count).padStart(2, '0')}
        </span>
        {filtering && count !== o.rows.length && (
          <span style={{ font: `400 10px ${MONO}`, color: 'var(--w-t700)' }}>of {o.rows.length}</span>
        )}
        {/* The window the count was taken through, said on the header whether the strip is open or
            shut. Shut, it is the only thing standing between "nothing finished" and "nothing
            finished lately", which are not the same board. */}
        {o.note && (
          <span style={{ font: `600 9px ${DISPLAY}`, letterSpacing: track(.18), textTransform: CAPS, color: 'var(--w-t600)' }}>
            {o.note}
          </span>
        )}
        {/* Only worth saying while the strip is shut. Open, the rows below are already lit, and
            the count here would just be the number sitting two words to its left said twice. */}
        {spotlit && !open && (
          <span style={{ font: `600 9.5px ${DISPLAY}`, letterSpacing: track(.18), textTransform: CAPS, color: c }}>
            {here} at this stage · expand
          </span>
        )}
        <span className="flex-1" />
        <span style={{ font: `400 10px ${BODY}`, color: 'var(--w-t700)' }}>{open ? 'collapse ▴' : 'expand ▾'}</span>
      </button>
    );
  };

  const foldRows = (o: { title: string; rows: TicketSummary[]; empty: string; head?: ReactNode }) => {
    if (!expanded[o.title]) return null;
    const rows = shown(o.rows);
    return (
      <div style={{ padding: `0 var(--board-gutter) 12px`, gap: 6, ['--rowgap' as string]: '6px' } as CSSProperties} className="flex flex-col">
        {/* Controls for the list, above the list. They cannot live on the header itself: the header
            is one button, and a button inside a button is neither valid nor clickable. */}
        {o.head}
        {kept(o.rows) === 0 && rows.length === 0 ? (
          <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t700)', padding: '6px 4px' }}>
            {filtering && o.rows.length > 0 ? 'Nothing here at the selected stages.' : o.empty}
          </div>
        ) : (
          rows.map((t, i) => <OrbitRow key={t.id} {...rowProps(t)} variant="flight" delay={`${(i % 5) * 0.3}s`} />)
        )}
      </div>
    );
  };

  return (
    <div
      className={narrow ? 'px-2 py-2' : 'px-6 py-4'}
      style={{ ['--board-gutter' as string]: `${narrow ? GUTTER_NARROW : GUTTER}px` } as CSSProperties}
    >
      {/* No background of its own: one sky is mounted at the root behind every tab, and a shell
          that painted itself would cover the very thing it used to contain. */}
      <div className="relative flex flex-col min-h-[calc(100vh-9rem)]">

        {/* ---- Station header ---- */}
        <div style={{ padding: `${narrow ? 10 : 24}px var(--board-gutter) 0` }}>
          <PageHead
            eyebrow="Whetstone // pipeline"
            title="Board"
            left={
              /* Project filter, as sector selectors rather than a dropdown: three or four projects
                 is a row of chips, and a chip you can see is one you can count tickets against. */
              <div className="flex gap-0.5" style={{ paddingBottom: 5 }}>
                <Chip on={projectFilter === null} onClick={() => setProjectFilter(null)}>All</Chip>
                {projects.map(p => (
                  <Chip
                    key={p.key}
                    on={projectFilter === p.key}
                    onClick={() => setProjectFilter(p.key)}
                    title={p.displayName}
                  >
                    {p.key}
                  </Chip>
                ))}
              </div>
            }
            right={<>
              <AgentDock
                status={state.status}
                wakeAt={wakeAt}
                workers={state.workers}
                agents={state.agents}
                visible={flightIds}
                events={events}
              />
              <PrimaryButton onClick={() => setCreating(true)}>New Ticket</PrimaryButton>
            </>}
          />
        </div>

        {/* Thin manifest line: what the sector holds, plus the repo controls for the current filter. */}
        <div
          className="relative flex flex-wrap items-center gap-3"
          style={{ padding: `10px var(--board-gutter) 0`, font: `400 10.5px ${BODY}`, color: 'var(--w-t600)' }}
        >
          <span style={{ font: `600 9.5px ${DISPLAY}`, letterSpacing: track(.22), textTransform: CAPS }}>
            {filtering && `${openVisible} of `}{openCount} open ticket{openCount === 1 ? '' : 's'} · {projects.length} project{projects.length === 1 ? '' : 's'}
          </span>
          {/* Which projects the fleet may actually claim from - the same switch as the Heartbeat
              page, restated here because this is where you are standing when you notice it is
              working the wrong client. Not to be confused with the chips above: those choose what
              this board shows, these choose what the agents are allowed to touch. */}
          <HeartbeatProjectStrip projects={projects} />
          {/* The other half of the same question, on the other axis: which priority bands the fleet
              may claim from. Sits next to the project switch rather than near the priority marks on
              the rows, because those say where a ticket sits in the queue and this says which
              queues are open - and the row marks are per-ticket while this is fleet-wide. */}
          <HeartbeatPriorityStrip />
          {/* The filter, restated where the board is rather than where you set it. The arc scrolls
              off the top of a long board, and a filter whose only handle has scrolled away is a
              board that has quietly lost tickets. */}
          {filtering && (
            <button
              onClick={() => setStationFilter(new Set())}
              title="Clear the stage filter (Esc)"
              className="orbit-btn flex items-center gap-2"
              style={{
                clipPath: chamfer(7),
                padding: '3px 9px',
                ['--rbg' as string]: 'rgb(var(--w-accent-rgb) / .1)',
                ['--rbw' as string]: '1px solid rgb(var(--w-accent-rgb) / .4)',
                ['--hbg' as string]: 'rgb(var(--w-accent-rgb) / .18)',
                ['--hbc' as string]: 'rgb(var(--w-accent-rgb) / .75)',
              } as CSSProperties}
            >
              <span style={{ font: `700 9px ${DISPLAY}`, letterSpacing: track(.22), textTransform: CAPS, color: 'var(--w-accent)' }}>
                {[...stationFilter].sort((a, b) => a - b).map(s => STATIONS[s].short).join(' · ')}
              </span>
              <span style={{ font: `400 11px ${BODY}`, color: 'var(--w-t400)', lineHeight: 1 }}>✕</span>
            </button>
          )}
          <ProjectGitBar projectFilter={projectFilter} />
          {actionError && <span style={{ color: 'var(--w-blocked-300)' }}>{actionError}</span>}
          {error && <span style={{ color: 'var(--w-blocked-300)' }}>{error}</span>}
        </div>

        {loading || phases.length === 0 ? (
          <div className="relative flex-1 text-center py-24" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)' }}>
            Charting the sector…
          </div>
        ) : (
          <div className={`relative flex-1 ${narrow ? '' : 'overflow-x-auto'}`}>
            <div style={{ minWidth: narrow ? undefined : 1180 }}>
              {/* The arc is a map nine stations wide, and every row is ruled to its columns. A
                  phone has room for neither, so there the board is the list alone: each row says
                  its own phase on its chip, which is the same fact the column was drawing.
                  +1 for the row shell's own border, which the grid inside it sits behind. */}
              {!narrow && <SectorArc
                load={load}
                // Which lanes to draw, from the per-project setting rather than from what happens
                // to be parked on the board.
                tracks={shownTracks}
                // The lane is the fleet's pulse, not a decoration: it crawls at working pace only
                // while an agent is actually out, drifts while the heartbeat rests, and is held
                // still and cold when the heartbeat is not running at all.
                status={state.status}
                // A ticket reaching Done arrives at the last station on the arc, and the arc says
                // so on the same beat the row below starts filing itself away.
                burst={touchdown ? { station: STATIONS.length - 1, key: touchdown } : null}
                // Every line an agent prints, flashed on the station it was printed at.
                pulses={pulses}
                insetLeft={GUTTER + ROW_PAD_L + 1}
                insetRight={GUTTER + ROW_PAD_R + 1}
                onHoverStation={setHoverStation}
                selected={stationFilter}
                onToggleStation={toggleStation}
              />}

              <SectionRule
                colour="var(--w-gate)"
                title="Awaiting your call"
                count={filtering ? kept(waiting) : waiting.length}
              />
              <div style={{ padding: `0 var(--board-gutter) 12px`, gap: 8, ['--rowgap' as string]: '8px' } as CSSProperties} className="flex flex-col">
                {kept(waiting) === 0 && shown(waiting).length === 0 ? (
                  <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t700)', padding: '2px 4px' }}>
                    {filtering && waiting.length > 0
                      ? 'Nothing awaiting you at the selected stages.'
                      : 'Nothing needs your attention right now.'}
                  </div>
                ) : (
                  shown(waiting).map((t, i) => (
                    <OrbitRow key={t.id} {...rowProps(t)} variant="waiting" delay={`${(i % 4) * 0.5}s`} />
                  ))
                )}
              </div>

              <SectionRule
                colour="var(--w-accent)"
                title="In flight"
                meta={`Claude, autonomously · ${String(filtering ? kept(flight) : flight.length).padStart(2, '0')}`}
              />
              <div style={{ padding: `0 var(--board-gutter) 12px`, gap: 3, ['--rowgap' as string]: '3px' } as CSSProperties} className="flex flex-col">
                {kept(flight) === 0 && shown(flight).length === 0 ? (
                  <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t700)', padding: '2px 4px' }}>
                    {filtering && flight.length > 0 ? 'Nothing in flight at the selected stages.' : 'Nothing in flight.'}
                  </div>
                ) : (
                  shown(flight).map((t, i) => {
                    const worker = agentByTicket.get(t.id);
                    const fx = rowFx.get(t.id);
                    return (
                      <Fragment key={t.id}>
                        {/* The agent marker belongs to the row beneath it, so it leaves on the same
                            beat. Left behind, it would be a mascot beaming down at a ticket the
                            filter has just taken off the board. */}
                        {worker && !narrow && (
                          <div className="orbit-fade" data-fx={fx}>
                            <AgentHover phase={worker.phase} station={stationOf(t)} agent={worker.slot} />
                          </div>
                        )}
                        <OrbitRow {...rowProps(t)} variant="flight" workedBy={worker?.slot} delay={`${(i % 5) * 0.3}s`} />
                      </Fragment>
                    );
                  })
                )}
              </div>

              <div className={`flex ${narrow ? 'flex-col gap-1.5' : 'gap-2.5'}`} style={{ padding: `2px var(--board-gutter) 12px` }}>
                {foldSection({ title: 'Backlog', rows: backlog, tier: 'backlog', empty: 'Backlog is empty.' })}
                {foldSection({
                  title: 'Completed', rows: done, tier: 'done', landed: filed,
                  empty: 'Nothing finished yet.',
                  note: DONE_WINDOWS.find(w => w.key === doneWindow)?.label,
                })}
              </div>
              {foldRows({ title: 'Backlog', rows: backlog, empty: 'Backlog is empty.' })}
              {foldRows({
                title: 'Completed',
                rows: done,
                empty: doneWindow === '30d' && doneTotal > 0
                  ? 'Nothing finished in the last 30 days.'
                  : 'Nothing finished yet.',
                head: (
                  <div className="flex items-center gap-1.5" style={{ paddingBottom: 2 }}>
                    {/* Names its own section. Backlog's rows can be open above these, and both
                        lists run the full width — the chips have to say which pile they cut. */}
                    <span style={{ font: `600 9px ${DISPLAY}`, letterSpacing: track(.22), textTransform: CAPS, color: 'var(--w-t700)' }}>
                      <span style={{ color: TIER.done.c }}>Completed</span> · showing
                    </span>
                    {DONE_WINDOWS.map(w => (
                      <Chip key={w.key} on={doneWindow === w.key} onClick={() => setDoneWindow(w.key)}>
                        {w.label}
                      </Chip>
                    ))}
                    {/* Only while something is actually being held back — otherwise the two chips
                        show the same list and saying so twice is noise. */}
                    {doneWindow === '30d' && doneTotal > done.length && (
                      <span style={{ font: `400 10px ${BODY}`, color: 'var(--w-t700)', marginLeft: 3 }}>
                        {doneTotal - done.length} older hidden
                      </span>
                    )}
                  </div>
                ),
              })}
              <div style={{ height: 12 }} />
            </div>
          </div>
        )}
      </div>

      {selected && (
        <InspectPanel
          t={selected}
          kind={kindOf.get(selected.phase)}
          agent={agentByTicket.get(selected.id)}
          phases={phases}
          onClose={() => setSelectedId(null)}
          onChanged={refreshTickets}
        />
      )}

      {creating && (
        <CreateTicketModal
          projects={projects}
          onClose={() => setCreating(false)}
          onCreated={async (id) => { setCreating(false); await refreshTickets(); setSelectedId(id); }}
        />
      )}
    </div>
  );
}

// ---- Filter transitions ----

type RowFx = 'in' | 'out' | 'file';

/** Must outlast the rowin/rowout keyframes in index.css, or a row unmounts mid-collapse. */
const FADE_MS = 280;

/**
 * Which rows are currently arriving or leaving, given the set the filter allows through.
 *
 * The board cannot simply stop rendering a filtered-out row: a row that vanishes between two
 * renders has no frames to fade over. So the departing ids are held here for the length of the
 * animation — long enough for the row to fade and collapse — and only then let go, at which point
 * React unmounts a row that is already invisible and occupies no height. Arrivals need no such
 * grace, but they are tracked the same way so they get the mirror animation instead of appearing
 * at full opacity the instant the filter changes.
 */
function useRowFade(visible: Set<number>): ReadonlyMap<number, RowFx> {
  const [fx, setFx] = useState<ReadonlyMap<number, RowFx>>(() => new Map());
  // Rendered ids as of the last settled state. A ref rather than state: comparing against it is
  // what *produces* the state below, so making it state would loop.
  const prev = useRef<Set<number> | null>(null);
  const timers = useRef(new Map<number, { fx: RowFx; handle: ReturnType<typeof setTimeout> }>());

  // Layout, not a plain effect, and the difference is the whole bug it fixes. A plain effect runs
  // after the browser has painted, so the first frame the board shows is the one where the
  // departing rows have already been dropped from the list but have not yet been told to fade:
  // the list snaps shut, springs back open holding the rows it is about to take away, and only
  // then plays the animation. React flushes a layout effect's state update before that paint, so
  // the intermediate frame is committed but never shown.
  //
  // Doing the same work during render would skip even the commit, but a ref mutated mid-render is
  // not idempotent, and React's development double-render calls the component twice against the
  // same ref: the second pass sees nothing left to react to and the update is dropped on the floor.
  useLayoutEffect(() => {
    const before = prev.current;
    prev.current = visible;
    // First pass. The board has its own entrance; rows animating in on top of it would be a
    // second, competing arrival for something that was never filtered.
    if (!before) return;

    const changed = new Map<number, RowFx>();
    for (const id of visible) if (!before.has(id)) changed.set(id, 'in');
    for (const id of before) if (!visible.has(id)) changed.set(id, 'out');
    // The heartbeat refetches tickets every few seconds, and nearly every one of those rebuilds
    // this set with identical contents. This is where that stops being a re-render.
    if (changed.size === 0) return;

    setFx(m => new Map([...m, ...changed]));
  }, [visible]);

  // The timers only clean up after the animations; nothing is waiting on them to be scheduled, so
  // they can wait for the commit. Each is tagged with the animation it was started for, because a
  // row can reverse mid-flight — filtered out, then clicked back in before it has finished going —
  // and a timer left over from the journey it is no longer making would cut the new one short.
  useEffect(() => {
    for (const [id, kind] of fx) {
      const pending = timers.current.get(id);
      if (pending?.fx === kind) continue;
      if (pending) clearTimeout(pending.handle);
      timers.current.set(id, {
        fx: kind,
        handle: setTimeout(() => {
          timers.current.delete(id);
          setFx(m => {
            if (m.get(id) !== kind) return m;
            const next = new Map(m);
            next.delete(id);
            return next;
          });
        }, FADE_MS),
      });
    }
  }, [fx]);

  useEffect(() => {
    const pending = timers.current;
    return () => { for (const t of pending.values()) clearTimeout(t.handle); };
  }, []);

  return fx;
}

// ---- Reaching Done ----

/** How long a finished row is held where it finished. Must outlast the rowfile keyframes. */
const LAND_MS = 1300;
/**
 * ...and how long it is held when the exit has been reduced to a plain collapse. The hold exists
 * to give the animation room, so it has to shrink with it: left at the full length, the row would
 * shut in a quarter of a second and the board would then sit still for another half, before the
 * Completed count ticked on its own for no reason anyone could see.
 */
const LAND_MS_REDUCED = 300;

/**
 * Tickets that have just reached Done, each mapped to where it stood when it got there.
 *
 * The board re-sorts every ticket into a zone on every render, so the instant one turns Done it
 * belongs to Completed — a strip that is folded shut by default. Its row is not faded or
 * collapsed; it is simply not rendered by the next render. That is why finishing a ticket read as
 * the row blinking out: there was nothing to animate, because there was no longer a row.
 *
 * So a landing is held here for the length of its exit, and the split asks this before it asks
 * the phase — for the list the row belongs in and for its place within it, both of which the
 * transition has just changed underneath it. The row keeps exactly where it was, wearing the done
 * tier it has just been given, long enough to file itself away. Only when the hold expires does the ticket
 * join Completed and that strip's count go up — which is what puts the tick down there on the same
 * beat as the seam arriving, rather than a beat before it has set off.
 *
 * touchdown and filed are counters rather than flags because they drive animations that have to be
 * able to replay. A flag already standing at true has nothing to say to the second ticket to
 * finish within a second.
 */
function useLanding(
  tickets: TicketSummary[],
  kindOf: ReadonlyMap<Phase, PhaseKind>,
  ready: boolean,
): { landing: ReadonlyMap<number, Standing>; touchdown: number; filed: number } {
  const [landing, setLanding] = useState<ReadonlyMap<number, Standing>>(() => new Map());
  const [touchdown, setTouchdown] = useState(0);
  const [filed, setFiled] = useState(0);
  // Where every ticket stood as of the last look. Null until there has been one, which is what
  // stops a page load reading a board full of finished work as a board full of things finishing.
  const seen = useRef<Map<number, Standing> | null>(null);
  const timers = useRef(new Map<number, ReturnType<typeof setTimeout>>());

  // A layout effect, for the same reason useRowFade needs one: the render that first reports a
  // ticket as Done is the render that drops its row, and a plain effect would let that frame reach
  // the screen — the list would snap shut and only then play an exit for a row it had already
  // taken away. React flushes this before the paint, so that frame is committed but never shown.
  useLayoutEffect(() => {
    // Phases arrive from a request of their own. Until they land, kindOf is empty and every ticket
    // reads as 'flight' — the finished ones included, which would then all appear to touch down
    // at once the moment the phase list turned up.
    if (!ready) return;
    const before = seen.current;
    const now = new Map<number, Standing>();
    for (const t of tickets) {
      now.set(t.id, { zone: zoneOf(t, kindOf), phase: t.phase, phaseBeforeBlock: t.phaseBeforeBlock });
    }
    seen.current = now;
    if (!before) return;

    const fresh = new Map<number, Standing>();
    for (const [id, standing] of now) {
      const was = before.get(id);
      // A ticket first seen already finished is history, not news.
      if (standing.zone !== 'done' || was === undefined || was.zone === 'done') continue;
      // The whole previous record, not just its zone: the sorts need the phase it stood at.
      fresh.set(id, was);
    }
    // The heartbeat refetches every few seconds and almost every one of those reports the same
    // board. This is where that stops being a re-render.
    if (fresh.size === 0) return;
    setLanding(m => new Map([...m, ...fresh]));
    setTouchdown(n => n + 1);
  }, [tickets, kindOf, ready]);

  // Nothing is waiting on these to be scheduled, so they can wait for the commit. Keyed by ticket
  // rather than one timer for the batch: two tickets finishing a moment apart each get a full exit.
  useEffect(() => {
    for (const id of landing.keys()) {
      if (timers.current.has(id)) continue;
      timers.current.set(id, setTimeout(() => {
        timers.current.delete(id);
        setLanding(m => {
          if (!m.has(id)) return m;
          const next = new Map(m);
          next.delete(id);
          return next;
        });
        setFiled(n => n + 1);
      }, window.matchMedia('(prefers-reduced-motion: reduce)').matches ? LAND_MS_REDUCED : LAND_MS));
    }
  }, [landing]);

  useEffect(() => {
    const pending = timers.current;
    return () => { for (const h of pending.values()) clearTimeout(h); };
  }, []);

  return { landing, touchdown, filed };
}

/**
 * How often one station will flash, at most. An agent mid-tool-run prints in bursts, and a flash
 * per line would be a strobe rather than a pulse; a whole burst arriving in one push collapses to
 * a single flash, because it happened in a single moment.
 */
const PULSE_GAP_MS = 220;

/**
 * The board's other clock: an agent talking, drawn on the map instead of in a berth.
 *
 * The berths already quote each working agent's last line, and that answers "is it still moving?"
 * for the fleet. This answers the same question about a place — the ticket in Spec has an agent on
 * it, and the SPEC node lights up every time that agent does something. It is the same event the
 * berth is quoting, put where the work is.
 *
 * Events are a growing buffer, not a stream, so "what is new" is whatever came after the last one
 * seen. The first pass only takes note of where the buffer ends: the connection opens by replaying
 * up to five hundred events, and replaying them as flashes would open the board with a firework
 * show of work that finished hours ago.
 */
function useStationPulse(
  events: HeartbeatEvent[],
  stationByAgent: ReadonlyMap<number, number>,
): ReadonlyMap<number, StationPulse> {
  const [pulses, setPulses] = useState<ReadonlyMap<number, StationPulse>>(() => new Map());
  // Read through a ref rather than a dependency: where the agents are standing changes on its own
  // beat, and re-running this on that beat would replay the newest line every time they moved.
  const where = useRef(stationByAgent);
  where.current = stationByAgent;
  const seen = useRef<string | null>(null);
  const beat = useRef(0);
  const lastAt = useRef(new Map<number, number>());

  useEffect(() => {
    if (events.length === 0) return;
    const tail = events[events.length - 1].id;
    if (seen.current === null) { seen.current = tail; return; }
    if (seen.current === tail) return;

    // A reconnect replaces the whole buffer rather than appending to it. If the last line seen is
    // still in there, everything after it is genuinely new; if it has rolled off the front, only
    // the newest line is safe to claim as news.
    const at = events.findIndex(e => e.id === seen.current);
    const fresh = at >= 0 ? events.slice(at + 1) : events.slice(-1);
    seen.current = tail;

    const now = Date.now();
    const hit = new Map<number, string>();
    for (const e of fresh) {
      if (e.agent == null || FEED_HIDDEN.has(e.type)) continue;
      const station = where.current.get(e.agent);
      if (station == null) continue;
      if (now - (lastAt.current.get(station) ?? 0) < PULSE_GAP_MS) continue;
      hit.set(station, agentAccent(e.agent).hex);
    }
    if (hit.size === 0) return;

    for (const station of hit.keys()) lastAt.current.set(station, now);
    setPulses(prev => {
      const next = new Map(prev);
      for (const [station, colour] of hit) next.set(station, { key: ++beat.current, colour });
      return next;
    });
  }, [events]);

  return pulses;
}

// ---- Header bits ----

/** A zone heading: a lit bar, the name, its count, and a rule that fades into the sky. */
function SectionRule({ colour, title, count, meta }: {
  colour: string; title: string; count?: number; meta?: string;
}) {
  return (
    <div className="relative flex items-center gap-3" style={{ padding: `4px var(--board-gutter) 8px` }}>
      <span style={{ width: 3, height: 14, background: colour, boxShadow: `0 0 12px ${colour}` }} />
      <span style={{ font: `700 10.5px ${DISPLAY}`, letterSpacing: track(.3), color: colour, textTransform: CAPS }}>
        {title}
      </span>
      {count != null && (
        <span style={{ font: `700 10px ${MONO}`, color: 'var(--w-void)', background: colour, padding: '1px 7px' }}>
          {String(count).padStart(2, '0')}
        </span>
      )}
      {meta && (
        <span style={{ font: `400 9.5px ${DISPLAY}`, letterSpacing: track(.18), color: 'var(--w-t600)', textTransform: CAPS }}>
          {meta}
        </span>
      )}
      <span style={{ flex: 1, height: 1, background: `linear-gradient(90deg,${fade(colour, 44)},transparent)` }} />
    </div>
  );
}

// ---- The row ----

/**
 * One ticket, on the same nine columns as every other ticket and as the arc above them.
 *
 * Two variants of one shell. A row awaiting a decision leads with its tier and its buttons; a row
 * in flight leads with its title and reports its status. Everything else — the chamfer, the tier
 * bar, the bracket corners that snap in on hover, the note that unfolds under the title — is
 * shared, because they are the same object in two states, not two kinds of object.
 *
 * Hover is pure CSS (`.orbit-row` sets `--rv`), so a mouse crossing a busy board costs no renders.
 */
function OrbitRow({
  t, labels, kind, nextPhases, showProjectBadge, variant, workedBy, menuOpen, selected, spotlit, dimmed, fx,
  onToggleMenu, onOpen, onMove, delay, repoType, mergeInsteadOfPr,
}: {
  t: TicketSummary;
  /** Your filing tags for this ticket. Never on `t` itself — see useTickets. */
  labels: LabelRef[];
  kind: PhaseKind | undefined;
  nextPhases: Phase[];
  showProjectBadge: boolean;
  variant: 'waiting' | 'flight';
  /** Slot of the agent working this row right now, if any — the row wears that agent's colour. */
  workedBy?: number;
  menuOpen: boolean;
  selected: boolean;
  /** This row is parked at the station being pointed at on the arc. */
  spotlit: boolean;
  /** Some other station is being pointed at, and this row is not at it. */
  dimmed: boolean;
  /** Mid-arrival or mid-departure under the stage filter; absent once the row has settled. */
  fx?: RowFx;
  onToggleMenu: () => void;
  onOpen: () => void;
  onMove: (t: TicketSummary, to: Phase) => void;
  delay?: string;
  /** The ticket's project's delivery: a PR ("git", the default) or a TFVC check-in. */
  repoType?: 'git' | 'tfvc';
  mergeInsteadOfPr?: boolean;
}) {
  const tier = tierToken(t.phase, kind);
  const blocked = t.phase === 'Blocked';
  const glow = kind === 'active' ? '206,174,51' : '150,180,230';
  const shell = useRef<HTMLDivElement>(null);
  // On a phone the row is a card: what it is, then what you can do about it, one under the other.
  // The nine station columns go, and so does everything that waited for a pointer to rest on the
  // row - there is no hover to wait for, so the line it unfolded is simply there.
  const narrow = useNarrow();

  // The collapse has to start from the height this row actually is, and the row is the only thing
  // that knows it. A layout effect writes it on before the browser paints the animation's first
  // frame; a fixed ceiling would spend the first third of the squeeze closing distance that was
  // never open, and the seam would appear late and then snap the rest of the way shut.
  useLayoutEffect(() => {
    if (fx !== 'file' || !shell.current) return;
    shell.current.style.setProperty('--rowh', `${shell.current.offsetHeight}px`);
  }, [fx]);

  const accent = workedBy != null ? agentAccent(workedBy) : null;

  const actions = (GATE_ACTIONS[t.phase] ?? []).filter(a => nextPhases.includes(a.to));
  const rest = nextPhases.filter(p => !actions.some(a => a.to === p));
  const status = rowStatus(t);
  const blockedMenu = variant === 'waiting' && blocked;
  const revealMenu = !blockedMenu && rest.length > 0;

  return (
    <div
      ref={shell}
      className="orbit-row relative"
      // The spotlight nudge and the dimming are set through data attributes rather than inline
      // styles so `.orbit-row:hover` can still win the transform: an inline transform would
      // outrank the stylesheet and a spotlit row would stop lifting when you pointed at it.
      data-spot={spotlit ? '1' : undefined}
      data-dim={dimmed ? '1' : undefined}
      // The fade and collapse are a keyframe animation rather than a transition: a row arriving is
      // arriving from nothing, and there is no previous frame for a transition to run from.
      data-fx={fx}
      // Nothing the stylesheet also wants to set is written here directly — everything the hover
      // rule touches (background, border colour, shadow) is handed over as a custom property and
      // applied by the class. Only the four properties no rule competes for stay inline.
      style={{
        overflow: 'hidden',
        // The chamfer and the tier colour both go over as custom properties rather than as
        // styles. A row filing itself away has to flatten its own corners and light its own seam,
        // and neither a keyframe nor a stylesheet can outrank a style attribute.
        ['--clip' as string]: chamferRow(13),
        ['--tier' as string]: tier.c,
        backdropFilter: 'blur(6px)',
        cursor: 'pointer',
        ['--rbg' as string]: `linear-gradient(100deg,${tier.bg},rgb(var(--w-panel-deep-rgb) / .36))`,
        ['--rbc' as string]: selected || spotlit ? `${tier.c}${spotlit ? 'cc' : '88'}` : 'rgb(var(--w-line-rgb) / .1)',
        // Every glow on this board is inset. The row is cut to a chamfer, and clip-path clips
        // everything an element paints — a halo drawn outside the shape is composited and then
        // discarded, which is what these were doing. Lighting the row from its own edges inward
        // survives the clip, and on a row this wide it reads better anyway: the colour runs the
        // length of the row instead of pooling at the corners.
        ['--rsh' as string]: spotlit
          ? `inset 0 0 38px -9px ${tier.c}`
          : selected ? `inset 0 0 30px -11px ${tier.c}` : 'none',
        ['--hbg' as string]: `linear-gradient(100deg,${tier.bg},rgb(var(--w-line-rgb) / .06) 45%,rgb(var(--w-line-rgb) / .02))`,
        ['--hbc' as string]: `rgba(${glow},.42)`,
        // Hover has to outglow whatever the row was already doing, or pointing at a spotlit row
        // would dim it.
        ['--hsh' as string]: `inset 0 0 ${spotlit ? 46 : 36}px ${spotlit ? -7 : -12}px ${tier.c}`,
        ['--htf' as string]: 'translateY(-2px)',
      } as CSSProperties}
      onClick={onOpen}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(); } }}
    >
      <span style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 3, background: tier.c, boxShadow: `0 0 calc(8px + var(--rv,0) * 10px) ${tier.c}` }} />
      <Bracket tier={tier.c} corner="tl" />
      <Bracket tier={tier.c} corner="br" />

      {/* Named so the exit can crush the row's contents without touching the shell they sit in. */}
      <div
        className="orbit-rowbody"
        style={narrow
          ? { display: 'flex', flexDirection: 'column', gap: 8, padding: '10px 12px 10px 15px' }
          : { ...GRID, padding: `${variant === 'waiting' ? 11 : 9}px ${ROW_PAD_R}px ${variant === 'waiting' ? 11 : 9}px ${ROW_PAD_L}px` }}
      >
        <div style={{ paddingRight: narrow ? 0 : 16, minWidth: 0 }}>
          {/* Same shape in both zones: what phase this is and which ticket it is, then the title.
              The zone a row sits in already implies its phase, but only to someone who knows the
              board — and the implication breaks the moment a row is spotlit, dragged into view by
              a filter, or read on its own. The chip is the row saying it itself. */}
          <div className={`flex items-center gap-2 min-w-0 ${narrow ? 'flex-wrap' : ''}`} style={{ marginBottom: variant === 'waiting' ? 3 : 2 }}>
            {/* The phase, not a rank. Set to be read rather than to look like a badge — less
                tracking than the rest of the display type, and it never shrinks: the borrowed
                identifiers beside it give way first. */}
            <span
              style={{
                font: `700 8.5px ${DISPLAY}`, letterSpacing: track(.12), textTransform: CAPS,
                color: tier.c, background: tier.bg, border: `1px solid ${fade(tier.c, 33)}`, padding: '2px 7px',
                whiteSpace: 'nowrap', flex: 'none',
              }}
            >
              {/* The gate rows are every one of them a Needs-something, so the prefix is the one
                  word on the chip that says nothing — and dropping it is what keeps
                  "Implementation Review" on the line beside the ticket's identifiers. The
                  slide-over still names the phase in full. */}
              {blocked ? '⚠ Blocked' : PHASE_LABEL[t.phase].replace(/^Needs /, '')}
            </span>
            {/* Only when the ticket is off the default band. Drawn against the row rather than in a
                box, so it reads as a mark ON this row instead of a second phase chip — the two sit
                side by side and a Critical ticket's colour would otherwise be a hair off the
                Blocked chip's, which is the one confusion this must not cause. */}
            <PriorityMark t={t} />
            {/* Held whole while an agent is on the row: what it is doing is a sentence that reads
                fine cut short, while the identifiers are a name — and a row that dropped its issue
                key to make room would look like a ticket that has none. */}
            <Ident t={t} showProjectBadge={showProjectBadge} keepWhole={variant === 'flight'} />
          </div>
          <div
            className="orbit-title"
            style={variant === 'waiting'
              ? { font: `600 14.5px/1.25 ${DISPLAY}`, color: 'var(--w-t100)' }
              : { font: `500 13.5px/1.3 ${DISPLAY}`, color: kind === 'terminal' ? 'var(--w-t400)' : 'var(--w-t200)' }}
          >
            {t.title}
          </div>
          {/* Your filing tags, on their own line and only on the rows that carry any. A line the
              row grows rather than width the identifiers give up: labels are the thing you scan a
              board FOR once you have filed anything, and a set truncated to fit beside a phase chip
              would be a set you have to open the drawer to read. */}
          {labels.length > 0 && (
            <span className="flex" style={{ marginTop: 3 }}>
              <LabelChips labels={labels} />
            </span>
          )}
          {/* Why a ticket whose speculation review is switched off is sitting at approval anyway.
              Always shown rather than left to the hover line: without it the stop reads as the gate
              skip having broken. */}
          {variant === 'waiting' && !!t.heldForExpansion && (
            <span
              title="Speculation review is off for this ticket; it stopped here only because the plan goes beyond what the ticket asked for"
              style={{ marginTop: 3, font: `600 10.5px ${BODY}`, color: tier.c, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}
            >
              ⚑ Held · {t.heldForExpansion} expansion item{t.heldForExpansion === 1 ? '' : 's'} beyond the ask
            </span>
          )}
          {/* What the agent is on right now, on a line of its own: it is a whole sentence, and
              sharing the line above with the chip and the identifiers left it about ten readable
              characters. The row only grows while somebody is actually working it.
              Gated on an agent holding the row, because the server scoped the field to the
              ticket's phase but it outlives the run that set it — a row nobody is holding must
              not claim to be mid-item. */}
          {variant === 'flight' && accent && t.currentItem && (
            <span className="flex items-center gap-1.5 min-w-0" style={{ marginTop: 3 }}>
              <AgentMascot mood="running" size={12} agent={workedBy} />
              <span
                style={{ font: `400 10.5px ${BODY}`, color: accent.hex, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}
                title={t.currentItem}
              >
                {t.currentItem}
              </span>
            </span>
          )}

          {/* Unfolds on hover. The context you want before deciding, and the controls that act on
              the branch rather than on the ticket's phase — neither of which belongs in a row you
              are only glancing at. */}
          <div style={narrow ? undefined : { maxHeight: 'calc(var(--rv,0) * 46px)', opacity: 'var(--rv,0)', overflow: 'hidden', transition: 'max-height .22s ease,opacity .18s ease' }}>
            <div className={`flex items-center gap-2 ${narrow ? 'flex-wrap' : ''}`} style={{ paddingTop: 4 }}>
              <span
                style={{
                  font: `400 ${variant === 'waiting' ? 11 : 10.5}px/1.5 ${BODY}`,
                  color: blocked ? 'var(--w-blocked-200)' : 'var(--w-t400)',
                  minWidth: 0, overflow: 'hidden',
                  // Two lines on a phone, where one is about forty characters and the reason a
                  // ticket is stuck is the thing you opened the board to read.
                  ...(narrow
                    ? { display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' as const }
                    : { whiteSpace: 'nowrap' as const, textOverflow: 'ellipsis' }),
                }}
                title={blocked ? clip(t.blockedReason) : undefined}
              >
                <RowContext t={t} kind={kind} />
              </span>
              {/* No spacer on a phone: the controls wrap under the line, and pushed to the far
                  edge they land left on one card and right on the next, by how long the line was. */}
              {!narrow && <span className="flex-1" />}
              <span className="flex items-center gap-1 flex-none" onClick={e => e.stopPropagation()}>
                <TicketGitActions ticketId={t.id} branch={t.branch ?? t.reviewBranch} size="row" track={t.track} repoType={repoType} merge={mergeInsteadOfPr} />
                {revealMenu && (
                  <span className="relative">
                    <button
                      onClick={onToggleMenu}
                      title="Move ticket"
                      style={{ font: `400 12px ${BODY}`, color: 'var(--w-t500)', padding: '0 6px', lineHeight: 1.2 }}
                    >
                      ⋯
                    </button>
                    <PhaseMenu open={menuOpen} options={rest} onPick={p => onMove(t, p)} />
                  </span>
                )}
              </span>
            </div>
          </div>
        </div>

        {!narrow && <TrackCells station={stationOf(t)} current={blocked ? 'blocked' : trackState(t.phase, kind)} delay={delay} track={t.track} />}

        <div className={`flex items-center gap-1.5 ${narrow ? 'flex-wrap' : 'justify-end'}`} onClick={e => e.stopPropagation()}>
          {variant === 'waiting' ? (
            blocked ? (
              <>
                {/* Resuming where the work stopped is the answer nine times in ten, and it is the
                    one the server can work out (PhaseMachine.ResumeAfterBlock) — so it gets the
                    button and the other legal targets stay in the hover menu. */}
                {t.unblockTo && (
                  <ActionButton
                    tier={tier.c}
                    primary
                    title={`Pick the work back up in ${PHASE_LABEL[t.unblockTo]}`}
                    onClick={() => onMove(t, t.unblockTo!)}
                  >
                    Resume →
                  </ActionButton>
                )}
                <ActionButton tier={tier.c} primary={!t.unblockTo} onClick={onToggleMenu}>
                  {t.unblockTo ? 'Elsewhere ▾' : 'Unblock ▾'}
                </ActionButton>
                <PhaseMenu
                  open={menuOpen}
                  options={t.unblockTo ? nextPhases.filter(p => p !== t.unblockTo) : nextPhases}
                  onPick={p => onMove(t, p)}
                />
              </>
            ) : (
              actions.map((a, i) => (
                <ActionButton key={a.to} tier={tier.c} primary={i === 0} onClick={() => onMove(t, a.to)}>
                  {a.label}
                </ActionButton>
              ))
            )
          ) : (
            <span className={narrow ? 'flex items-baseline' : 'flex flex-col items-end'} style={{ gap: narrow ? 8 : 2 }}>
              <span
                style={{
                  font: `700 10.5px ${MONO}`, color: tier.c, whiteSpace: 'nowrap',
                  textShadow: `0 0 14px ${fade(tier.c, 50)}`,
                }}
              >
                {status}
              </span>
              {/* When it finished, under the tick. Completed is the one list read by date rather
                  than by state — every row in it says the same two words, and without this the
                  only way to tell last week's work from last quarter's is to open each one. */}
              {t.phase === 'Done' && (
                <span
                  title={`Last updated ${new Date(t.updatedAt).toLocaleString()}`}
                  style={{ font: `400 9.5px ${MONO}`, color: 'var(--w-t600)', whiteSpace: 'nowrap' }}
                >
                  {doneOn(t.updatedAt)}
                </span>
              )}
            </span>
          )}
        </div>
      </div>
    </div>
  );
}

/** The targeting bracket that snaps into a corner as the row lights up. */
function Bracket({ tier, corner }: { tier: string; corner: 'tl' | 'br' }) {
  const tl = corner === 'tl';
  return (
    <span
      style={{
        position: 'absolute', width: 12, height: 12, pointerEvents: 'none',
        ...(tl ? { left: 7, top: 5, borderLeft: `1.5px solid ${tier}`, borderTop: `1.5px solid ${tier}` }
               : { right: 7, bottom: 5, borderRight: `1.5px solid ${tier}`, borderBottom: `1.5px solid ${tier}` }),
        opacity: 'var(--rv,0)',
        transform: `translate(calc((1 - var(--rv,0)) * ${tl ? '-4px' : '4px'}),calc((1 - var(--rv,0)) * ${tl ? '-4px' : '4px'}))`,
        transition: 'opacity .18s ease,transform .18s ease',
      }}
    />
  );
}

/**
 * `▲▲ P5 CRITICAL` — the band this ticket is claimed from, drawn only when it is not the default.
 *
 * It earns its place because the board and the claim disagree about order on purpose: the rows are
 * laid out by phase, while the heartbeat empties the highest priority band first. Without this, a
 * ticket picked up ahead of the three above it looks like the heartbeat choosing at random, and the
 * only way to find out otherwise is to open every drawer in the list.
 *
 * Never drawn for Normal. A mark that appeared on every row would be a column, not a mark — and the
 * thing worth seeing here is the exception.
 */
function PriorityMark({ t }: { t: TicketSummary }) {
  const p = t.priority ?? DEFAULT_PRIORITY;
  if (p === DEFAULT_PRIORITY) return null;
  const c = PRIORITY_COLOR[p];
  return (
    <span
      className="flex items-center gap-1 flex-none"
      title={`Priority ${PRIORITY_LEVEL[p]} — ${PRIORITY_HINT[p]}`}
      style={{ font: `700 8.5px ${DISPLAY}`, letterSpacing: track(.12), textTransform: CAPS, color: c, whiteSpace: 'nowrap' }}
    >
      <span style={{ fontSize: 7.5, letterSpacing: 0 }}>{PRIORITY_MARK[p]}</span>
      {p}
    </span>
  );
}

/** `#142 · WHET · WS-88` — everything that identifies a ticket, in one mono breath. */
function Ident({ t, showProjectBadge, keepWhole }: {
  t: TicketSummary; showProjectBadge: boolean;
  /** Refuse to shrink, leaving whatever sits beside this to give up the width instead. */
  keepWhole?: boolean;
}) {
  const bits = [`#${t.id}`];
  if (showProjectBadge) bits.push(t.projectKey.toUpperCase());
  return (
    <span className={`flex items-center gap-1.5 min-w-0 ${keepWhole ? 'flex-none' : ''}`} style={{ font: `600 9px ${MONO}`, color: 'var(--w-t600)', whiteSpace: 'nowrap' }}>
      {/* The local id is the one thing here that is never allowed to go missing — it is how you
          name this ticket to anything else. The borrowed identifiers truncate instead. */}
      <span className="flex-none">{bits.join(' · ')}</span>
      {t.issueKey && <span className="min-w-0 overflow-hidden text-ellipsis" style={{ color: 'var(--w-queue-400)' }}>{t.issueKey}</span>}
      {t.commentCount > 0 && <span className="flex-none" style={{ color: 'var(--w-t700)' }}>💬 {t.commentCount}</span>}
    </span>
  );
}

function ActionButton({ tier, primary, children, onClick, title }: {
  tier: string; primary?: boolean; children: ReactNode; onClick: () => void; title?: string;
}) {
  // A decision made with a thumb needs more to land on than one made with a pointer.
  const narrow = useNarrow();
  return (
    <button
      onClick={onClick}
      title={title}
      className="orbit-btn flex-none"
      style={{
        clipPath: chamfer(9),
        ['--rbg' as string]: primary ? `linear-gradient(120deg,${tier},${fade(tier, 60)})` : 'rgb(var(--w-line-rgb) / .06)',
        ['--rbw' as string]: `1px solid ${primary ? 'transparent' : 'rgb(var(--w-line-rgb) / .18)'}`,
        padding: narrow ? (primary ? '10px 16px' : '9px 15px') : primary ? '6px 12px' : '5px 11px',
        font: `700 ${narrow ? 10.5 : 9.5}px ${DISPLAY}`, letterSpacing: track(.16), textTransform: CAPS,
        color: primary ? 'var(--w-void)' : 'var(--w-t300)', whiteSpace: 'nowrap',
        ['--hbg' as string]: primary ? `linear-gradient(120deg,${tier},${tier})` : 'rgb(var(--w-line-rgb) / .15)',
        ['--htxt' as string]: primary ? 'var(--w-void)' : 'var(--w-t100)',
        ['--hbc' as string]: primary ? 'transparent' : `${fade(tier, 60)}`,
        ['--hsh' as string]: primary ? `inset 0 0 20px -3px ${tier}` : `inset 0 0 16px -5px ${tier}`,
      } as CSSProperties}
    >
      {children}
    </button>
  );
}

/** Hover peek at a blocked reason. Capped — these run to thousands of characters. */
function clip(s: string | null | undefined, max = 300) {
  if (!s) return undefined;
  const flat = s.replace(/\s+/g, ' ').trim();
  return flat.length > max ? `${flat.slice(0, max)}…` : flat;
}

/** The one line that says why this row is where it is. */
function RowContext({ t, kind }: { t: TicketSummary; kind: PhaseKind | undefined }) {
  const hi = { color: 'var(--w-t100)', fontWeight: 600 };
  if (t.phase === 'Blocked') {
    return (
      <>
        {t.phaseBeforeBlock && <>Blocked while <b style={{ color: 'var(--w-blocked-200)' }}>{PHASE_LABEL[t.phaseBeforeBlock].toLowerCase()}</b> — </>}
        {t.blockedReason || 'no reason recorded.'}
      </>
    );
  }
  if (t.phase === 'NeedsApproval') {
    return t.workItemsTotal > 0
      ? <>Spec ready — <b style={hi}>{t.workItemsTotal} work item{t.workItemsTotal === 1 ? '' : 's'}</b> proposed · waiting {ago(t.updatedAt)}</>
      : <>Spec ready for your sign-off · waiting {ago(t.updatedAt)}</>;
  }
  if (t.phase === 'NeedsImplementationReview') {
    return <><b style={hi}>{t.workItemsDone}/{t.workItemsTotal}</b> work items done — code ready for your look before QA · waiting {ago(t.updatedAt)}</>;
  }
  if (t.phase === 'NeedsReview') {
    return (
      <>
        {t.qaTotal > 0 && <b style={{ color: 'var(--w-done-400)' }}>QA {t.qaVerified}/{t.qaTotal} verified · </b>}
        <b style={hi}>{t.workItemsDone}/{t.workItemsTotal}</b> work items done — ready for review · waiting {ago(t.updatedAt)}
      </>
    );
  }
  if (t.waiting && t.waitingOn?.length) {
    return <>Held until {t.waitingOn.join(', ')} lands · {PHASE_LABEL[t.phase]} for {ago(t.updatedAt)}</>;
  }
  if (kind === 'active') {
    return <><b style={hi}>{PHASE_LABEL[t.phase]}</b> · {t.workItemsTotal > 0 ? `${t.workItemsDone}/${t.workItemsTotal} work items · ` : ''}{ago(t.updatedAt)} in phase</>;
  }
  return <>{PHASE_LABEL[t.phase]} · {ago(t.updatedAt)} in phase</>;
}

/**
 * The day a finished ticket finished — `28 Aug 2026`.
 *
 * Read off `updatedAt`, which is the nearest thing the summary carries to a completion stamp and
 * already what Completed sorts and windows on. It is the move to Done in every normal case; a note
 * posted onto a closed ticket afterwards would push it later, which is why the hover says
 * "last updated" rather than claiming more than the field knows.
 */
function doneOn(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? ''
    : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
}

function rowStatus(t: TicketSummary): string {
  // A ticket waiting on another one is never claimed, however near the front of the queue it sits.
  // Saying "next: implement" there would promise a pickup that cannot happen, and the heartbeat
  // would look like it was ignoring the top of its own queue.
  if (t.waiting) {
    const on = t.waitingOn ?? [];
    const shown = on.slice(0, 2).join(', ');
    const more = on.length > 2 ? ` +${on.length - 2}` : '';
    return on.length ? `waiting on ${shown}${more}` : 'waiting';
  }
  // An unanswered owner note holds this ticket's next hand-off. Said on the row rather than left
  // for the drawer, because from out here the ticket is otherwise indistinguishable from one the
  // heartbeat has quietly stopped working — and the person it is waiting on is the one reading this.
  //
  // Only while nothing is running on it. A note posted onto a live run is being dealt with by that
  // run, and the item-by-item progress below is the better thing to be watching in the meantime.
  const live = ['Speculating', 'Implementing', 'QaTesting', 'Refining', 'PeerReviewing'].includes(t.phase);
  if (t.unansweredNotes && !live) {
    return t.unansweredNotes === 1 ? 'your note pending' : `${t.unansweredNotes} notes pending`;
  }
  switch (t.phase) {
    case 'Implementing': {
      if (t.workItemsTotal > 0) {
        const filled = Math.max(0, Math.min(4, Math.round((t.workItemsDone / t.workItemsTotal) * 4)));
        return `${'▓'.repeat(filled)}${'░'.repeat(4 - filled)} ${t.workItemsDone}/${t.workItemsTotal}`;
      }
      return 'implementing';
    }
    case 'QaTesting':
      // A code-review-only ticket sits at 0/0 by design, and "QA testing" over an empty
      // checklist reads as a run that has stalled rather than one doing exactly its job.
      if (t.skipQaTesting) return 'code review';
      return t.qaTotal > 0 ? `QA ${t.qaVerified}/${t.qaTotal}` : 'QA testing';
    case 'PeerReviewing':
      // Same shape as QA above, and the same reason: a review reading only the diff sits at 0/0 by
      // design, and "reviewing" over an empty checklist reads as a run that has stalled.
      if (t.skipQaTesting) return 'reading diff';
      return t.qaTotal > 0 ? `review ${t.qaVerified}/${t.qaTotal}` : 'reviewing';
    case 'NeedsPeerReview': return 'next: review';
    case 'NeedsReview':
      // The one phase both tracks share, and the one place a row has to say which it is. A review
      // ticket here is either waiting on ITS OWNER to send the feedback or on the AUTHOR to answer
      // it — which is the distinction this track needs no extra phase to carry, because the
      // submitted stamp already knows it (see ReviewTarget.SubmittedAt).
      if (t.track !== 'Review') return PHASE_LABEL[t.phase];
      if (t.reviewSubmitted) return 'sent · awaiting dev';
      return t.reviewCommentCount
        ? `${t.reviewCommentCount} staged`
        : 'review done';
    case 'Speculating': return 'speculating';
    case 'Refining': return 'refining';
    case 'NeedsSpeculation': return 'next: spec';
    case 'NeedsImplementation': return 'next: implement';
    case 'NeedsQa': return 'next: QA';
    // Passed over by the heartbeat until its independent analysis lands - say so, or the row
    // reads as a ticket being ignored at the front of the queue.
    case 'NeedsRefinement': return t.awaitingAnalysis ? 'analysis before refine' : 'next: refine';
    case 'Backlog': return 'backlog';
    case 'Done': return '✓ done';
    default: return PHASE_LABEL[t.phase];
  }
}

// ---- Inspect slide-over ----

/**
 * The same slide-over, for a page that is not the board.
 *
 * The heartbeat's ticket column knows a ticket only as an id out of an event, so this fetches the
 * list the board would have had and hands the panel the row it needs. Mounted only while a ticket
 * is open, so a page that never opens one never pays for the fetch.
 */
export function TicketDrawer({ id, state, onClose }: { id: number; state: DashboardState; onClose: () => void }) {
  const { tickets, phases, refreshTickets } = useTickets(null);
  const t = tickets.find(x => x.id === id);
  const agent = state.status === 'running'
    ? state.agents.find(a => a.ticketId != null && Number(a.ticketId.replace(/\D/g, '')) === id)
    : undefined;

  // The fleet can move the open ticket on, and the panel's header reads the phase off this list.
  const beat = `${state.status}|${state.agents.map(a => `${a.slot}:${a.ticketId ?? ''}:${a.phase ?? ''}`).join(',')}`;
  useEffect(() => { refreshTickets(); }, [beat, refreshTickets]);

  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', esc);
    return () => document.removeEventListener('keydown', esc);
  }, [onClose]);

  if (!t) return null;
  return (
    <InspectPanel
      t={t}
      kind={phases.find(p => p.name === t.phase)?.kind}
      agent={agent}
      phases={phases}
      onClose={onClose}
      onChanged={refreshTickets}
    />
  );
}

/**
 * The ticket, opened beside the board rather than inside it.
 *
 * Portalled to the body and fixed to the viewport: the board card clips itself (chamfered corners,
 * a horizontal scroller) and an in-flow panel would be cut off by both. The whole thing wears the
 * ticket's tier — edge, glyph, eyebrow — so the row you clicked and the panel that opened are
 * visibly the same object.
 */
function InspectPanel({ t, kind, agent, phases, onClose, onChanged }: {
  t: TicketSummary;
  kind: PhaseKind | undefined;
  agent?: AgentState;
  phases: PhaseInfo[];
  onClose: () => void;
  onChanged: () => void;
}) {
  const tier = tierToken(t.phase, kind);
  const blocked = t.phase === 'Blocked';
  // Beside the board at a desk; in place of it on a phone, where two thirds of the width is a
  // column too thin to read a plan in.
  const narrow = useNarrow();

  return createPortal(
    <>
      <div
        onClick={onClose}
        style={{
          position: 'fixed', inset: 0, zIndex: 40,
          background: 'rgb(var(--w-void-rgb) / .7)', backdropFilter: 'blur(3px)',
        }}
      />
      <div
        role="dialog"
        aria-label={`Ticket #${t.id}`}
        style={{
          position: 'fixed', top: 0, right: 0, bottom: 0, zIndex: 41,
          width: narrow ? '100vw' : 'min(66vw, 1040px)', display: 'flex', flexDirection: 'column',
          clipPath: narrow ? undefined : chamfer(26),
          background: 'linear-gradient(150deg,rgb(var(--w-panel-lit-rgb) / .96),rgb(var(--w-panel-deep-rgb) / .985))',
          backdropFilter: 'blur(16px)',
          borderLeft: `1px solid ${fade(tier.c, 30)}`,
          // Inset, because the chamfer clips anything painted outside it. The scrim behind the
          // panel was always doing the work of separating it from the page anyway; this is the
          // tier light running down the leading edge, which is the part that was worth keeping.
          boxShadow: `inset 26px 0 46px -34px ${tier.c}`,
          animation: 'orbit-slidein .34s cubic-bezier(.22,.9,.28,1)',
        }}
      >
        <span
          style={{
            position: 'absolute', left: 0, top: 0, bottom: 0, width: 2,
            background: `linear-gradient(180deg,transparent,${tier.c},transparent)`,
          }}
        />
        <div style={{ flex: 'none', padding: narrow ? '12px 14px 0' : '24px 28px 0' }}>
          <div className={`flex items-start ${narrow ? 'gap-3' : 'gap-4'}`}>
            {/* The glyph's halo is a sibling behind it, not a box-shadow on it. A shape cut with
                clip-path cannot cast light past its own edge — the shadow is painted and then
                clipped away — so anything that has to glow outward needs something unclipped to
                glow from. */}
            {!narrow && <div style={{ position: 'relative', width: 64, height: 64, flex: 'none' }}>
              <span
                style={{
                  position: 'absolute', left: -24, top: -24, width: 112, height: 112,
                  background: `radial-gradient(circle,${fade(tier.c, 33)},transparent 68%)`,
                  pointerEvents: 'none',
                }}
              />
              <div
                style={{
                  position: 'absolute', inset: 0, ...HEX_GLYPH,
                  background: `linear-gradient(150deg,${tier.c},${fade(tier.c, 16)})`,
                  boxShadow: `inset 0 0 24px rgba(0,0,0,.65)`,
                }}
              />
            </div>}
            <div className="flex-1 min-w-0" style={{ paddingTop: 2 }}>
              <div style={{ font: `700 10px ${DISPLAY}`, letterSpacing: track(.36), textTransform: CAPS, color: tier.c, textShadow: `0 0 18px ${fade(tier.c, 60)}` }}>
                {PHASE_LABEL[t.phase]}
              </div>
              <div
                style={{ font: `700 ${narrow ? 17 : 26}px/1.15 ${DISPLAY}`, letterSpacing: track(.02), color: 'var(--w-t100)', textTransform: CAPS, marginTop: 5 }}
              >
                {t.title}
              </div>
              <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 8, font: `600 11px ${MONO}`, color: 'var(--w-t500)' }}>
                <span>#{t.id}</span>
                <span style={{ width: 1, height: 12, background: 'rgb(var(--w-line-rgb) / .25)' }} />
                <span>{t.projectKey}</span>
                {t.issueKey && <span style={{ color: 'var(--w-queue-400)' }}>{t.issueKey}</span>}
                <span style={{ width: 1, height: 12, background: 'rgb(var(--w-line-rgb) / .25)' }} />
                <span style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t400)' }}>
                  {blocked ? `stuck for ${ago(t.updatedAt)}` : agent ? `held by agent ${agent.slot} for ${ago(t.updatedAt)}` : `${ago(t.updatedAt)} in phase`}
                </span>
              </div>
            </div>
            <button
              onClick={onClose}
              aria-label="Close"
              className="orbit-btn flex-none flex items-center justify-center"
              style={{
                width: narrow ? 40 : 32, height: narrow ? 40 : 32, clipPath: chamfer(9),
                background: 'rgb(var(--w-line-rgb) / .06)', border: '1px solid rgb(var(--w-line-rgb) / .18)',
                color: 'var(--w-t300)', font: `400 14px ${BODY}`,
                ['--hbg' as string]: 'rgb(var(--w-line-rgb) / .16)',
                ['--htxt' as string]: 'var(--w-t100)',
                ['--hbc' as string]: 'rgb(var(--w-line-rgb) / .4)',
                ['--hsh' as string]: 'none',
              } as CSSProperties}
            >
              ✕
            </button>
          </div>
          <div style={{ height: 1, background: `linear-gradient(90deg,${tier.c},rgb(var(--w-line-rgb) / .08) 60%,transparent)`, marginTop: narrow ? 10 : 18 }} />
        </div>

        <div className="flex-1 min-h-0 overflow-y-auto">
          <TicketDetail id={t.id} phases={phases} agent={agent} onClose={onClose} onChanged={onChanged} flush />
        </div>
      </div>
    </>,
    document.body,
  );
}

// ---- Move menu ----

const MENU_W = 192; // w-48
const MENU_GAP = 4;

/**
 * Move menu for a ticket row. Rendered through a portal in fixed coordinates rather than
 * absolutely inside the row: every row clips itself (chamfered corners) and the board body
 * scrolls horizontally, so an in-flow dropdown gets cut off at the row's edge. Anchored to the
 * button group it replaces, and flipped above when there isn't room below.
 */
function PhaseMenu({ open, options, onPick }: { open: boolean; options: Phase[]; onPick: (p: Phase) => void }) {
  // Rendered whether or not it is open, so that closing is something it can be seen to do. Nothing
  // is in the row while it is shut - `show` is false and this returns null, exactly as the caller
  // used to do with `&&`.
  const { show, fx } = usePop(open);
  const anchor = useRef<HTMLSpanElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number; flip: boolean } | null>(null);

  const place = useCallback(() => {
    const box = anchor.current?.parentElement?.getBoundingClientRect();
    if (!box) return;
    const h = menu.current?.offsetHeight ?? 0;
    const below = box.bottom + MENU_GAP;
    const flip = h > 0 && below + h > window.innerHeight - 8;
    setPos({
      left: Math.max(8, Math.min(box.right - MENU_W, window.innerWidth - MENU_W - 8)),
      top: flip ? Math.max(8, box.top - MENU_GAP - h) : below,
      // Which edge it is hanging off, so it can arrive from there - see `.orbit-pop`.
      flip,
    });
  }, []);

  useLayoutEffect(() => {
    if (!show) return;
    place();
    // The board scrolls horizontally and the page vertically; keep the menu on its anchor.
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [show, place, options.length]);

  if (!show) return null;

  return (
    <>
      <span ref={anchor} className="hidden" />
      {createPortal(
        <div
          ref={menu}
          className="orbit-pop"
          data-fx={fx}
          data-side={pos?.flip ? "above" : undefined}
          onClick={e => e.stopPropagation()}
          style={{
            position: 'fixed', left: pos?.left ?? -9999, top: pos?.top ?? -9999, width: MENU_W, zIndex: 50,
            clipPath: chamfer(10),
            background: 'rgb(var(--w-panel-lit-rgb) / .97)',
            border: '1px solid rgb(var(--w-line-rgb) / .22)',
            boxShadow: '0 18px 44px -18px rgba(0,0,0,.95)',
            padding: '4px 0',
          }}
        >
          {options.map(p => (
            <button
              key={p}
              onClick={() => onPick(p)}
              className="block w-full text-left orbit-btn"
              style={{
                padding: '6px 12px',
                font: `500 11.5px ${BODY}`, color: 'var(--w-t300)',
                ['--hbg' as string]: 'rgb(var(--w-line-rgb) / .1)',
                ['--htxt' as string]: 'var(--w-t100)',
                ['--hbc' as string]: 'transparent',
                ['--hsh' as string]: 'none',
              } as CSSProperties}
            >
              {PHASE_LABEL[p]}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </>
  );
}
