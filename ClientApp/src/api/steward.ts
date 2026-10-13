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
  /** The unit of the plan it belongs to, by way of its ticket. */
  unit?: number | null;
  /** For a release note: what kind of thing it is. */
  release?: ReleaseKind | null;
  /** When the owner ticked it off. */
  handledAt?: string | null;
  text: string;
  at: string;
}

export type ReleaseKind = 'Migration' | 'Config' | 'Action' | 'Note';

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

export interface StewardPlanTicket { id: number; phase: string; title: string }

export interface StewardPlanOutcome {
  outcome: string;
  /** The sentence the spec gives it. */
  text: string;
  state: StewardOutcomeRow['state'];
  moreToFile: boolean;
  note?: string | null;
  tickets: StewardPlanTicket[];
}

/** One unit of the plan, whole: its spec as written, and its outcomes under the spec's own headings. */
export interface StewardPlanUnitView {
  unit: number;
  file: string;
  title: string;
  agreed: boolean;
  draft: boolean;
  /** Every outcome has been called done. */
  done: boolean;
  /** Where it is on the way from a draft to released. */
  stage: UnitStage;
  agreedAt?: string | null;
  releasedAt?: string | null;
  /** Why a draft cannot be agreed from the board, when it cannot. */
  cannotAgree?: string | null;
  /** What its release still asks, by kind; null for a unit nobody has agreed. */
  release?: { migrations: number; config: number; actions: number; toKnow: number; outstanding: number } | null;
  progress: { percent: number; done: number; inProgress: number; notStarted: number; outcomes: number };
  text: string;
  groups: { heading: string; outcomes: StewardPlanOutcome[] }[];
}

export type UnitStage = 'draft' | 'notAgreed' | 'agreed' | 'building' | 'built' | 'released';

export interface StewardPlanName { key: string; name: string; working: boolean }

export interface StewardPlanView {
  /** Why there is no plan to show, when there is none. */
  why?: string | null;
  plan?: {
    /** Every plan the project has: the one being worked, then the ones it has finished. */
    plans?: StewardPlanName[];
    /** The plan these units are of. */
    plan?: StewardPlanName & { unitsDir: string };
    current?: number | null;
    units?: StewardPlanUnitView[];
  } | null;
  /** The document the units are cut from, when the project names one. */
  master?: { file: string; text: string } | null;
}

/** The plan being worked, or with `plan` one the project has finished. */
export const getStewardPlan = (project: string, plan?: string | null) =>
  fetch(`/api/steward/plan?${q(project)}${plan != null ? `&plan=${encodeURIComponent(plan)}` : ''}`).then(json<StewardPlanView>);

/** Agree a draft unit: the commit that agrees it, made from here. */
export const agreeStewardUnit = (project: string, unit: number) =>
  fetch(`/api/steward/plan/units/${unit}/agree?${q(project)}`, { method: 'POST' }).then(json<{ answer: string }>);

/** Say a unit's work has been released, or take that back. */
export const setStewardUnitReleased = (project: string, unit: number, released: boolean) =>
  fetch(`/api/steward/plan/units/${unit}/released?${q(project)}`, {
    method: 'PUT', headers: jsonHeaders, body: JSON.stringify({ released }),
  }).then(json<{ answer: string }>);

/** Start a new plan: its documents into the repo, and the project pointed at it. */
export const startStewardPlan = (project: string, plan: { key: string; name: string; folder?: string }) =>
  fetch(`/api/steward/plan/new?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify(plan) })
    .then(json<{ answer: string; data: { master: string; first: string }; settings: string }>);

/** One ticket's lines of the record: what was decided about it, and what it leaves for a release. */
export const getStewardEntriesForTicket = (project: string, ticketId: number) =>
  fetch(`/api/steward/entries?${q(project)}&ticket=${ticketId}`).then(json<StewardEntry[]>);

/** Tick a line off or un-tick it, or say what kind of release note it is. */
export const markStewardEntry = (project: string, id: number, mark: { handled?: boolean; release?: ReleaseKind }) =>
  fetch(`/api/steward/entries/${id}?${q(project)}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(mark) })
    .then(json<{ answer: string }>);
