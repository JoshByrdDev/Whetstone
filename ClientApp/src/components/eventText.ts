import type { HeartbeatEvent } from '../types/events';

// One place that turns an event into words. The activity feed renders each kind in its own row
// shape, but the words themselves are the same words the berths on the board show, and two copies
// of that mapping is two things to keep in step.

/** Compact display name for a tool: drop the mcp__tickets__ noise, keep the verb. */
export function prettyTool(name?: string): string {
  if (!name) return 'tool';
  if (name.startsWith('mcp__tickets__')) return name.slice('mcp__tickets__'.length);
  if (name.startsWith('mcp__')) return name.split('__').slice(-1)[0];
  return name;
}

/** The feed's phrasing for the event kinds that are rendered as a single action line. */
export function formatEventText(evt: HeartbeatEvent): string {
  switch (evt.type) {
    case 'cycle_start':
      return evt.ticketId
        ? `Working ${evt.ticketId}${evt.phase ? ` (${evt.phase})` : ''}`
        : 'Heartbeat cycle started';
    case 'cycle_end':
      return evt.ticketId ? `Finished ${evt.ticketId}` : 'Heartbeat cycle complete';
    case 'ticket_start':
      return `Working on ${evt.ticketId ?? 'ticket'}`;
    case 'tool_call':
      return evt.toolName ?? 'Unknown tool';
    case 'sleep':
      return evt.durationSeconds
        ? `Sleeping for ${Math.ceil(evt.durationSeconds / 60)}m`
        : 'Going to sleep';
    case 'error':
      return evt.error ?? evt.text ?? 'Error occurred';
    case 'summary':
      return evt.text ?? 'Cycle summary';
    case 'phase_detected':
      return `${evt.phase ?? 'Phase'} on ${evt.ticketId ?? 'ticket'}`;
    case 'state_change':
      return evt.text ?? 'State changed';
    case 'skill_start':
      return `${evt.ticketId ?? 'ticket'} ${evt.phase ?? 'Skill'} Start`;
    case 'skill_end':
      return `${evt.ticketId ?? 'ticket'} ${evt.phase ?? 'Skill'} Finish`;
    case 'ticket_skip':
      return `${evt.ticketId ?? 'ticket'} Skip — ${evt.text ?? ''}`;
    default:
      return evt.text ?? evt.type;
  }
}

/**
 * The whole of what one event says, on one line — narration and thinking carry their own text, a
 * tool call is its verb plus what it acted on, and everything else is the action phrasing above.
 * Whitespace is collapsed: narration arrives with the line breaks Claude wrote, and a berth is one
 * line tall.
 */
export function eventLine(evt: HeartbeatEvent): string {
  let s: string;
  switch (evt.type) {
    case 'text_output':
    case 'thinking':
      s = evt.text ?? '';
      break;
    case 'tool_call':
      s = [prettyTool(evt.toolName), evt.text].filter(Boolean).join(' ');
      break;
    default:
      s = formatEventText(evt);
  }
  return s.replace(/\s+/g, ' ').trim();
}

/** The kinds the activity feed hides — internal plumbing, and a restatement of the narration. */
export const FEED_HIDDEN = new Set(['state_change', 'summary']);

/** A line that is only the opening or closing of a code block says nothing on its own. */
const FENCE = /^`{3,}\w*$/;

/**
 * The last thing each agent said, as the heartbeat page would have shown it.
 *
 * Scanned backwards so a long buffer costs only as much as it takes to find one line per agent,
 * and floored at the moment each agent claimed its current ticket: a slot that has just started
 * should say nothing rather than repeat the cycle it finished ten minutes ago.
 */
export function lastLinesByAgent(
  events: HeartbeatEvent[],
  slots: { slot: number; since?: string }[],
): Map<number, { text: string; at: string }> {
  const found = new Map<number, { text: string; at: string }>();
  if (slots.length === 0) return found;

  const floor = new Map(slots.map(s => [s.slot, s.since ? new Date(s.since).getTime() : 0]));
  // A slot lands here once its answer is settled, whether that answer is a line or nothing.
  const done = new Set<number>();

  for (let i = events.length - 1; i >= 0 && done.size < slots.length; i--) {
    const e = events[i];
    if (e.agent == null || done.has(e.agent) || !floor.has(e.agent)) continue;
    if (FEED_HIDDEN.has(e.type)) continue;
    // A cycle's own start and end bracket the work rather than narrate it, and "Working #12" next
    // to a berth that already says "working #12" is a wasted line.
    if (e.type === 'cycle_start' || e.type === 'cycle_end') continue;
    // Events run oldest-first, so anything before this agent's claim belongs to an earlier cycle —
    // and so does everything before it. Stop looking for that agent.
    if (new Date(e.timestamp).getTime() < (floor.get(e.agent) ?? 0)) { done.add(e.agent); continue; }
    const text = eventLine(e);
    if (!text || FENCE.test(text)) continue;
    found.set(e.agent, { text, at: e.timestamp });
    done.add(e.agent);
  }

  return found;
}
