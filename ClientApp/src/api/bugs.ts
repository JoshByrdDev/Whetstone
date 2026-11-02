/**
 * Bug tracing (docs/bugs-and-experiments-plan.md, section 1): where a bug ticket's fix came from,
 * and the owner's verdicts. Every write here is a route no MCP tool reaches - only a person
 * confirms or rejects a link.
 */

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.error) msg = body.error;
    } catch { /* non-JSON error body */ }
    throw new Error(msg);
  }
  return res.json() as Promise<T>;
}

const post = (url: string, body?: unknown) =>
  fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

export interface LinkCommit { repo: string; sha: string; subject: string; lines: number; files: string[] }

export interface RunGuess {
  runId: number; skill: string; pass: number; startedAt: string; model: string | null; skillHash: string | null;
  /** exact: the commit is inside the run's range; likely: the run's commits touched a blamed file; phase: a run that committed. */
  tier: 'exact' | 'likely' | 'phase';
}

export interface BugLinkView {
  id: number; sourceTicketId: number | null; sourceTitle: string | null;
  status: 'Suggested' | 'Confirmed' | 'Rejected'; origin: 'Blame' | 'Manual';
  lines: number; share: number; neighbours: boolean; escaped: boolean;
  foundBy: string | null; missedBy: string | null; note: string | null;
  tracedAt: string; decidedAt: string | null;
  commits: LinkCommit[]; runs: RunGuess[];
}

export interface BugView {
  ticketId: number; isBug: boolean; tracedAt: string | null; linesBlamed: number; note: string | null;
  links: BugLinkView[];
}

export interface TracedHere {
  linkId: number; bugTicketId: number; bugTitle: string | null; status: string; escaped: boolean;
  foundBy: string | null; missedBy: string | null; share: number; tracedAt: string;
}

export const FOUND_BY = [
  { key: 'Owner', label: 'Me' },
  { key: 'LaterTicketQa', label: "A later ticket's QA" },
  { key: 'Production', label: 'Production' },
  { key: 'Steward', label: 'The steward' },
] as const;

export const MISSED_BY = ['speculate', 'implement', 'qa-test', 'refine', 'review'] as const;

export const getBug = (ticketId: number) => fetch(`/api/tickets/${ticketId}/bug`).then(json<BugView>);
export const getTracedHere = (ticketId: number) => fetch(`/api/tickets/${ticketId}/bugs-traced-here`).then(json<TracedHere[]>);

export const setBug = (ticketId: number, isBug: boolean) =>
  fetch(`/api/tickets/${ticketId}/bug`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ isBug }) })
    .then(json<{ id: number; isBug: boolean }>);

export const traceBug = (ticketId: number) => post(`/api/tickets/${ticketId}/bug/trace`).then(json<BugView>);

export const confirmLink = (linkId: number, foundBy: string, missedBy: string, note?: string) =>
  post(`/api/bug-links/${linkId}/confirm`, { foundBy, missedBy, note }).then(json<{ id: number }>);

export const rejectLink = (linkId: number, note?: string) =>
  post(`/api/bug-links/${linkId}/reject`, { note }).then(json<{ id: number }>);

export const reopenLink = (linkId: number) => post(`/api/bug-links/${linkId}/reopen`).then(json<{ id: number }>);

export const addLink = (ticketId: number, sourceTicketId: number, foundBy: string, missedBy: string, note?: string) =>
  post(`/api/tickets/${ticketId}/bug/links`, { sourceTicketId, foundBy, missedBy, note }).then(json<{ id: number }>);
