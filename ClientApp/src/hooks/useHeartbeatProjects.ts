import { useState, useEffect, useMemo } from 'react';
import type { ProjectInfo } from '../types/tickets';
import { useHeartbeatAllowSet } from './useHeartbeatAllowSet';

/**
 * Which projects the one global heartbeat is allowed to claim tickets from.
 *
 * Stored in global config as `heartbeatProjects`; the empty-list convention, the last-one-standing
 * rule and the optimistic save all live in {@link useHeartbeatAllowSet}, which the priority-band
 * switch shares. What is left here is the part that is genuinely about projects: the list itself
 * has to be fetched, and it can arrive after the config does.
 *
 * A hook rather than a component because two pages need the same switch — the Heartbeat page, where
 * you go to watch the fleet, and the Board, where you already are when you notice it is working the
 * wrong client's tickets.
 */
export function useHeartbeatProjects(supplied?: ProjectInfo[]) {
  // `undefined` means the caller has no project list of its own and wants one fetched. The Board
  // passes its own (briefly empty while it loads) rather than asking for the same list twice.
  const selfFetch = supplied === undefined;
  const [fetched, setFetched] = useState<ProjectInfo[]>([]);

  const projects = selfFetch ? fetched : supplied!;

  useEffect(() => {
    if (!selfFetch) return;
    let cancelled = false;
    (async () => {
      try {
        const pr = await fetch('/api/projects').then(r => r.json());
        if (!cancelled) setFetched(pr.projects ?? []);
      } catch {
        // Leave it empty; the control hides itself below one project.
      }
    })();
    return () => { cancelled = true; };
  }, [selfFetch]);

  // Rebuilt only when the keys actually change: a fresh array every render would re-derive the
  // selection (and re-make `toggle`) on every parent render for no reason.
  const keys = useMemo(() => projects.map(p => p.key), [projects]);
  const set = useHeartbeatAllowSet({ configKey: 'heartbeatProjects', universe: keys });

  return { projects, ...set };
}
