import type { DashboardState, HeartbeatEvent } from '../types/events';
import { AgentMascot } from './AgentMascot';
import { StewardTag } from './StewardMark';

interface Props {
  state: DashboardState;
  isConnected: boolean;
  events: HeartbeatEvent[];
  /** Agent slots the page below is narrowed to. Empty means the whole fleet. */
  agentFilter?: ReadonlySet<number>;
  onToggleAgent?: (slot: number) => void;
  onClearAgents?: () => void;
}

const statusConfig: Record<string, { bg: string; label: string; pulse: boolean }> = {
  running:    { bg: 'bg-emerald-600', label: 'Running',    pulse: true },
  sleeping:   { bg: 'bg-amber-600',   label: 'Sleeping',   pulse: false },
  idle:       { bg: 'bg-gray-600',    label: 'Idle',       pulse: false },
  stopped:    { bg: 'bg-gray-700',    label: 'Stopped',    pulse: false },
  error:      { bg: 'bg-red-600',     label: 'Error',      pulse: true },
  'off-hours': { bg: 'bg-indigo-600', label: 'Off-Hours',  pulse: false },
};

export function StatusBanner({
  state, isConnected, events,
  agentFilter, onToggleAgent, onClearAgents,
}: Props) {
  const config = statusConfig[state.status] ?? statusConfig.idle;
  const timeAgo = state.lastEventTime
    ? formatTimeAgo(new Date(state.lastEventTime))
    : 'never';

  const wakeUpTime = getWakeUpTime(state, events);
  // While running, say how much of the fleet is out. One phase name was the right headline when
  // there was only ever one agent; with three it would describe a third of what is happening.
  const working = state.agents.length;
  const filtering = (agentFilter?.size ?? 0) > 0;
  const label = state.status === 'running' && working > 0
    ? `${working} of ${state.workers} working`
    : config.label;
  // The steward is out beside the agents, never counted among them, and its chip filters the page
  // the same way theirs do - by the slot its lines arrive on.
  const steward = state.steward ?? null;
  const stewardOn = steward != null && !!agentFilter?.has(steward.slot);
  const picked = [...(agentFilter ?? [])].sort((x, y) => x - y);
  const pickedAgents = picked.filter(s => s !== steward?.slot);
  const pickedLabel = [
    pickedAgents.length > 0 ? `Agent ${pickedAgents.join(' · ')}` : null,
    stewardOn ? 'Steward' : null,
  ].filter(Boolean).join(' · ');

  return (
    <div className={`${config.bg} rounded-lg px-6 py-4 flex flex-wrap items-center justify-between gap-y-3`}>
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative flex h-3 w-3">
          {config.pulse && (
            <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-white opacity-75" />
          )}
          <span className="relative inline-flex rounded-full h-3 w-3 bg-white" />
        </div>
        <span className="text-lg font-semibold">{label}</span>
        {wakeUpTime && (
          <span className="ml-1 bg-white/20 rounded px-2 py-0.5 text-sm">
            Wake up at {wakeUpTime}
          </span>
        )}
        {working === 0 && !steward && state.project && (
          <span className="ml-2 bg-white/25 rounded px-2 py-0.5 text-sm font-medium">
            {state.project}
          </span>
        )}
        {/* One pill per working agent, each carrying its own bot so the banner answers "who is on
            what" rather than just "something is happening" — and each a switch that narrows the
            feed and the ticket column below to that agent's strand. Multi-select, because the
            question is often "these two" rather than "this one": pick a second and both stay lit.
            Picking every agent out is the same picture as picking none, so the panels below read
            an empty set as the whole fleet and the chips settle back to resting. */}
        {state.agents.map(a => {
          const on = !!agentFilter?.has(a.slot);
          const dim = filtering && !on;
          return (
            <button
              key={a.slot}
              type="button"
              onClick={() => onToggleAgent?.(a.slot)}
              aria-pressed={on}
              title={on
                ? `Showing only agent ${a.slot} — click to drop it from the filter`
                : `Show only agent ${a.slot}${filtering ? ' as well' : ''}`}
              /* Lifts a pixel toward the pointer and lights its ring rather than growing: the
                 banner is one horizontal line of pills, and a chip that swells shoves its
                 neighbours along the row. A picked chip is already lit, so it does not move. */
              className={`flex items-center gap-2 rounded-full pl-1.5 pr-3 py-1 border transition-all duration-150
                motion-reduce:transition-none focus:outline-none focus-visible:ring-2 focus-visible:ring-white/80 ${
                on
                  ? 'bg-black/45 border-white/70 shadow-[0_0_0_1px_rgb(255_255_255/.25)]'
                  : 'bg-black/25 border-transparent hover:bg-black/40 hover:border-white/40 hover:-translate-y-px active:translate-y-0 motion-reduce:hover:translate-y-0'
              } ${dim ? 'opacity-45 saturate-50 hover:opacity-90 hover:saturate-100' : ''}`}
            >
              <AgentMascot mood="running" size={20} agent={a.slot} />
              <span className="text-sm">
                {a.project && <span className="opacity-80 mr-1.5">{a.project}</span>}
                <span className="font-mono">#{a.ticketId?.replace(/\D/g, '') ?? '?'}</span>
                {a.phase && <span className="opacity-80 ml-1.5">{a.phase}</span>}
              </span>
            </button>
          );
        })}
        {/* Not a fourth pill. An agent's is round and carries its bot and its ticket; the steward
            has neither, so it is a squared chip that says what it is in a word. */}
        {steward && (
          <button
            type="button"
            onClick={() => onToggleAgent?.(steward.slot)}
            aria-pressed={stewardOn}
            title={stewardOn
              ? 'Showing only the steward — click to drop it from the filter'
              : `Show only the steward${filtering ? ' as well' : ''}`}
            className={`orbit-tap flex items-center gap-2 rounded px-2 py-1 border text-sm
              focus:outline-none focus-visible:ring-2 focus-visible:ring-white/80 ${
              stewardOn ? 'bg-black/55 border-white/70' : 'bg-black/40 border-white/25 hover:border-white/50'
            } ${filtering && !stewardOn ? 'opacity-45 saturate-50 hover:opacity-90 hover:saturate-100' : ''}`}
          >
            <StewardTag />
            {steward.project && <span className="opacity-80">{steward.project}</span>}
          </button>
        )}
        {filtering && (
          <button
            type="button"
            onClick={onClearAgents}
            title="Show the whole fleet again (Esc)"
            className="flex items-center gap-1.5 rounded-full border border-white/40 bg-white/10 px-2.5 py-1 text-xs
              transition-all duration-150 hover:bg-white/20 hover:border-white/70 motion-reduce:transition-none
              focus:outline-none focus-visible:ring-2 focus-visible:ring-white/80"
          >
            <span className="font-semibold">
              {pickedLabel} only
            </span>
            <span aria-hidden className="opacity-70 leading-none">✕</span>
          </button>
        )}
      </div>
      <div className="flex items-center gap-4 text-sm opacity-80">
        <span>Last event: {timeAgo}</span>
        <span className="flex items-center gap-1">
          <span className={`inline-block h-2 w-2 rounded-full ${isConnected ? 'bg-green-300' : 'bg-red-400'}`} />
          {isConnected ? 'Connected' : 'Disconnected'}
        </span>
      </div>
    </div>
  );
}

function getWakeUpTime(state: DashboardState, events: HeartbeatEvent[]): string | null {
  if (state.status !== 'sleeping' && state.status !== 'off-hours') return null;

  // Find the most recent sleep event
  for (let i = events.length - 1; i >= 0; i--) {
    const evt = events[i];
    if (evt.type === 'sleep' && evt.durationSeconds) {
      const sleepStart = new Date(evt.timestamp);
      const wakeUp = new Date(sleepStart.getTime() + evt.durationSeconds * 1000);
      return wakeUp.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    }
  }
  return null;
}

function formatTimeAgo(date: Date): string {
  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ago`;
}
