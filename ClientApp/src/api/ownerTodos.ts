/**
 * The owner's to-do list (docs/owner-todo-plan.md), from the owner's side: `Api/OwnerTaskEndpoints.cs`.
 * Every write here is one no agent can make - done, drop, wait, reopen, edit.
 */

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      msg = body?.error ?? body?.why ?? msg;
    } catch { /* non-JSON error body */ }
    throw new Error(msg);
  }
  return res.json() as Promise<T>;
}

const post = (url: string, body: unknown) =>
  fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).then(json<unknown>);

export type OwnerTodoKind = 'secret' | 'config' | 'infra' | 'account' | 'deploy' | 'action';
export type OwnerTodoStatus = 'open' | 'waiting' | 'done' | 'dropped';

export const KINDS: { id: OwnerTodoKind; word: string }[] = [
  { id: 'secret', word: 'Secret' },
  { id: 'config', word: 'Config' },
  { id: 'infra', word: 'Infra' },
  { id: 'account', word: 'Account' },
  { id: 'deploy', word: 'Deploy' },
  { id: 'action', word: 'By hand' },
];

export interface OwnerTodoNote { id: number; kind: string; by: string; ticketId?: number | null; text: string; at: string }

export interface OwnerTodo {
  ref: string;
  id: number;
  project?: string | null;
  kind: OwnerTodoKind;
  status: OwnerTodoStatus;
  title: string;
  body: string;
  key?: string | null;
  waitingOn?: string | null;
  waitingUntil?: string | null;
  ticketId?: number | null;
  unit?: number | null;
  holdsRelease: boolean;
  /** For after its slice is released and deployed: linked to the slice, never holding it. */
  afterRelease: boolean;
  /** The slice whose release it holds, placed as the release gate places it. */
  slice?: { unit: number; title: string } | null;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
  doneAt?: string | null;
  doneNote?: string | null;
  droppedAt?: string | null;
  droppedBecause?: string | null;
  suggestion?: 'done' | 'obsolete' | null;
  suggestionBecause?: string | null;
  notice?: string | null;
  askers: number[];
  notes: OwnerTodoNote[];
}

export const listOwnerTodos = (status: 'open' | 'done' | 'dropped' | 'all' = 'open') =>
  fetch(`/api/owner-todos?status=${status}`).then(json<{ open: number; items: OwnerTodo[] }>);

export const ownerTodoCount = () => fetch('/api/owner-todos/count').then(json<{ open: number }>);

export const addOwnerTodo = (t: { project?: string | null; title: string; body: string; kind: OwnerTodoKind }) =>
  post('/api/owner-todos', { ...t, project: t.project ?? null });

export const editOwnerTodo = (id: number, t: { title?: string; body?: string; kind?: OwnerTodoKind; project?: string; holdsRelease?: boolean; afterRelease?: boolean }) =>
  fetch(`/api/owner-todos/${id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(t) }).then(json<unknown>);

export const doneOwnerTodo = (id: number, note: string) => post(`/api/owner-todos/${id}/done`, { note });
export const dropOwnerTodo = (id: number, because: string) => post(`/api/owner-todos/${id}/drop`, { because });
export const waitOwnerTodo = (id: number, on: string, until?: string | null) => post(`/api/owner-todos/${id}/wait`, { on, until: until || null });
export const reopenOwnerTodo = (id: number, why?: string) => post(`/api/owner-todos/${id}/reopen`, { why: why ?? null });
export const noteOwnerTodo = (id: number, text: string) => post(`/api/owner-todos/${id}/note`, { text });

/** Open the To-Do tab, optionally on one item, from anywhere in the app (App listens). */
export const openOwnerTodo = (ref?: string) =>
  window.dispatchEvent(new CustomEvent('open-owner-todo', { detail: { ref: ref ?? null } }));

/** Tell the badge the count may have moved. */
export const ownerTodosChanged = () => window.dispatchEvent(new Event('owner-todos-changed'));
