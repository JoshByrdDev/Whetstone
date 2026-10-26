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
  /** The register's decisions it bears on, with their rules, as the steward claimed when it asked. */
  touches?: { number: number; rule?: string | null }[];
  /** Which of those each option, by its label, would change. */
  changes?: Record<string, number[]> | null;
  /** An idea the steward proposes, committed as written on a yes. */
  idea?: { file: string; text: string } | null;
  /** For a slice being built: the story sentence an answer changes, and its rewording under each option. */
  story?: { sentence: string; rewordings?: Record<string, string> | null } | null;
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
  waiting: StewardQuestion[];
  answered: StewardQuestion[];
}

export interface StewardEntry {
  id: number;
  /** TicketNote: something the steward means to do about the ticket, open until it closes it. */
  kind: 'Call' | 'BeforeRelease' | 'OwnerSaid' | 'TicketNote' | 'Ruling' | 'Learned';
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
  /** A ruling's: the decisions it reads, and what happened to one that no longer holds. */
  reads?: number[];
  flagged?: string | null;
  /** A ruling's: when the owner reversed it, and what they said should hold instead. */
  reversedAt?: string | null;
  reversedBecause?: string | null;
  /** A learned fact's: where it went in the notes store once placed (its suggested key before). */
  placed?: string | null;
}

/** A ruling in force that the owner has not yet kept or reversed. */
/** A ruling the owner has to look at: new, or reading a decision that no longer holds. */
export const unseenRuling = (e: StewardEntry) => e.kind === 'Ruling' && !e.reversedAt && (!e.handledAt || !!e.flagged);

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

/** Answer a question; `replaces` is which of the decisions it touches the answer replaces. */
export const answerStewardQuestion = (project: string, key: string, answer: string, replaces?: number[], story?: string) =>
  fetch(`/api/steward/questions/${encodeURIComponent(key)}/answer?${q(project)}`, {
    method: 'POST', headers: jsonHeaders, body: JSON.stringify({ answer, replaces, story }),
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
  /** When the steward last set where it stands. Absent from a server before it was sent. */
  updatedAt?: string | null;
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

export type UnitStage = 'idea' | 'draft' | 'notAgreed' | 'agreed' | 'building' | 'built' | 'released';

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
    /** Agreed slices whose file or number changed since they were agreed. */
    moved?: string[];
  } | null;
  /** The older single plan document, on a plan from before charters. */
  master?: { file: string; text: string } | null;
  /** Why the plan exists, its words, where things are. Decides nothing. */
  charter?: { file: string; text: string } | null;
  /** The plan as read: charter, the project's decisions, every slice in order. Generated. */
  readable?: string | null;
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
export const startStewardPlan = (project: string,
  plan: { key: string; name: string; folder?: string; ideas: { title: string; paragraph: string }[] }) =>
  fetch(`/api/steward/plan/new?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify(plan) })
    .then(json<{ answer: string; data: { charter: string; ideas: string[] }; settings: string }>);

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

// ---- the register of decisions -----------------------------------------------------------

export type DecisionStatus = 'inForce' | 'replaced' | 'provisional' | 'withdrawn';

/** One option a question offered, as the register hands it back with the decision it made. */
export interface DecisionOffered { label: string; detail?: string | null; picked: boolean; stewardsPick: boolean }

export interface StewardDecision {
  number: number;
  rule: string;
  title?: string | null;
  /** "project", or the unit (and outcomes) it applies to, in words. */
  appliesTo: string;
  plan?: string | null;
  unit?: number | null;
  outcomes?: string | null;
  status: DecisionStatus;
  provisionalUntil?: string | null;
  replaces?: { number: number; why: string }[] | null;
  replacedBy?: { number: number; why: string }[] | null;
  withdrawn?: { on: string; because?: string | null } | null;
  on: string;
  cameFrom: string;
  key?: string | null;
  question?: string | null;
  offered?: DecisionOffered[] | null;
  where?: string | null;
}

export interface DecisionPair { id: number; older: number; newer: number; why: string }

export interface StewardFollowUp { id: number; decision: number; text: string; at: string; citedBy?: string | null; droppedAt?: string | null }

export interface StewardInstruction {
  id: number; text: string; until: string; at: string;
  closedAt?: string | null; closedBy?: string | null; closedNote?: string | null;
}

export interface StewardRegister {
  next: number;
  plan?: string | null;
  units: { unit: number; title: string }[];
  decisions: StewardDecision[];
  mayConflict: DecisionPair[];
  /** Provisional decisions whose point has come. */
  due: number[];
  followUps: StewardFollowUp[];
  instructions: StewardInstruction[];
}

export interface NewDecision {
  rule: string;
  title?: string;
  unit?: number | null;
  outcomes?: string[];
  provisional?: boolean;
  provisionalUnit?: number | null;
  replaces?: { number: number; why: string }[];
}

/** The register: every decision with its status, and the pairs the import thinks may disagree. */
export const getStewardRegister = (project: string) =>
  fetch(`/api/steward/decisions?${q(project)}`).then(json<{ data: StewardRegister }>).then(r => r.data);

/** Record a decision directly; with `replaces`, it changes the decisions it names. */
export const recordStewardDecision = (project: string, d: NewDecision) =>
  fetch(`/api/steward/decisions?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify(d) })
    .then(json<{ answer: string; data: { number: number } }>);

/** Withdraw a decision, with nothing in its place. */
export const withdrawStewardDecision = (project: string, number: number, because: string) =>
  fetch(`/api/steward/decisions/${number}/withdraw?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ because }) })
    .then(json<{ answer: string }>);

/** Settle a pair the import thought may disagree: the newer replaces the older, or both stand. */
export const settleDecisionPair = (project: string, id: number, replaces: boolean, why?: string) =>
  fetch(`/api/steward/decisions/pairs/${id}?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ replaces, why }) })
    .then(json<{ answer: string }>);

/** Take into the register what has arrived since it was built. */
export const importStewardDecisions = (project: string) =>
  fetch(`/api/steward/decisions/import?${q(project)}`, { method: 'POST' }).then(json<{ answer: string; data: unknown }>);

/** A provisional decision whose point has come, confirmed so it holds like any other. */
export const confirmStewardDecision = (project: string, number: number) =>
  fetch(`/api/steward/decisions/${number}/confirm?${q(project)}`, { method: 'POST' }).then(json<{ answer: string }>);

/** Something to do until a condition holds: an instruction, not a decision. */
export const giveStewardInstruction = (project: string, text: string, until: string) =>
  fetch(`/api/steward/instructions?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ text, until }) })
    .then(json<{ answer: string }>);

export const closeStewardInstruction = (project: string, id: number, note?: string) =>
  fetch(`/api/steward/instructions/${id}/close?${q(project)}`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ note }) })
    .then(json<{ answer: string }>);

/** A follow-up that is nothing to take up. */
export const dropStewardFollowUp = (project: string, id: number) =>
  fetch(`/api/steward/followups/${id}/drop?${q(project)}`, { method: 'POST' }).then(json<{ answer: string }>);
