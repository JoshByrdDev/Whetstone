/**
 * A project's steward, from its owner's side: what it did, what it is waiting on, and the answers.
 *
 * Its own module against its own routes (`Api/StewardEndpoints.cs`). Nothing here is something the
 * steward itself can call - it asks through its tools and cannot reply to itself, which is the
 * whole reason answering lives on this side.
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

const jsonHeaders = { 'Content-Type': 'application/json' };
const q = (project: string) => `project=${encodeURIComponent(project)}`;

export type StewardMode = 'off' | 'propose' | 'manage' | 'full';

export interface StewardOption { label: string; detail?: string | null; recommended?: boolean }

export interface StewardQuestion {
  key: string;
  question: string;
  why: string;
  options: StewardOption[];
  heldUp: string;
  ticketId?: number | null;
  askedAt: string;
  answer?: string | null;
  answeredAt?: string | null;
}

export interface StewardPage {
  project: string;
  displayName: string;
  /** What the steward may actually do, after the project's own limits. */
  mode: StewardMode;
  /** What was asked for. Differs from `mode` only when full is not on offer here. */
  modeSetting: StewardMode;
  /** Why full is unavailable for this project, when it is. */
  fullUnavailable?: string | null;
  running: boolean;
  due: boolean;
  dueReason: string;
  lastRunStartedAt?: string | null;
  lastRunEndedAt?: string | null;
  lastRunSummary?: string | null;
  resting: boolean;
  memo: string;
  waiting: StewardQuestion[];
  answered: StewardQuestion[];
}

export interface StewardEntry {
  id: number;
  kind: 'Call' | 'BeforeRelease' | 'OwnerSaid';
  ticketId?: number | null;
  ticketTitle?: string | null;
  text: string;
  at: string;
}

export interface StewardOutcomeRow {
  outcome: string;
  state: 'NotStarted' | 'InProgress' | 'Done';
  moreToFile: boolean;
  note?: string | null;
  tickets: string[];
}

export interface StewardProgress {
  unit: number;
  percent: number;
  done: number;
  inProgress: number;
  notStarted: number;
  outcomes: number;
}

export interface StewardPlanUnit { unit: number; file: string; agreed: boolean; draft: boolean; outcomes: number }

/** The plan half of the steward's own status: the same figures a run is shown. */
export interface StewardStatus {
  plan: StewardPlanUnit[] | string;
  progress?: StewardProgress | null;
  outcomes?: StewardOutcomeRow[] | null;
  limits: { createdToday: string; open: string; sendBacksPerTicket: number };
}

export const getSteward = (project: string) => fetch(`/api/steward?${q(project)}`).then(json<StewardPage>);

export const getStewardEntries = (project: string) =>
  fetch(`/api/steward/entries?${q(project)}`).then(json<StewardEntry[]>);

/** Null while the steward is off: there is no status to show for a steward that does not exist. */
export const getStewardStatus = (project: string) =>
  fetch(`/api/steward/status?${q(project)}`)
    .then(json<{ answer: string; data?: StewardStatus }>)
    .then(r => (r.answer === 'status' ? r.data ?? null : null));

export const setStewardMode = (project: string, mode: StewardMode) =>
  fetch(`/api/steward/mode?${q(project)}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify({ mode }) })
    .then(json<{ mode: StewardMode; modeSetting: StewardMode }>);

export const answerStewardQuestion = (project: string, key: string, answer: string) =>
  fetch(`/api/steward/questions/${encodeURIComponent(key)}/answer?${q(project)}`, {
    method: 'POST', headers: jsonHeaders, body: JSON.stringify({ answer }),
  }).then(json<{ answer: string }>);

export const tellSteward = (project: string, text: string) =>
  fetch(`/api/steward/said?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ text }) })
    .then(json<{ answer: string }>);
