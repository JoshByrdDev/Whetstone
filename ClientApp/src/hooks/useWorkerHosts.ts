import { useEffect, useState } from 'react';

/** One run a worker host's runner says it is holding. */
export interface WorkerHostRun {
  slot: number;
  ticketId: number;
  runId: number;
  project?: string | null;
  exited: boolean;
}

/**
 * A worker host as `GET /api/hosts` describes it: another machine that runs workers for this board
 * and hands their branches back (docs/worker-hosts-plan.md). Its berths are slots
 * `firstSlot .. firstSlot + slots - 1`, a block of ten per host after the board host's (11.., 21..).
 */
export interface WorkerHost {
  name: string;
  enabled: boolean;
  slots: number;
  firstSlot: number;
  online: boolean;
  lastCheckin?: string | null;
  commitMatches: boolean;
  usageLimitUntil?: string | null;
  dispatchable: boolean;
  runs: WorkerHostRun[];
}

/** What a host's berths say when it is not taking work, or null when it is. */
export function hostHold(h: WorkerHost): string | null {
  if (!h.enabled) return 'switched off';
  if (!h.online) return 'offline';
  if (!h.commitMatches) return 'on another commit';
  if (h.usageLimitUntil) return 'at its usage limit';
  return null;
}

/**
 * The board's worker hosts, polled. Their state changes on the scale of a check-in - online,
 * offline, a pause - while what their agents are doing already arrives through the event stream
 * like any other agent's, so a slow poll is all this needs. An error leaves the last answer in
 * place: a hiccup is not a host going away.
 */
export function useWorkerHosts(intervalMs = 15000): WorkerHost[] {
  const [hosts, setHosts] = useState<WorkerHost[]>([]);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const r = await fetch('/api/hosts');
        if (!r.ok) return;
        const body = await r.json();
        if (!cancelled && Array.isArray(body)) setHosts(body);
      } catch {
        // Keep what we had.
      }
    };
    load();
    const id = window.setInterval(load, intervalMs);
    return () => { cancelled = true; window.clearInterval(id); };
  }, [intervalMs]);

  return hosts;
}
