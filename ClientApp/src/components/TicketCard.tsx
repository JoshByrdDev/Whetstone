import { useState, useEffect, useMemo } from 'react';
import type { HeartbeatEvent } from '../types/events';
import { AgentTag } from './AgentMascot';

interface TicketEvent {
  id: string;
  ticketId: string;
  phase: string;
  project?: string;
  /** Which agent worked it — the cards are otherwise indistinguishable when three run at once. */
  agent?: number;
  action: 'start' | 'finish' | 'skip';
  timestamp: string;
  reason?: string;
}

interface Props {
  events: HeartbeatEvent[];
  onTicketClick?: (ticketId: string) => void;
  /** Agent slots the column is narrowed to, set from the status banner. Empty means the fleet. */
  agentFilter?: ReadonlySet<number>;
}

/* Each card keeps its resting skin and names the one it wears under the pointer. The hover is the
   same gesture the classroom's ticket list and the board's rows make — the hairline lights, the
   ground comes up a step — rather than a blanket brightness filter, which washed the green cards
   out and barely touched the red ones. */
const actionStyles: Record<string, { bg: string; hover: string; label: string }> = {
  start:  { bg: 'bg-green-600/20 border-green-500/40', hover: 'hover:bg-green-600/30 hover:border-green-400/70', label: 'Start' },
  finish: { bg: 'bg-green-900/30 border-green-700/40', hover: 'hover:bg-green-900/50 hover:border-green-600/70', label: 'Finish' },
  skip:   { bg: 'bg-red-900/30 border-red-500/40',     hover: 'hover:bg-red-900/50 hover:border-red-400/70',     label: 'Skip' },
};

export function TicketCards({ events, onTicketClick, agentFilter }: Props) {
  const filtering = (agentFilter?.size ?? 0) > 0;
  // Filtered after the pairing pass, not before: a cycle_end or an error can arrive without the
  // agent stamped on it, and dropping those first would leave a finished ticket showing as still
  // running. The card carries the agent that opened it, which is the one the chip named.
  const all = buildTicketEvents(events);
  const ticketEvents = filtering
    ? all.filter(t => t.agent != null && agentFilter!.has(t.agent))
    : all;

  // Events carry only the local ticket id; resolve each to its issue key (if linked) via /api/tickets
  // so we can show the issue id alongside the local one. Refetched only when the ticket set changes.
  const [issueKeyById, setIssueKeyById] = useState<Record<string, string>>({});
  // Every ticket id that still exists, from the same fetch. The feed remembers tickets that have
  // since been deleted, and their cards have no drawer to open. Null until it lands, so nothing
  // reads as deleted just because the list has not come back yet.
  const [known, setKnown] = useState<ReadonlySet<string> | null>(null);
  const idsKey = useMemo(
    () => Array.from(new Set(ticketEvents.map(t => t.ticketId))).sort().join(','),
    [ticketEvents],
  );
  useEffect(() => {
    if (!idsKey) return;
    let active = true;
    fetch('/api/tickets')
      .then(r => (r.ok ? r.json() : []))
      .then((tickets: Array<{ id: number; issueKey?: string | null }>) => {
        if (!active) return;
        const m: Record<string, string> = {};
        for (const t of tickets) if (t.issueKey) m[String(t.id)] = t.issueKey;
        setIssueKeyById(m);
        setKnown(new Set(tickets.map(t => String(t.id))));
      })
      .catch(() => {});
    return () => { active = false; };
  }, [idsKey]);

  if (ticketEvents.length === 0) {
    return (
      <div className="text-gray-500 text-center py-8 px-3 text-sm leading-relaxed">
        {filtering
          ? 'No tickets from the selected agents yet. Press Esc for the whole fleet.'
          : 'No tickets processed yet this session.'}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {ticketEvents.map(t => {
        const style = actionStyles[t.action];
        const numId = t.ticketId.replace(/\D/g, '');
        const issueKey = issueKeyById[numId];
        const gone = known != null && !known.has(numId);
        return (
          <button
            key={t.id}
            type="button"
            disabled={gone}
            onClick={() => onTicketClick?.(t.ticketId)}
            title={gone ? `${t.ticketId} has since been deleted` : `Open ${t.ticketId}`}
            /* `orbit-pick` nudges it right, toward the drawer the click slides in from that edge,
               and stands still for a disabled card. */
            className={`orbit-pick group block w-full text-left rounded-lg px-3 py-2.5 border
              focus:outline-none focus-visible:ring-2 focus-visible:ring-white/40
              ${style.bg} ${gone ? 'opacity-50 cursor-default' : `cursor-pointer hover:ring-1 hover:ring-white/10 ${style.hover}`}`}
          >
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2 min-w-0">
                {t.agent != null && <AgentTag agent={t.agent} />}
                <span className="font-mono font-bold text-sm text-gray-200 group-hover:text-white transition-colors">
                  {t.ticketId}
                </span>
                {issueKey && (
                  <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-blue-900/60 text-blue-300 truncate">
                    {issueKey}
                  </span>
                )}
                {t.project && (
                  <span className="text-[10px] font-medium px-1.5 py-0.5 rounded bg-black/30 text-gray-300 truncate">
                    {t.project}
                  </span>
                )}
              </div>
              <span className={`text-xs font-semibold ${
                t.action === 'skip' ? 'text-red-400' :
                t.action === 'start' ? 'text-green-400' : 'text-green-600'
              }`}>
                {style.label}
              </span>
            </div>
            <div className="flex items-center justify-between mt-1">
              <span className="text-xs text-gray-400 group-hover:text-gray-200 font-semibold transition-colors">
                {t.phase}
              </span>
              <span className="text-xs text-gray-500">
                {new Date(t.timestamp).toLocaleTimeString()}
              </span>
            </div>
            {t.reason && (
              <div className="text-xs text-gray-500 group-hover:text-gray-400 mt-1 truncate transition-colors">
                {t.reason}
              </div>
            )}
          </button>
        );
      })}
    </div>
  );
}

// Each agent runs one ticket: a cycle_start (with ticketId/phase/project) opens it, a matching
// cycle_end marks it finished, an error marks it failed. Pairing by ticket rather than by agent
// stays correct with a fleet, because the claim leases a ticket to one agent at a time.
function buildTicketEvents(events: HeartbeatEvent[]): TicketEvent[] {
  const result: TicketEvent[] = [];

  const findOpen = (ticketId: string) =>
    [...result].reverse().find(r => r.ticketId === ticketId);

  for (const evt of events) {
    if (!evt.ticketId) continue;

    if (evt.type === 'cycle_start') {
      result.push({
        id: evt.id,
        ticketId: evt.ticketId,
        phase: evt.phase ?? '',
        project: evt.project,
        agent: evt.agent,
        action: 'start',
        timestamp: evt.timestamp,
      });
    } else if (evt.type === 'cycle_end') {
      const open = findOpen(evt.ticketId);
      if (open && open.action === 'start') open.action = 'finish';
    } else if (evt.type === 'error') {
      const open = findOpen(evt.ticketId);
      if (open) { open.action = 'skip'; open.reason = evt.error ?? evt.text; }
    }
  }

  return result.reverse();
}
