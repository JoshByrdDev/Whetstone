import { useState, useEffect, useCallback } from 'react';
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

  const refreshTickets = useCallback(async () => {
    try {
      // In parallel, and the labels are allowed to fail on their own: chips missing from a row is a
      // cosmetic loss, and letting it take the board down with it would not be.
      const [rows, index] = await Promise.all([
        getTickets(projectFilter ?? undefined),
        getLabels().catch(() => null),
      ]);
      setTickets(rows);
      if (index) setLabels(index.assignments);
      setError(null);
    } catch (e) {
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
