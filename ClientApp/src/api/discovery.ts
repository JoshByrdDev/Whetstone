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
  /** When retention removed its screenshot; the text stays. */
  shotPrunedAt?: string | null;
  /** The recording it was seen in, and how far into it. */
  recordingId?: number | null;
  videoAt?: number | null;
}

/** A proposal's clip: cut and ready, cut when played, removed, or not to be had and why. */
export type ClipState = 'Ready' | 'Cuttable' | 'Removed' | 'RecordingRemoved' | 'None' | 'NoFfmpeg';

/** One run's video, with the chapters it marked. A pruned one keeps its row. */
export interface DiscoveryRecording {
  id: number;
  round: number;
  startedAt: string;
  seconds: number;
  bytes: number;
  verdictAt?: string | null;
  prunedAt?: string | null;
  chapters: { id: number; title: string; job?: number | null; videoAt?: number | null }[];
  /** What is said over it, in the order it is heard. Null once the recording is removed. */
  voiceOver?: VoiceLine[] | null;
}

/**
 * One line of a recording's voice-over. `Say` is the run's own words; a run that said nothing has
 * its chapters and observations read aloud instead (`Chapter`, `Observation`, with the
 * observation's kind). `clip` is set once it is voiced; until then it plays as a caption for
 * `seconds` at a reading pace.
 */
export interface VoiceLine {
  key: string;
  from: 'Say' | 'Chapter' | 'Observation';
  text: string;
  at: number;
  kind?: DiscoveryObservation['kind'] | null;
  clip?: string | null;
  seconds: number;
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
  clip?: ClipState;
  clipPrunedAt?: string | null;
  /** The owner's pin: its clip and shots outlive every retention rule. */
  pinned?: boolean;
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
  recordings?: DiscoveryRecording[];
  /** Whether this machine can cut clips (ffmpeg). */
  media?: { ffmpeg: boolean };
  /** Whether the voice-over can be generated here (why not, when not), and how the last press went. */
  voicing?: { unavailable?: string | null; state?: { running: boolean; activity?: string | null; error?: string | null } | null };
  /** Which taste version each Sift and Recheck run of this discovery read; versionNumber null = no taste file yet. */
  tasteReads?: { id: number; runId?: number | null; round: number; phase: string; versionId?: number | null; versionNumber?: number | null; at: string }[];
}

/** One version of a project's taste file (Services/DiscoveryTaste.cs). */
export interface TasteVersion {
  id: number;
  number: number;
  source: 'Fold' | 'Owner' | 'Revert' | 'Outside';
  hash: string;
  commit?: string | null;
  commitNote?: string | null;
  verdicts: number;
  events: number;
  hasSuggestions: boolean;
  revertOf?: number | null;
  rodeOnTicketId?: number | null;
  at: string;
  /** How many Sift and Recheck runs read it. */
  reads: number;
}

/** A project's taste file as the owner's panel shows it. */
export interface TasteStatus {
  path: string;
  exists: boolean;
  text: string;
  owner: string;
  learned: string;
  current: TasteVersion | null;
  /** The latest fold's suggestions for the owner's section, which no fold changes. */
  suggestions?: string | null;
  versions: TasteVersion[];
  fold: { every: number; verdicts: number; due: boolean; requested: boolean; lastNote?: string | null; lastAt?: string | null; claimedAt?: string | null; teachNothing: number };
  /** Rejected as already decided since the last fold: a decision the register may be missing. */
  alreadyDecided: { proposalId: number; discovery: number; title: string; note?: string | null }[];
  verdictsEver: number;
  versioned: boolean;
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

export const recordingUrl = (ticketId: number, recordingId: number) =>
  `/api/tickets/${ticketId}/discovery/recordings/${recordingId}`;

/** Voice every recording's lines not voiced yet, with the Classroom's narrator. Returns at once. */
export const generateVoiceOver = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/discovery/voice-over`, { method: 'POST' }).then(json<{ message: string }>);

/** A voiced line, shared with the Classroom's: named by its content, so it never changes. */
export const voiceClipUrl = (clip: string) => `/api/walkthrough-clips/${clip}.wav`;

/** A proposal's clip. The first request cuts it from the run's recording. */
export const clipUrl = (proposalId: number) => `/api/discovery/proposals/${proposalId}/clip`;

/** The owner's pin on a proposal's clip and shots. No tool behind it. */
export const pin = (proposalId: number, pinned: boolean) =>
  fetch(`/api/discovery/proposals/${proposalId}/pin`, { method: 'PUT', headers, body: JSON.stringify({ pinned }) })
    .then(json<{ id: number; pinned: boolean }>);

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

// ---- the taste file (step 4): the owner's routes, none with a tool behind it ------------------

export const getTaste = (project: string) =>
  fetch(`/api/discovery/projects/${encodeURIComponent(project)}/taste`).then(json<TasteStatus>);

export const getTasteVersion = (project: string, id: number) =>
  fetch(`/api/discovery/projects/${encodeURIComponent(project)}/taste/versions/${id}`)
    .then(json<{ id: number; number: number; source: string; text: string; suggestions?: string | null }>);

/** The owner writes the whole file. Committed in the project folder as their edit. */
export const saveTaste = (project: string, text: string) =>
  fetch(`/api/discovery/projects/${encodeURIComponent(project)}/taste`, { method: 'PUT', headers, body: JSON.stringify({ text }) })
    .then(json<{ number: number }>);

/** Puts an earlier version back, as a new version. */
export const restoreTaste = (project: string, versionId: number) =>
  fetch(`/api/discovery/projects/${encodeURIComponent(project)}/taste/versions/${versionId}/restore`, { method: 'POST' })
    .then(json<{ number: number }>);

/** Asks the next worker run on the project to fold now, below the count - or takes the ask back. */
export const requestFold = (project: string, requested: boolean) =>
  fetch(`/api/discovery/projects/${encodeURIComponent(project)}/taste/fold-request`, { method: 'PUT', headers, body: JSON.stringify({ requested }) })
    .then(json<{ requested: boolean }>);
