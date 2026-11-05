/**
 * Escapes: finished work a person sent back after the pipeline called it done, and what kind of
 * miss it was. The implementation run that picks one up classifies it (classify_escape); this route
 * is a person's correction, recorded as theirs.
 */

export interface EscapeView {
  id: number; ticketId: number; ticketTitle: string | null; projectKey: string; track: string;
  fromPhase: string; toPhase: string; reason: string | null; noteCommentId: number | null; note: string | null;
  build: string | null; at: string; backfilled: boolean;
  kind: string | null; source: string | null; why: string | null; classifiedAt: string | null;
  /** Worker (the implementation run that picked it up) or Human (a person's correction). */
  classifiedBy: string | null; classifiedByRunId: number | null; overridden: boolean;
}

export interface EscapeList { kinds: string[]; sources: string[]; escapes: EscapeView[] }

export function escapesUrl(project: string | null, unclassified: boolean): string {
  const q = new URLSearchParams();
  if (project) q.set('project', project);
  if (unclassified) q.set('unclassified', 'true');
  return `/api/escapes?${q.toString()}`;
}

export async function classifyEscape(id: number, kind: string | null, source: string | null, note?: string): Promise<void> {
  const res = await fetch(`/api/escapes/${id}/classification`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ kind, source, note: note ?? null }),
  });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try { const b = await res.json(); if (b?.error) msg = b.error; } catch { /* non-JSON error body */ }
    throw new Error(msg);
  }
}
