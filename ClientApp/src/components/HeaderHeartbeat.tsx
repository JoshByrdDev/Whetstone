import { useState, useEffect } from 'react';
import type { DashboardState } from '../types/events';
import { formatWakeTime } from '../hooks/useTabStatus';
import { agentAccent } from './AgentMascot';
import { StewardTag } from './StewardMark';

const statusMeta: Record<string, { label: string; dot: string; text: string; pulse: boolean }> = {
  running:    { label: 'Running',   dot: 'bg-emerald-400', text: 'text-emerald-300', pulse: true },
  sleeping:   { label: 'Sleeping',  dot: 'bg-amber-400',   text: 'text-amber-300',   pulse: false },
  idle:       { label: 'Idle',      dot: 'bg-gray-500',    text: 'text-gray-400',    pulse: false },
  stopped:    { label: 'Stopped',   dot: 'bg-gray-600',    text: 'text-gray-500',    pulse: false },
  error:      { label: 'Error',     dot: 'bg-red-400',     text: 'text-red-300',     pulse: true },
  'off-hours':{ label: 'Off-Hours', dot: 'bg-indigo-400',  text: 'text-indigo-300',  pulse: false },
};

/**
 * Compact fleet glance for the app bar — status pill plus one chip per working agent, in that
 * agent's colour. Shows the linked issue key when the ticket has one, else the (truncated) local
 * title. Visible on every tab.
 *
 * `compact` is the phone's bar: the pill and each agent's numbered dot, without the ticket names.
 * Three truncated titles are wider than the screen they would be read on, and the board directly
 * underneath says which ticket each agent is holding.
 */
export function HeaderHeartbeat({ state, wakeAt, compact }: { state: DashboardState; wakeAt?: Date | null; compact?: boolean }) {
  const [titles, setTitles] = useState<Record<string, { title: string; issueKey?: string | null }>>({});

  // Every ticket the fleet is on, as a stable key so this refetches when the set changes rather
  // than on every event.
  const ids = state.agents
    .map(a => (a.ticketId ? a.ticketId.replace(/\D/g, '') : ''))
    .filter(Boolean);
  const idsKey = Array.from(new Set(ids)).sort().join(',');

  useEffect(() => {
    if (!idsKey) { setTitles({}); return; }
    let active = true;
    Promise.all(
      idsKey.split(',').map(id =>
        fetch(`/api/tickets/${id}`)
          .then(r => (r.ok ? r.json() : null))
          .then(d => [id, d] as const)
          .catch(() => [id, null] as const),
      ),
    ).then(pairs => {
      if (!active) return;
      const next: Record<string, { title: string; issueKey?: string | null }> = {};
      for (const [id, d] of pairs) if (d) next[id] = { title: d.title, issueKey: d.issueKey };
      setTitles(next);
    });
    return () => { active = false; };
  }, [idsKey]);

  const meta = statusMeta[state.status] ?? statusMeta.idle;
  // While running, the count of working agents is the useful signal - "Running" alone says nothing
  // about whether one bot is out or all three.
  const working = state.agents.length;
  const statusLabel = state.status === 'running' && working > 0
    ? `${working} working`
    : meta.label;

  return (
    <div className="flex items-center gap-2 text-xs">
      <span className={`inline-flex items-center gap-1.5 font-medium whitespace-nowrap ${meta.text}`}>
        <span className="relative flex h-2 w-2">
          {meta.pulse && (
            <span className={`animate-ping absolute inline-flex h-full w-full rounded-full ${meta.dot} opacity-75`} />
          )}
          <span className={`relative inline-flex h-2 w-2 rounded-full ${meta.dot}`} />
        </span>
        {statusLabel}
      </span>
      {state.status === 'sleeping' && wakeAt && (
        <>
          <span className="text-gray-700">·</span>
          <span className="text-amber-300/80 whitespace-nowrap">wakes {formatWakeTime(wakeAt)}</span>
        </>
      )}
      {state.agents.map(a => {
        const id = a.ticketId ? a.ticketId.replace(/\D/g, '') : '';
        const t = titles[id];
        const accent = agentAccent(a.slot);
        const label = t ? (t.issueKey?.trim() || t.title) : id ? `#${id}` : null;
        if (!label) return null;
        const isLinked = !!(t && t.issueKey?.trim());
        return (
          <span key={a.slot} className="flex items-center gap-1">
            {!compact && <span className="text-gray-700">·</span>}
            <span
              className="inline-flex items-center justify-center flex-shrink-0 rounded-full border text-[9px] font-bold leading-none"
              style={{ width: 14, height: 14, borderColor: accent.edge, color: accent.hex, background: 'var(--w-void)' }}
              title={compact ? `Agent ${a.slot} - ${t?.title ?? label}` : `Agent ${a.slot}`}
            >
              {a.slot}
            </span>
            {!compact && <span
              className={`max-w-[150px] truncate ${isLinked ? 'font-mono text-blue-300' : 'text-gray-300'}`}
              title={t?.title ?? label}
            >
              {label}
            </span>}
          </span>
        );
      })}
      {/* Out beside the agents, and said in a word: it has no number and no ticket to show. */}
      {state.steward && (
        <span className="flex items-center gap-1" title={`A steward's run is going${state.steward.project ? ` for ${state.steward.project}` : ''}`}>
          <span className="text-gray-700">·</span>
          <StewardTag />
        </span>
      )}
    </div>
  );
}
