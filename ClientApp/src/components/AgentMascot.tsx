import { useEffect, useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import type { AgentState, HeartbeatEvent } from '../types/events';
import { hostHold, type WorkerHost } from '../hooks/useWorkerHosts';
import { formatWakeTime } from '../hooks/useTabStatus';
import { GRID, stationColumn, stationIsMid, ACTIVE_DOT_PX } from './JourneyTrack';
import { lastLinesByAgent } from './eventText';
import { chamfer } from './orbit';
import { agentPalette, currentTheme } from '../theme';
import type { MascotPalette, Mood } from '../theme';
import { Bot } from './mascots/Bot';

export type { Mood };

/** The corner every chip on the board is cut with. A call, not a constant: a constant computed at
 *  import time would still be wearing the theme the page was loaded under. */
const chipCut = () => chamfer(8);

// The heartbeat agents, rendered as little bots whose look reflects their status.
// Up to three of them work at once, so a bot also has to say WHICH agent it is.

/**
 * A signature colour per agent slot.
 *
 * The distinction is deliberately narrow. Colour on this board already MEANS something — amber is
 * work in progress, violet is a human gate, red is trouble — so an agent's identity is carried by
 * hue alone, on the parts of the bot that are decoration rather than status: its shell while it is
 * working, its antenna, its beam, and a numeral. A bot's mood still reads the same across all
 * three, which is the point: you should be able to tell "that one is stuck" and "that one is
 * agent 2" independently, without learning a second colour language.
 *
 * Agent 1 keeps the amber the single agent always had, so the common case looks unchanged.
 */
export interface AgentAccent extends MascotPalette {
  text: string;   // tailwind text colour for labels
  chip: string;   // tailwind classes for a small badge
  wash: string;   // translucent tint for the board row this agent is working
}

/**
 * One slot's palette: the six colours a mascot draws itself from, plus the three the rest of the
 * board uses to point at the same agent.
 *
 * The six come from `agentPalette` in the theme module rather than being derived here, because a
 * theme's own mascot needs exactly the same six and deriving them twice is how the bot and a
 * replacement creature would end up disagreeing about what agent 2 looks like.
 *
 * The Tailwind classes are spelled out per slot rather than built from `n`, because Tailwind reads
 * this file as text: a class name assembled at runtime is a class name that was never generated.
 */
const slotAccent = (n: 1 | 2 | 3, text: string, chip: string): AgentAccent => ({
  ...agentPalette(n),
  text,
  chip,
  wash: `rgb(var(--w-agent-${n}-rgb) / .06)`,
});

const ACCENTS: AgentAccent[] = [
  slotAccent(1, 'text-[var(--w-agent-1-300)]', 'bg-[var(--w-agent-1-950)] text-[var(--w-agent-1-300)] border-[var(--w-agent-1-700)]'),
  slotAccent(2, 'text-[var(--w-agent-2-300)]', 'bg-[var(--w-agent-2-950)] text-[var(--w-agent-2-300)] border-[var(--w-agent-2-700)]'),
  slotAccent(3, 'text-[var(--w-agent-3-300)]', 'bg-[var(--w-agent-3-950)] text-[var(--w-agent-3-300)] border-[var(--w-agent-3-700)]'),
];

/** Slot 1..N to its palette. Wraps rather than throwing, so a fourth agent would still render. */
export function agentAccent(slot: number): AgentAccent {
  return ACCENTS[(Math.max(1, slot) - 1) % ACCENTS.length];
}

/**
 * One mascot: the theme's creature, plus the agent numeral that says which agent it is.
 *
 * The split is the whole point of the slot. What the creature *looks like* is the theme's to
 * replace — a bot suits a sector map and would look ridiculous on a manuscript. What the marker
 * *says* is not: the box it occupies, and the number in the corner, are how you tell two working
 * agents apart in a 12px board row and in a screenshot with the colour washed out of it. A theme
 * that could drop those would be a re-skin that removed information, so they stay here.
 *
 * The rule about when a mascot wears its agent's hue also stays here, for the same reason. Only a
 * working agent is tinted: a resting one has no ticket, so what it is doing matters more than which
 * one it is, and tinting every mood would turn six status colours into eighteen.
 */
export function AgentMascot({ mood, size = 40, agent }: { mood: Mood; size?: number; agent?: number }) {
  // Read per render rather than held in a constant, so a theme switched in Settings changes the
  // creature at the same moment it changes everything else. Unset is the common case: the bot is
  // token-driven and already wears whatever palette the theme set.
  const draw = currentTheme().mascot ?? Bot;
  const a = agent != null ? agentAccent(agent) : null;
  const s = size;
  const badge = Math.max(11, s * 0.38);

  return (
    <div style={{ position: 'relative', width: s, height: s, flex: 'none' }}>
      {draw({ mood, size: s, palette: a && mood === 'running' ? a : null })}
      {/* The number, so identity survives colour-blindness and a small screenshot alike. */}
      {a && (
        <span
          style={{
            position: 'absolute', right: -badge * 0.28, bottom: -badge * 0.28,
            width: badge, height: badge, borderRadius: '50%',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            background: 'var(--w-void)', border: `1px solid ${a.edge}`, color: a.hex,
            font: `700 ${Math.max(7, s * 0.24)}px var(--w-font-body)`, lineHeight: 1,
          }}
        >
          {agent}
        </span>
      )}
    </div>
  );
}

const DOCK_LABEL: Record<Mood, string> = {
  running: 'working',
  sleeping: 'resting',
  'off-hours': 'off-hours',
  idle: 'standing by',
  stopped: 'stopped',
  error: 'needs you',
};

const DOCK_TEXT: Record<Mood, string> = {
  running: 'text-amber-300',
  sleeping: 'text-amber-300/90',
  'off-hours': 'text-indigo-300',
  idle: 'text-gray-400',
  stopped: 'text-gray-500',
  error: 'text-red-300',
};

/**
 * A clock that only runs while somebody is out.
 *
 * The berths quote an age ("14s ago"), and an age that is only recomputed when a new event lands
 * is wrong the moment an agent goes quiet — which is exactly when you look at it. One tick a
 * second, mounted only while the fleet is working, and stopped again when it parks.
 */
function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [active]);
  return now;
}

/** How long ago, in the coarsest unit that still says something: 14s ago, 3m ago, 2h ago. */
function sinceLabel(iso: string, now: number): string {
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

/** Narration runs to paragraphs. The berth wants a glimpse, and the title attribute has the rest. */
const LOG_CHARS = 90;

/**
 * The last thing this agent said, quoted under its berth's label.
 *
 * The age is laid out first and never shrinks, so a long line is clipped at its own end rather
 * than pushing the one piece of information that is always readable off the chip.
 */
function BerthLog({ text, at, now }: { text: string; at: string; now: number }) {
  const clipped = text.length > LOG_CHARS ? `${text.slice(0, LOG_CHARS - 1).trimEnd()}…` : text;
  return (
    <span className="berth-log flex items-baseline gap-1.5 max-w-[210px]" title={text}>
      <span className="flex-none text-[8.5px] font-medium tabular-nums text-[var(--w-t600)]">
        {sinceLabel(at, now)}
      </span>
      <span className="min-w-0 truncate text-[9px] font-normal text-[var(--w-t400)]">{clipped}</span>
    </span>
  );
}

/**
 * The fleet's home berths in the "In flight" header — one per configured agent slot.
 *
 * A berth shows one of three things: an empty outline when that agent is out working a ticket you
 * can see on the board below (its bot is hovering there instead), the bot itself with the ticket
 * number when it is working something the current filter hides, or the bot at rest with the
 * fleet's status. With one agent configured this is exactly the single dock it has always been.
 *
 * A berth whose agent is out also quotes that agent's last line from the activity feed, aged. It
 * is the one thing the board could never tell you — the rows say what is being worked and how far
 * along it is, but not what the agent is actually doing this minute, and "is it still moving?" is
 * the question you come to this header with. A parked berth shows nothing: an agent that has
 * stopped has no current line, only an old one, and an old line reads as activity.
 */
export function AgentDock({ status, wakeAt, workers, agents, visible, events = [], hosts = [] }: {
  status: Mood;
  wakeAt?: Date | null;
  /** How many berths to draw. */
  workers: number;
  /** The agents currently out. */
  agents: AgentState[];
  /** Ticket ids visible in the in-flight section, so we know whose bot is already on screen. */
  visible: Set<number>;
  /** The heartbeat buffer, for the last line each working agent said. */
  events?: HeartbeatEvent[];
  /**
   * The board's worker hosts. Each one that is switched on gets its berths after the board host's,
   * behind a tag with its name - and, when it is not taking work, why not.
   */
  hosts?: WorkerHost[];
}) {
  const bySlot = new Map(agents.map(a => [a.slot, a]));
  const slots = Array.from({ length: Math.max(1, workers) }, (_, i) => i + 1);
  const remote = hosts.filter(h => h.enabled);

  // Scanning the buffer is keyed on the buffer and the fleet; the clock ticks on its own, so an
  // ageing label costs a re-render rather than a re-scan of five hundred events every second.
  const lastLines = useMemo(() => lastLinesByAgent(events, agents), [events, agents]);
  const now = useNow(agents.length > 0);

  const renderBerth = (slot: number, host?: WorkerHost) => {
    const a = bySlot.get(slot);
    const ticket = a?.ticketId ? Number(a.ticketId.replace(/\D/g, '')) : null;
    const accent = agentAccent(slot);
    const log = a ? lastLines.get(slot) : undefined;
    const where = host ? ` on ${host.name}` : '';

    // The berth's own colours go through custom properties rather than straight onto the
    // element: an inline `border` or `background` outranks the stylesheet, and a berth whose
    // resting colours are inline is a berth that cannot light up on hover.
    //   --bc  the colour it lights up in — its own agent's, so pointing at one of three tells
    //         you which one you have without reading the numeral
    //   --bw  the border it wears at rest (dashed while its bot is out on the board)
    const berth: CSSProperties = {
      clipPath: chipCut(),
      backdropFilter: 'blur(6px)',
      ['--bc' as string]: accent.hex,
    };

    if (a && ticket != null && visible.has(ticket)) {
      // Out on the board — the berth is empty and says where he went.
      return (
        <div
          key={slot}
          className="orbit-berth flex items-center gap-1.5 pl-2 pr-2.5 py-1 max-sm:max-w-full"
          tabIndex={0}
          title={`Agent ${slot}${where} — out on the board, working #${ticket}`}
          style={{ ...berth, ['--bw' as string]: `1px dashed ${accent.edge}` } as CSSProperties}
        >
          <span
            className="berth-bot w-5 h-5 rounded-[7px] border border-dashed opacity-60 flex-none"
            style={{ borderColor: accent.edge }}
          />
          <span className="flex flex-col items-start gap-px min-w-0">
            <span className={`berth-label text-[9px] font-semibold whitespace-nowrap ${accent.text}`}>
              out — #{ticket}
            </span>
            {log && <BerthLog text={log.text} at={log.at} now={now} />}
          </span>
        </div>
      );
    }

    // Working something this board isn't showing, or resting here with the fleet. A worker host
    // that is not taking work rests stopped whatever the fleet is doing, and says why.
    const hold = host && !a ? hostHold(host) : null;
    const mood: Mood = a ? 'running' : hold ? 'stopped' : status === 'running' ? 'idle' : status;
    let label = a && ticket != null ? `working #${ticket}` : hold ?? DOCK_LABEL[mood];
    if (!a && !hold && mood === 'sleeping' && wakeAt) label = `resting · ${formatWakeTime(wakeAt)}`;

    return (
      <div
        key={slot}
        className="orbit-berth flex items-center gap-2 pl-1.5 pr-2.5 py-1 max-sm:max-w-full"
        tabIndex={0}
        title={`Agent ${slot}${where} — ${a && ticket != null ? `working ticket #${ticket}` : hold ?? DOCK_LABEL[mood]}`}
        style={{ ...berth, ['--bw' as string]: '1px solid rgb(var(--w-line-rgb) / .16)' } as CSSProperties}
      >
        <span className="berth-bot flex flex-none">
          <AgentMascot mood={mood} size={22} agent={slot} />
        </span>
        <span className="flex flex-col items-start gap-px min-w-0">
          <span className={`berth-label text-[9px] font-semibold whitespace-nowrap ${a ? accent.text : DOCK_TEXT[mood]}`}>
            {label}
          </span>
          {log && <BerthLog text={log.text} at={log.at} now={now} />}
        </span>
      </div>
    );
  };

  return (
    <div className="flex items-center gap-1.5 max-sm:flex-wrap max-sm:max-w-full">
      {slots.map(slot => renderBerth(slot))}
      {remote.map(h => (
        <div key={h.name} className="flex items-center gap-1.5 max-sm:flex-wrap max-sm:max-w-full min-w-0">
          <span
            className={`text-[9px] font-semibold uppercase tracking-wider whitespace-nowrap pl-1 ${hostHold(h) ? DOCK_TEXT.stopped : DOCK_TEXT.idle}`}
            title={`Worker host ${h.name}${hostHold(h) ? ` — ${hostHold(h)}` : ''}`}
          >
            {h.name}
          </span>
          {Array.from({ length: Math.max(0, h.slots) }, (_, i) => renderBerth(h.firstSlot + i, h))}
        </div>
      ))}
    </div>
  );
}


/**
 * The agent hovering over the ticket it is working, aimed at that ticket's station. Laid out on
 * the same grid as the track rows and placed in the station's own column, so the beam lands on
 * the dot below it instead of floating over the middle of the row. The px-2 mirrors FlightRow's
 * own padding: without it the two grids resolve different column widths and the drift compounds
 * across columns until the beam misses the dot it is aimed at.
 *
 * Everything about it — pill, bot, beam — carries the agent's colour, because with three of these
 * on screen at once the beam is the only thing tying a bot to the row it belongs to.
 */
export function AgentHover({ phase, station, agent }: { phase?: string; station: number; agent: number }) {
  const a = agentAccent(agent);
  // A station with no column of its own is drawn half a column into the one to its left, so the
  // mascot has to make the same offset or its beam lands on the neighbour's dot instead. 50% of
  // the cell is exactly where that chevron's left edge sits — see TrackCells.
  const mid = stationIsMid(station);
  return (
    <div style={{ ...GRID, paddingLeft: 21, paddingRight: 17 }} className="select-none pointer-events-none">
      <div
        style={{ gridColumn: stationColumn(station) }}
        className="relative flex flex-col items-start pt-1 pb-2"
      >
        {/* Box exactly as wide as the station dot, at the column's left edge where the dot sits.
            Centring the stack on it puts the beam dead on the dot; the wider pill spills evenly. */}
        <div className="flex flex-col items-center" style={{ width: ACTIVE_DOT_PX, marginLeft: mid ? '50%' : 0 }}>
          <span
            className={`text-[8px] font-semibold uppercase tracking-wider px-2 py-0.5 rounded-full whitespace-nowrap mb-1.5 border ${a.chip}`}
          >
            ✎ {phase ? phase.toLowerCase() : 'working'}
          </span>
          <AgentMascot mood="running" size={40} agent={agent} />
          {/* focus beam down onto this station's dot */}
          <div className="relative w-0.5 h-3.5 mt-0.5 overflow-hidden">
            <span className="absolute inset-x-0 top-0 bottom-0" style={{ background: `linear-gradient(180deg,${a.hex},transparent)` }} />
            <span className="anim-agentbeam absolute left-1/2 -translate-x-1/2 w-1.5 h-1.5 rounded-full" style={{ background: a.soft, boxShadow: `0 0 8px ${a.hex}` }} />
          </div>
        </div>
      </div>
    </div>
  );
}

/**
 * The smallest possible "who said this" marker: a coloured dot with the agent's number, for
 * places that are already dense — feed rows, header chips, ticket cards.
 */
export function AgentTag({ agent, className = '' }: { agent: number; className?: string }) {
  const a = agentAccent(agent);
  return (
    <span
      className={`inline-flex items-center justify-center flex-shrink-0 rounded-full border text-[9px] font-bold leading-none ${className}`}
      style={{ width: 14, height: 14, borderColor: a.edge, color: a.hex, background: 'var(--w-void)' }}
      title={`Agent ${agent}`}
    >
      {agent}
    </span>
  );
}
