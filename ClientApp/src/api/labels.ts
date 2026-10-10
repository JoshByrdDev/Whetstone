import type { LabelRef, LabelUsage } from '../types/tickets';

/**
 * Your filing tags. Its own module, against its own routes, for the reason spelled out on the
 * server (`Api/LabelEndpoints.cs`): the ticket API's shapes are what every worker receives over MCP,
 * and labels are the one thing on this board the pipeline is never shown.
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

const jsonHeaders = { 'Content-Type': 'application/json' };

/** Assignments is keyed by ticket id; a ticket wearing nothing is simply absent. */
export interface LabelIndex {
  labels: LabelUsage[];
  assignments: Record<string, LabelRef[]>;
}

/** Everything at once: the vocabulary for the picker, and who wears what for the board's chips. */
export const getLabels = () => fetch('/api/labels').then(json<LabelIndex>);

export const getTicketLabels = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/labels`).then(json<LabelRef[]>);

/**
 * Add a label to a ticket BY NAME — which is what makes the picker one control rather than two. A
 * name already in the vocabulary is matched (case-insensitively); one that is not is created on the
 * spot. The caller never has to decide which of the two it is doing.
 */
export const addTicketLabel = (ticketId: number, name: string) =>
  fetch(`/api/tickets/${ticketId}/labels`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ name }) })
    .then(json<LabelRef[]>);

/** Take it off. If that was the last ticket wearing it, the label stops existing. */
export const removeTicketLabel = (ticketId: number, labelId: number) =>
  fetch(`/api/tickets/${ticketId}/labels/${labelId}`, { method: 'DELETE' }).then(json<LabelRef[]>);

/** Rename everywhere at once. Renaming onto a name already in use merges the two. */
export const renameLabel = (labelId: number, name: string) =>
  fetch(`/api/labels/${labelId}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify({ name }) })
    .then(json<LabelUsage>);

/** Retire it from every ticket wearing it. */
export const deleteLabel = (labelId: number) =>
  fetch(`/api/labels/${labelId}`, { method: 'DELETE' }).then(json<{ deleted: boolean }>);
