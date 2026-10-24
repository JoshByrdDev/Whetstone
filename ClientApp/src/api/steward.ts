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
  /** The unit whose open decisions this asks about; with `decisions`, it is answered on those. */
  unit?: number | null;
  decisions?: number[];
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
  /** TicketNote: something the steward means to do about the ticket, open until it closes it. */
  kind: 'Call' | 'BeforeRelease' | 'OwnerSaid' | 'TicketNote' | 'Ruling';
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
  /** A ruling's: what it rests on. */
  basis?: string | null;
  /** A ruling's: when the owner reversed it, and what they said should hold instead. */
  reversedAt?: string | null;
  reversedBecause?: string | null;
}

/** A ruling in force that the owner has not yet kept or reversed. */
export const unseenRuling = (e: StewardEntry) => e.kind === 'Ruling' && !e.handledAt && !e.reversedAt;

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
  /** The project's tickets that are not Done yet. */
  tickets?: { id: number; phase: string; title: string }[] | null;
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

/** A ticket's place in a plan: the plan's key, the unit's number, the outcomes it carries. */
export interface TicketPlanLink { key: string; unit: number; outcomes: string[] }

/** Link a ticket to a unit of a plan (plan null is the one being worked), or unit null to take it out. */
export const setTicketPlan = (ticketId: number, link: { plan?: string | null; unit: number | null; outcomes?: string[] }) =>
  fetch(`/api/tickets/${ticketId}/plan`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(link) })
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
  /** The numbers of the decisions its spec still leaves open. */
  openDecisions?: number[];
  text: string;
  groups: { heading: string; outcomes: StewardPlanOutcome[] }[];
}

/** One choice a unit's spec leaves open, as the spec writes it. */
export interface UnitDecision {
  number: number;
  title: string;
  /** Everything the spec says about it, after its title. */
  text: string;
  options: { letter: string; text: string }[];
  /** The pick the spec recommends, as written, and the reason it gives. */
  pick?: string | null;
  pickWhy?: string | null;
  /** The options the pick names; empty when the pick is words rather than a letter. */
  pickLetters: string[];
}

export interface UnitDecisions {
  unit: number; file: string; title: string; agreed: boolean; draft: boolean;
  open: UnitDecision[];
}

/** An answer to one decision: letters ("a" or "a,c"), or none for the pick as written; a note may go with either. */
export interface UnitDecisionChoice { number: number; option?: string | null; note?: string | null }

export const getUnitDecisions = (project: string, unit: number) =>
  fetch(`/api/steward/plan/units/${unit}/decisions?${q(project)}`)
    .then(json<{ answer: string; data: UnitDecisions }>).then(r => r.data);

/**
 * Settle some of a unit's decisions: written into its spec under "Decided". With `agree`, the
 * unit is agreed in the same commit; with `answering`, the question that asked for them is answered.
 */
export const decideStewardUnit = (project: string, unit: number,
  body: { decisions: UnitDecisionChoice[]; agree?: boolean; answering?: string | null }) =>
  fetch(`/api/steward/plan/units/${unit}/decisions?${q(project)}`, {
    method: 'POST', headers: jsonHeaders, body: JSON.stringify(body),
  }).then(json<{ answer: string; why?: string | null }>);

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

/** A flag on a ticket's change that is the owner's to decide. */
export interface StewardOwnerFlag {
  flag: string;
  say?: string | null;
  detail?: string[] | null;
  /** Cleared by the owner for the change as it stands: the steward may merge past it. */
  cleared: boolean;
  /** False for a flag that says nothing was checked, which there is nothing to clear of. */
  clearable: boolean;
}

/** The owner's flags on one ticket, as its steward's merge would raise them now. */
export const getStewardOwnerFlags = (project: string, ticketId: number) =>
  fetch(`/api/steward/tickets/${ticketId}/flags?${q(project)}`)
    .then(json<{ answer: string; data?: { tip?: string | null; phase?: string; flags: StewardOwnerFlag[] } }>)
    .then(r => r.data ?? { flags: [] });

/** Let the steward merge this ticket past one flag, for its change as it stands. */
export const clearStewardFlag = (project: string, ticketId: number, flag: string) =>
  fetch(`/api/steward/tickets/${ticketId}/flags/${encodeURIComponent(flag)}/clear?${q(project)}`, { method: 'POST' })
    .then(json<{ answer: string }>);

/** One ticket's lines of the record: what was decided about it, and what it leaves for a release. */
export const getStewardEntriesForTicket = (project: string, ticketId: number) =>
  fetch(`/api/steward/entries?${q(project)}&ticket=${ticketId}`).then(json<StewardEntry[]>);

/** Tick a line off or un-tick it, or say what kind of release note it is. */
/** Let one of the steward's rulings stand. */
export const keepStewardRuling = (project: string, id: number) =>
  fetch(`/api/steward/rulings/${id}/keep?${q(project)}`, { method: 'POST' }).then(json<{ answer: string }>);

/** Reverse one of the steward's rulings, saying what should hold instead. */
export const reverseStewardRuling = (project: string, id: number, because: string) =>
  fetch(`/api/steward/rulings/${id}/reverse?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ because }) })
    .then(json<{ answer: string }>);

export const markStewardEntry = (project: string, id: number, mark: { handled?: boolean; release?: ReleaseKind }) =>
  fetch(`/api/steward/entries/${id}?${q(project)}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(mark) })
    .then(json<{ answer: string }>);
