/**
 * The Classroom API — on-demand teaching analyses of a ticket.
 *
 * Read-only over the pipeline: nothing here moves a ticket, comments on one, or writes anything a
 * worker later reads. Generating is a POST only because it starts a process.
 */

import type { Comment, JourneyLeg, Phase, Track } from '../types/tickets';

export type { JourneyLeg };

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.message) msg = body.message;
      else if (body?.error) msg = body.error;
    } catch { /* non-JSON error body */ }
    throw new Error(msg);
  }
  return res.json() as Promise<T>;
}

const jsonHeaders = { 'Content-Type': 'application/json' };

export type Lens = 'Full' | 'Decisions' | 'Code' | 'Testing';

export const LENSES: { value: Lens; label: string; hint: string }[] = [
  { value: 'Full', label: 'Full', hint: 'Everything, evenly — the right first read' },
  { value: 'Decisions', label: 'Decisions', hint: 'What was chosen, what was rejected, and why' },
  { value: 'Code', label: 'Code', hint: 'What changed, and the route to read it in' },
  { value: 'Testing', label: 'Testing', hint: 'What QA proved, and what it did not' },
];

export type FlowChange = 'new' | 'changed' | 'unchanged' | 'removed';

/**
 * One stop on the user's route. No coordinates — the model says what the steps are, the renderer
 * decides where they go.
 *
 * `ref` and `detail` are also where a stop grows later: a node that already knows which file it
 * belongs to is one hover away from showing the snippet, or the QA screenshot, in place.
 */
export interface FlowNode {
  id: string;
  /** A header, 2-5 words. The substance is in `explain`, one click away. */
  label: string;
  kind: 'start' | 'screen' | 'step' | 'decision' | 'system' | 'end';
  /** What actually happens here — shown when the reader opens the stop. */
  explain?: string;
  /** The role taking this step. Only set when the flow crosses more than one. */
  actor?: string;
  change: FlowChange;
  /** Up to three `file:line` citations. Each carries its own diff into the opened stop. */
  refs?: string[];
  /** `order` of the QA screenshot that is a picture of this stop, drawn on the stop itself and
   *  never hidden behind the click that opens it. Resolved against the ticket's live shots at
   *  render time under the same rule as a coverage row's: a number that no longer names one shows
   *  no picture rather than a broken image. */
  shot?: number | null;
  /** The requirement lines that called for this stop, in the ticket's own words — joined on by the
   *  server from the ids the run named, never written by it. Absent on analyses written before. */
  asks?: FlowAsk[];
  /** Legacy single citation, on analyses written before `refs`. Read via `nodeRefs`. */
  ref?: string;
}

/** One quoted criterion on a stop. `origin` is the ancestor's key when the line was inherited. */
export interface FlowAsk { text: string; origin?: string | null; originRole?: string | null }

/** A node's citations, whichever shape the analysis was written in. */
export const nodeRefs = (n: FlowNode): string[] =>
  n.refs?.length ? n.refs : n.ref ? [n.ref] : [];

export interface FlowEdge { from: string; to: string; label?: string; change: FlowChange }

/**
 * What the change did about one line of the ticket.
 *
 * `unaddressed` is the one the server writes rather than the model: it means the run returned no
 * ruling for this line at all. It exists so that a requirement the analysis skipped shows up as a
 * requirement the analysis skipped, instead of vanishing from a list nobody can check.
 */
export type Verdict = 'met' | 'partial' | 'gap' | 'deferred' | 'unknown' | 'context' | 'unaddressed';

/** A snippet quoted onto one coverage row. `startLine` is absent where those lines are no longer in
 *  the file — the renderer then draws no line numbers rather than counting from one. */
export interface CoverageSnippet {
  text: string;
  path?: string | null;
  startLine?: number | null;
  language?: string | null;
}

/**
 * One line of the ticket, with its verdict. `text` is the ticket's OWN wording, cut up server-side
 * and never rewritten by the model — the reader is meant to recognise their own words, not audit a
 * summary of them.
 */
export interface CoverageSegment {
  id: string;
  /** `heading` and `code` are the description's furniture: carried for shape, never ruled on. */
  kind: 'heading' | 'para' | 'item' | 'row' | 'code';
  text: string;
  /** List nesting, 0-3. */
  depth: number;
  /** The ancestor this was inherited from, when it was not written on this ticket. */
  origin?: string | null;
  /** How that ancestor relates — "epic", "story", else "parent". */
  originRole?: string | null;
  verdict: Verdict;
  /** One sentence: what was done about this. */
  evidence?: string | null;
  /** 1-3 `file:line`. A `met` verdict cannot reach here without them. */
  refs?: string[] | null;
  /** The QA item that exercised this line, where one did. */
  qa?: string | null;
  /** `order` of the QA screenshot showing this line working. Resolved against the ticket's live
   *  shots at render time, so a number that no longer names one shows no picture rather than a
   *  broken image. Chosen by the run, never matched by caption similarity — a screenshot of the
   *  wrong screen beside a requirement is worse than none. */
  shot?: number | null;
  /** The few lines that ARE the answer to this requirement. Same shape and same rules as a `code`
   *  block at a sixth of the length — rationed to at most two across a whole map, because a snippet
   *  settles the question in place and one on every row settles nothing. */
  snippet?: CoverageSnippet | null;
  /** Where this ask actually went — a sibling key, or the comment that descoped it. */
  deferredTo?: string | null;
}

/** Everything a verdict is not settled by. What the "unresolved only" filter keeps. */
export const UNSETTLED: ReadonlySet<Verdict> = new Set<Verdict>(['partial', 'gap', 'unknown', 'unaddressed']);

/** One typed block. Flat by design — see AnalysisDocument on the server. */
export interface Block {
  kind: 'prose' | 'keyValue' | 'comparison' | 'readingPath' | 'glossary' | 'callout' | 'question' | 'code' | 'userFlow' | 'coverage';
  text?: string;
  title?: string;
  tone?: 'gotcha' | 'insight' | 'risk';
  note?: string;
  // code only
  path?: string;
  startLine?: number;
  language?: string;
  columns?: string[];
  rows?: string[][];
  items?: { key: string; value: string }[];
  terms?: { term: string; meaning: string }[];
  steps?: { location: string; what: string; why?: string }[];
  // userFlow only
  nodes?: FlowNode[];
  edges?: FlowEdge[];
  // coverage only — already joined against the ticket's text server-side, so this is self-contained.
  segments?: CoverageSegment[];
}

/** Whether a section is the coverage map — the one the renderer lifts above the numbered spine. */
export const isCoverage = (s: Section) => s.blocks.some(b => b.kind === 'coverage');

export interface Section { title: string; blocks: Block[] }
export interface Doc { headline: string; sections: Section[] }

export interface AnalysisMeta {
  id: number;
  lens: Lens;
  phaseAtGeneration: string;
  headline: string;
  headSha: string | null;
  branch: string | null;
  generatedAt: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  durationMs: number;
  model: string | null;
  /** The ticket has moved phase or gained commits since this was written. */
  stale: boolean;
  /** Whether the run drew the user's path. False is legitimate on work no user can see — and worth
   *  saying out loud, because an omitted section is otherwise indistinguishable from a skipped one. */
  hasUserFlow: boolean;
  /** The ticket has been re-worded since this was read, so the coverage map rules on words that are
   *  no longer there. Distinct from `stale`, which says the work moved on rather than the ask. */
  descriptionChanged: boolean;
}

export interface Analysis { meta: AnalysisMeta; document: Doc }

export interface StudiableTicket {
  id: number;
  projectKey: string;
  title: string;
  phase: string;
  branch: string | null;
  /** The read-only tracker link, when the ticket has one — usually how you actually refer to it. */
  issueKey: string | null;
  analysisCount: number;
  latestAnalysisAt: string | null;
  updatedAt: string;
}

export interface TicketCost {
  runs: number; inputTokens: number; outputTokens: number;
  cacheReadTokens: number; cacheCreationTokens: number; minutes: number;
}

export interface ClassroomTicket {
  id: number;
  projectKey: string;
  title: string;
  /** The linked tracker issue, when there is one. */
  issueKey?: string | null;
  phase: string;
  /** Which set of phases this ticket's trip could ever have visited. */
  track: Track;
  branch: string | null;
  worktreePath: string | null;
  eligible: boolean;
  reason: string | null;
  headSha: string | null;
  learningLabel: string;
  /** The hub checkout — where an editor link should open, not the agent's worktree. */
  repoPath: string;
  /** Template with {path} and {line}. Defaults to VS Code's scheme; set `ideUrl` in project.json. */
  ideUrl: string;
  // The pipeline's own record of the work, shown alongside the analysis rather than restated by it.
  implementationSummary: string | null;
  qaShots: { order: number; caption: string; imageUrl: string; focus?: { x: number; y: number; w: number; h: number } | null }[];
  /** Why there are no screenshots, when QA said so explicitly. */
  qaShotsWaiver: string | null;
  analyses: AnalysisMeta[];
  journey: JourneyLeg[];
  cost: TicketCost;
  /** Every backward move, with the notes written around it. Empty when the trip never went back. */
  bounces: Bounce[];
}

/**
 * One trip backwards, and why. The notes are picked by time server-side: `why` is what was written
 * while the ticket sat in the phase it bounced out of (plus any owner note left before the rework
 * was picked up), and `reply` is the rework run's first note.
 */
export interface Bounce {
  /** Index into `journey`. */
  leg: number;
  from: Phase;
  to: Phase;
  at: string;
  actor: string;
  reason: string | null;
  why: Comment[];
  reply: Comment | null;
}

/** One analysis waiting its turn. `id` is stable from queueing to outcome. */
export interface QueueItem {
  id: number;
  ticketId: number;
  lens: Lens;
  queuedAt: string;
  /**
   * Why this one was stepped over on the last pass — a heartbeat agent is in its worktree. Transient
   * and self-clearing: the item keeps its place and the queue looks again shortly.
   */
  holdReason: string | null;
  /** 'walkthrough' for an item rewriting an existing analysis's walkthrough rather than writing a new analysis. */
  kind?: 'analysis' | 'walkthrough';
  walkthroughOf?: number | null;
}

/**
 * A run that has finished, however it finished. A list rather than a single "last run" because with
 * a queue two items can fail while you are reading a third, and one slot only ever showed you one
 * of them — for a ticket you may not have been looking at.
 */
export interface RunOutcome {
  id: number;
  ticketId: number;
  lens: Lens;
  finishedAt: string;
  analysisId: number | null;
  error: string | null;
}

export interface RunStatus {
  running: boolean;
  ticketId: number | null;
  lens: Lens | null;
  /** What the running item is. A new analysis also writes its walkthrough before the slot frees. */
  kind?: 'analysis' | 'walkthrough' | null;
  walkthroughOf?: number | null;
  startedAt: string | null;
  elapsedSeconds: number | null;
  /** Tool calls the running analysis has made so far. */
  steps: number;
  /** The most recent one, in words — "Read AnalysisQueue.cs". Null before the run's first move. */
  activity: string | null;
  /**
   * What runs like this one have taken, from the ones before it. Null on the first run of a lens,
   * and that absence is the point: with no denominator the panel says so rather than drawing a bar
   * against a number it made up.
   */
  expectedSeconds: number | null;
  /** The same, in tool calls. */
  expectedSteps: number | null;
  /** In turn order. */
  pending: QueueItem[];
  /** Newest first, capped server-side. */
  recent: RunOutcome[];
}

export function getStudiableTickets(project?: string): Promise<StudiableTicket[]> {
  const q = project ? `?project=${encodeURIComponent(project)}` : '';
  return fetch(`/api/classroom/tickets${q}`).then(json<StudiableTicket[]>);
}

export const getClassroomTicket = (id: number) =>
  fetch(`/api/classroom/tickets/${id}`).then(json<ClassroomTicket>);

export const getAnalysis = (id: number) =>
  fetch(`/api/classroom/analyses/${id}`).then(json<Analysis>);

/**
 * Join the queue. Returns as soon as the item is in the line — it may start now or in ten minutes,
 * and nothing about the ticket is checked here. That check happens when its turn comes, because a
 * ticket can move phase, gain commits or be claimed by a heartbeat agent while it waits.
 */
export const queueAnalysis = (ticketId: number, lens: Lens) =>
  fetch(`/api/classroom/tickets/${ticketId}/generate`, {
    method: 'POST', headers: jsonHeaders, body: JSON.stringify({ lens }),
  }).then(json<{ message: string }>);

/** Drop one item, waiting or running — the pair identifies it either way. */
export const cancelQueued = (ticketId: number, lens: Lens) =>
  fetch(`/api/classroom/queue/${ticketId}/${lens}`, { method: 'DELETE' })
    .then(json<{ cancelled: boolean }>);

/** Empty the waiting line, leaving whatever is running alone. */
export const clearQueue = () =>
  fetch('/api/classroom/queue', { method: 'DELETE' }).then(json<{ cleared: number }>);

export const getRunStatus = () => fetch('/api/classroom/status').then(json<RunStatus>);

/** One changed file, as the diff behind a stop on a user-flow diagram. */
export interface NodeDiff {
  path: string;
  patch: string;
  added: number;
  removed: number;
  truncated: boolean;
  error: string | null;
}

/** One file's diff, from a document's diff route (an analysis's, or a unit report's). */
export const getNodeDiff = (diffUrl: string, path: string) =>
  fetch(`${diffUrl}?path=${encodeURIComponent(path)}`).then(json<NodeDiff>);

export const deleteAnalysis = (id: number) =>
  fetch(`/api/classroom/analyses/${id}`, { method: 'DELETE' }).then(json<{ deleted: boolean }>);

// ---- walkthrough ---------------------------------------------------------------------------------

/** A rectangle in an image's own pixels. */
export interface Rect { x: number; y: number; w: number; h: number }

/** One line of code on a slide, with the marker git gave it: "+" added, "-" removed, " " already there. */
export interface DeckLine { m: '+' | '-' | ' '; n: number | null; o: number | null; t: string }

export interface DeckCode {
  path: string;
  language: string | null;
  added: number;
  removed: number;
  /** Hold the added lines back until a beat reveals them. */
  reveal: boolean;
  lines: DeckLine[];
}

export interface DeckImage { name: string; url: string; w: number; h: number }

export interface DeckCue {
  /** [first, last] new-side line numbers to light. */
  lines?: number[] | null;
  nodes?: string[] | null;
  quotes?: number[] | null;
  image?: string | null;
  zoom?: Rect | null;
  box?: Rect | null;
  reveal?: boolean | null;
  show?: number | null;
  /** On a picture scene: the whole picture from this beat on. Absent holds the last one. */
  picture?: Picture | null;
  /** Picture item ids pointed at on this beat. */
  lit?: string[] | null;
}

/** The flow diagram's words for a change, so a picture and a diagram never disagree on a colour. */
export type PictureChange = FlowChange;

/**
 * A small diagram described as what is on screen, not as how it moves. Ids persist between beats and
 * the player animates the difference - see WalkthroughDeck.Picture on the server.
 */
export interface Picture {
  layout: 'row' | 'column';
  items: PictureItem[];
  arrows: PictureArrow[];
}

export interface PictureItem {
  id: string;
  kind: 'box' | 'group' | 'text' | 'code';
  text: string;
  sub?: string | null;
  change: PictureChange;
  /** The group it sits inside. */
  in?: string | null;
  /** On a group: how its children run. */
  layout?: 'row' | 'column' | null;
}

export interface PictureArrow { from: string; to: string; label?: string | null; change: PictureChange }

export interface DeckBeat {
  say: string;
  cue?: DeckCue | null;
  spoken?: string | null;
  /** The voiced line; null when it could not be voiced and plays as a caption only. */
  clip?: string | null;
  /** Seconds. */
  dur: number;
}

export type SceneKind = 'title' | 'ask' | 'shot' | 'code' | 'flow' | 'cards' | 'question' | 'picture';

export interface DeckScene {
  chapter: string;
  kind: SceneKind;
  beats: DeckBeat[];
  shot?: number | null;
  /** An analysis block as "section.block". */
  block?: string | null;
  blocks?: string[] | null;
  quotes?: string[] | null;
  images?: DeckImage[] | null;
  code?: DeckCode | null;
}

export interface Deck { voice: string; scenes: DeckScene[] }

export interface Walkthrough {
  /** The latest attempt's. The last good deck survives a retry, so a deck can sit beside "Pending" or "Failed". */
  status: 'none' | 'Pending' | 'Ready' | 'Failed';
  /** Why the latest attempt failed; on a Ready deck, a warning (lines left unvoiced). */
  error: string | null;
  deck: Deck | null;
  audioSeconds: number;
  /** Pointers the checks threw out. Zero is the goal. */
  dropped: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  model: string | null;
  durationMs: number;
  generatedAt: string | null;
}

export const getWalkthrough = (analysisId: number) =>
  fetch(`/api/classroom/analyses/${analysisId}/walkthrough`).then(json<Walkthrough>);

/** Queue a rewrite. New analyses get theirs on their own; this is Retry, and the backfill for older ones. */
export const writeWalkthrough = (analysisId: number) =>
  fetch(`/api/classroom/analyses/${analysisId}/walkthrough`, { method: 'POST' }).then(json<{ message: string }>);

export const clipUrl = (clip: string) => `/api/walkthrough-clips/${clip}.wav`;

/** Whether this machine can voice walkthroughs: `unavailable` is why not, or null when it can. */
export interface NarratorStatus { unavailable: string | null; dir: string }

// One answer per page load: it only changes when someone runs `npm install`, and a reload picks that up.
let narratorStatus: Promise<NarratorStatus> | null = null;
export const getNarratorStatus = () =>
  (narratorStatus ??= fetch('/api/narrator').then(json<NarratorStatus>).catch(e => { narratorStatus = null; throw e; }));

/** An analysis block by its "section.block" address, or null. */
export function blockAt(doc: Doc, address: string | null | undefined): Block | null {
  const m = /^(\d+)\.(\d+)$/.exec(address ?? '');
  if (!m) return null;
  return doc.sections[Number(m[1])]?.blocks[Number(m[2])] ?? null;
}
