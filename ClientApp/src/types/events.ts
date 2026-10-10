export interface HeartbeatEvent {
  id: string;
  timestamp: string;
  type: EventType;
  /** Project this event belongs to. Global events (sleep/idle/off-hours) have none. */
  project?: string;
  /**
   * Which agent said this (1..N). Absent on the orchestrator's own events — sleeping, going idle —
   * which belong to the fleet rather than to any one agent. With three agents narrating at once
   * this is what separates the strands.
   */
  agent?: number;
  ticketId?: string;
  phase?: string;
  toolName?: string;
  text?: string;
  durationSeconds?: number;
  error?: string;
  meta?: Record<string, unknown>;
}

export type EventType =
  | 'cycle_start'
  | 'cycle_end'
  | 'ticket_start'
  | 'tool_call'
  | 'text_output'
  | 'thinking'
  | 'sleep'
  | 'error'
  | 'summary'
  | 'phase_detected'
  | 'state_change'
  | 'skill_start'
  | 'skill_end'
  | 'ticket_skip';

/** One agent slot and what it is working right now. Only agents actually out are reported. */
export interface AgentState {
  slot: number;
  status: string;
  ticketId?: string;
  phase?: string;
  project?: string;
  since?: string;
}

export interface DashboardState {
  status: 'idle' | 'running' | 'sleeping' | 'error' | 'off-hours' | 'stopped';
  /**
   * The lead agent's ticket. Kept for the compact surfaces that only ever showed one thing;
   * anything that wants the whole picture reads `agents`.
   */
  ticketId?: string;
  phase?: string;
  project?: string;
  lastEventTime?: string;
  totalEvents: number;
  /** How many agent slots the fleet is configured to run — the number of berths to draw. */
  workers: number;
  /** The agents currently out, lowest slot first. Empty when the fleet is idle or stopped. */
  agents: AgentState[];
}
