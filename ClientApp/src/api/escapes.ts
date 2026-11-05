/**
 * Escapes: finished work a person sent back after the pipeline called it done, and their call on
 * what kind of miss it was. Classifying is a route no MCP tool reaches - only a person says.
 */

export interface EscapeView {
  id: number; ticketId: number; ticketTitle: string | null; projectKey: string; track: string;
  fromPhase: string; toPhase: string; reason: string | null; noteCommentId: number | null; note: string | null;
  build: string | null; at: string; backfilled: boolean;
  kind: string | null; source: string | null; classifierNote: string | null; classifiedAt: string | null;
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
