// The owner's side of a discovery (Api/DiscoveryEndpoints.cs): its record, the verdict on each
// proposal, and Complete. There is no MCP tool behind the last two - only a person decides.

export type ObservationKind = 'Wall' | 'Judgement' | 'Idea' | 'RoughEdge' | 'Bug';
export type ProposalKind = 'Ticket' | 'Bug' | 'Slice' | 'Decide';
export type ProposalStatus = 'Proposed' | 'Approved' | 'Rejected' | 'Snoozed' | 'Filed' | 'Dropped';
export type RejectReason = 'Generic' | 'AlreadyDecided' | 'NotNow' | 'Wrong';
export type RecheckKind = 'FixUp' | 'New';
export type GoalOutcome = 'Met' | 'NotMet' | 'Unclear';

export interface DiscoveryRoute {
  persona: string;
  signedInAs?: string | null;
  jobs: string[];
  startCases: string[];
  widths: string[];
  notes?: string | null;
  areas: string[];
  writtenAt: string;
  /** When the owner approved it at the route gate; null when the gate was off or it is still waiting. */
  approvedAt?: string | null;
  ownerEdited?: boolean;
}

export interface DiscoveryObservation {
  id: number;
  kind: ObservationKind;
  title: string;
  saw: string;
  tried?: string | null;
  job?: number | null;
  url?: string | null;
  width?: string | null;
  signedInAs?: string | null;
  area?: string | null;
  shot?: string | null;
  createdAt: string;
  /** 0 for the scout, N for the Nth recheck. */
  round?: number;
}

export interface DiscoveryVisit {
  id: number;
  url: string;
  routePattern?: string | null;
  width?: string | null;
  seconds?: number | null;
  area?: string | null;
}

export interface DiscoveryProposal {
  id: number;
  rank: number;
  kind: ProposalKind;
  status: ProposalStatus;
  title: string;
  why?: string | null;
  body: string;
  workItems: string[];
  size?: string | null;
  confidence?: string | null;
  area?: string | null;
  observationIds: number[];
  dropReason?: string | null;
  rejectReason?: RejectReason | null;
  rejectNote?: string | null;
  snoozeUntil?: string | null;
  answer?: string | null;
  filedTicketId?: number | null;
  filedNote?: string | null;
  edited: boolean;
  /** The owner's one tap: something they had not already thought of. Null until they say. */
  newToMe?: boolean | null;
  /** Sift's words as written, kept beside the owner's edits. */
  draftKind?: ProposalKind;
  draftTitle?: string;
  draftBody?: string;
  /** 0 when the sift wrote it, N when the Nth recheck did. */
  round?: number;
  recheckKind?: RecheckKind | null;
  /** For a fix-up: the approved proposal it fixes up. */
  goalId?: number | null;
}

/** One recheck's finding on one approved goal. */
export interface GoalCheck {
  id: number;
  goalId: number;
  filedTicketId?: number | null;
  round: number;
  outcome: GoalOutcome;
  note: string;
  observationIds: number[];
  at: string;
}

export interface Discovery {
  route: DiscoveryRoute | null;
  observations: DiscoveryObservation[];
  visits: DiscoveryVisit[];
  proposals: DiscoveryProposal[];
  filed: { id: number; title?: string | null; phase: string; proposalId?: number | null; blockedReason?: string | null }[];
  goalChecks?: GoalCheck[];
  /** Which recheck it is on (0 before the first), how many get a verdict, and the goals the next (or current) one asks about. */
  recheck?: { round: number; roundsWithVerdict: number; goals: number[] };
}

/** The owner's edit of a route at its gate: each field absent is left as the chart wrote it. */
export interface RouteEdit {
  persona?: string;
  jobs?: string[];
  signedInAs?: string;
  widths?: string[];
  notes?: string;
}

export interface VerdictChange {
  status?: 'Proposed' | 'Approved' | 'Rejected' | 'Snoozed';
  title?: string;
  why?: string;
  body?: string;
  workItems?: string[];
  kind?: ProposalKind;
  rejectReason?: RejectReason;
  rejectNote?: string;
  snoozeUntil?: string | null;
  answer?: string;
  newToMe?: boolean;
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try { const b = await res.json(); if (b?.error) msg = b.error; } catch { /* not JSON */ }
    throw new Error(msg);
  }
  return res.json() as Promise<T>;
}

const headers = { 'Content-Type': 'application/json' };

export const getDiscovery = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/discovery`).then(json<Discovery>);

export const shotUrl = (ticketId: number, name: string) =>
  `/api/tickets/${ticketId}/discovery/shots/${encodeURIComponent(name)}`;

export const decide = (proposalId: number, change: VerdictChange) =>
  fetch(`/api/discovery/proposals/${proposalId}`, { method: 'PUT', headers, body: JSON.stringify(change) })
    .then(json<{ id: number; status: ProposalStatus; edited: boolean }>);

export const complete = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/discovery/complete`, { method: 'POST' })
    .then(json<{ filed: number; answered: number; briefed: number; ticketIds: number[]; phase: string }>);

/** Promotes a line Sift dropped to an undecided proposal. Recorded: restores are how a sift that cut too much is counted. */
export const restore = (proposalId: number) =>
  fetch(`/api/discovery/proposals/${proposalId}/restore`, { method: 'POST' })
    .then(json<{ id: number; status: ProposalStatus; rank: number }>);

/** The owner's edit of the route at its gate, before a scout walks it. */
export const editRoute = (ticketId: number, edit: RouteEdit) =>
  fetch(`/api/tickets/${ticketId}/discovery/route`, { method: 'PUT', headers, body: JSON.stringify(edit) })
    .then(json<{ ticketId: number; ownerEdited: boolean }>);

/** The owner's approval of the route at its gate: the discovery goes on to be scouted. */
export const approveRoute = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/discovery/route/approve`, { method: 'POST' })
    .then(json<{ phase: string }>);
