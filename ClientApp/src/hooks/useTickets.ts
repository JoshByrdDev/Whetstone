import { useState, useEffect, useCallback, useRef } from 'react';
import type { LabelRef, PhaseInfo, ProjectInfo, TicketSummary } from '../types/tickets';
import { getPhases, getProjects, getTickets } from '../api/tickets';
import { getLabels } from '../api/labels';

export function useTickets(projectFilter: string | null) {
  const [tickets, setTickets] = useState<TicketSummary[]>([]);
  const [phases, setPhases] = useState<PhaseInfo[]>([]);
  const [projects, setProjects] = useState<ProjectInfo[]>([]);
  // Keyed by ticket id; a ticket wearing nothing is simply absent. A second request rather than a
  // field on the ticket, because the ticket shape is also what every worker receives over MCP and
  // labels are the one thing on this board the pipeline is never shown (see api/labels.ts).
  const [labels, setLabels] = useState<Record<string, LabelRef[]>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Several refreshes overlap: the board polls while the fleet runs, refetches on every agent
  // change, and refetches after each of your own moves. A request can finish after one started
  // later - the list waits on the tracker when its title cache has expired - and saving it would
  // put back a board from before your click. A ticket moved to Done would read as never having
  // moved, so its completion never played. Each request is numbered, and an answer older than the
  // one already shown is dropped.
  const started = useRef(0);
  const shown = useRef(0);

  const refreshTickets = useCallback(async () => {
    const mine = ++started.current;
    try {
      // In parallel, and the labels are allowed to fail on their own: chips missing from a row is a
      // cosmetic loss, and letting it take the board down with it would not be.
      const [rows, index] = await Promise.all([
        getTickets(projectFilter ?? undefined),
        getLabels().catch(() => null),
      ]);
      if (mine < shown.current) return;
      shown.current = mine;
      setTickets(rows);
      if (index) setLabels(index.assignments);
      setError(null);
    } catch (e) {
      if (mine < shown.current) return;
      setError(e instanceof Error ? e.message : 'Failed to load tickets');
    }
  }, [projectFilter]);

  // Load static metadata once.
  useEffect(() => {
    let active = true;
    Promise.all([getPhases(), getProjects()])
      .then(([ph, pr]) => {
        if (!active) return;
        setPhases(ph);
        setProjects(pr.projects);
      })
      .catch(e => active && setError(e instanceof Error ? e.message : 'Failed to load board metadata'));
    return () => { active = false; };
  }, []);

  // (Re)load tickets when the filter changes.
  useEffect(() => {
    setLoading(true);
    refreshTickets().finally(() => setLoading(false));
  }, [refreshTickets]);

  return { tickets, phases, projects, labels, loading, error, refreshTickets };
}
