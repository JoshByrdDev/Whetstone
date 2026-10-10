// Git actions the human takes on a ticket from the board. Everything is ticket-scoped: the branch
// is whatever the ticket records, so the browser never gets to name one.

import type { RepoType } from '../types/tickets';

export interface GitChange {
  status: string;
  path: string;
}

export interface CheckoutResult {
  ok: boolean;
  error?: string | null;
  branch: string;
  repoPath: string;
  /** Taken with --ignore-other-worktrees: an agent worktree holds this branch too. */
  forced: boolean;
  sharedWith?: string | null;
  createdFromRemote: boolean;
  /** Populated only on the refusal that names uncommitted work as the reason. */
  pending: GitChange[];
}

export interface PrCommit {
  sha: string;
  subject: string;
  author: string;
  date: string;
}

/** Major = read this, Normal = glance, Minor = mechanical or generated. Ranked server-side. */
export type DiffTier = 'Major' | 'Normal' | 'Minor';

export interface PrFile {
  path: string;
  added: number;
  removed: number;
  binary: boolean;
  tier: DiffTier;
  score: number;
  /** What earned the tier — "migration", "query changed", "list entries", "generated". */
  reasons: string[];
  /** This file's hunks alone. Empty when patchOmitted. */
  patch: string;
  /** The diff was too large to send in full and this file's body lost the draw. */
  patchOmitted: boolean;
  renamedFrom?: string | null;
  /** From `git diff --name-status`. A TFVC check-in reads it as instructions: only edits ride along by themselves. */
  status: FileChangeStatus;
}

export type FileChangeStatus = 'added' | 'deleted' | 'renamed' | 'modified';

export interface PrPreflight {
  ok: boolean;
  error?: string | null;
  branch: string;
  baseBranch: string;
  repoPath: string;
  /** Editor URL template with {path} and {line} — from the project's `ideUrl`, VS Code by default. */
  ideUrl: string;
  provider: 'github' | 'azure-devops' | 'other' | 'none';
  remoteUrl?: string | null;
  /**
   * Whether the base comparison has been made at all. False on the local-only read the dialog opens
   * with: every count below is then a last-known value carried so there is something to draw, and
   * not an answer to "is this branch fit to raise". Nothing may act on them until the base check
   * lands and turns this true.
   */
  baseChecked: boolean;
  /** False means every count below was computed against a local ref, not the server's. */
  fetched: boolean;
  fetchError?: string | null;
  baseRef: string;
  /** Commits on the base this branch is missing. Zero is "properly based off the latest". */
  behind: number;
  ahead: number;
  remoteBranchExists: boolean;
  remoteUpToDate: boolean;
  /** Commits on origin/<branch> this branch does not have. Non-zero means diverged — a rebase did
   *  this, and only a force push will move the remote off it. */
  remoteAhead: number;
  /** Uncommitted files sitting in the agent's worktree — they will not be in the PR. */
  worktreeDirtyFiles: number;
  worktreePath?: string | null;
  commits: PrCommit[];
  /** Already ranked: Major first, then by score. */
  files: PrFile[];
  /** At least one file's hunks were dropped for size — always the least significant ones. */
  patchTruncated: boolean;
  upToDateWithBase: boolean;
}

export interface PrPreflightResponse {
  /** Which dialog this belongs in: "git" raises a PR, "tfvc" checks in. */
  repoType: RepoType;
  /** The project delivers by merging into its base instead of raising a PR - see ProjectConfig.MergeInsteadOfPr. */
  mergeInsteadOfPr?: boolean;
  preflight: PrPreflight;
  suggestedTitle: string;
  suggestedDescription: string;
}

/**
 * The half of the preflight that had to go to the remote. Every field here overwrites the
 * same-named provisional one on the preflight — see `withBase` in PrModal.
 */
export interface PrBaseCheck {
  ok: boolean;
  error?: string | null;
  fetched: boolean;
  fetchError?: string | null;
  baseRef: string;
  behind: number;
  ahead: number;
  remoteBranchExists: boolean;
  remoteUpToDate: boolean;
  remoteAhead: number;
}

export interface PrCreateResult {
  ok: boolean;
  error?: string | null;
  pushed: boolean;
  url?: string | null;
  /** "gh" — the PR exists. "web" — pushed, and url is the prefilled form to finish on. */
  mode: 'gh' | 'web' | 'none';
  output: string[];
}

/**
 * A ticket's branch merged into the base in the hub, on a project that does not raise PRs. Local
 * only - nothing is pushed; aheadOfOrigin is what a push of the base would now publish.
 */
export interface MergeResult {
  ok: boolean;
  error?: string | null;
  /** The merge conflicted and was aborted. The base is exactly where it was. */
  conflicted: boolean;
  conflictedFiles: string[];
  branch: string;
  baseBranch: string;
  repoPath: string;
  mergeSha?: string | null;
  /** The local base trailed origin and was fast-forwarded before merging. */
  fastForwardedBase: boolean;
  aheadOfOrigin: number;
  hasOrigin: boolean;
  output: string[];
  movedToDone?: boolean;
  phaseNote?: string | null;
}

export interface RebaseResult {
  ok: boolean;
  error?: string | null;
  /** The rebase hit conflicts and was rolled back. The branch is exactly where it was. */
  conflicted: boolean;
  conflictedFiles: string[];
  branch: string;
  baseRef: string;
  /** Where the branch pointed before — a reset to this undoes the rebase. */
  previousSha?: string | null;
  newSha?: string | null;
  /** The tree it ran in: the hub checkout, or the agent worktree holding the branch. */
  ranIn: string;
  output: string[];
}

export interface ForcePushResult {
  ok: boolean;
  error?: string | null;
  /** origin moved since the last fetch, so the lease refused. Nothing was overwritten. */
  stale: boolean;
  branch: string;
  sha?: string | null;
  output: string[];
}

/**
 * A refusal here is a structured answer, not an exception: the 409 body carries the list of
 * uncommitted files that caused it, and that list is the whole message the user needs.
 */
export async function checkoutTicket(id: number): Promise<CheckoutResult> {
  const res = await fetch(`/api/tickets/${id}/git/checkout`, { method: 'POST' });
  if (res.status === 404) throw new Error('Ticket not found.');
  const body = await res.json().catch(() => null);
  if (!body) throw new Error(`${res.status} ${res.statusText}`);
  return body as CheckoutResult;
}

/**
 * The branch itself: commits, ranked diff, the text to raise it with. Local reads only, so this is
 * back in well under a second and the dialog draws on it.
 */
export async function getPrPreflight(id: number): Promise<PrPreflightResponse> {
  const res = await fetch(`/api/tickets/${id}/git/pr`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json();
}

/** The base comparison, which fetches from origin. Slow, and asked for alongside the above. */
export async function getPrBaseCheck(id: number): Promise<PrBaseCheck> {
  const res = await fetch(`/api/tickets/${id}/git/pr/base`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json();
}

/**
 * Both of these answer 409 on refusal with a body worth reading — a conflicted rebase names the
 * files, a refused force push says origin moved — so the status is never the message.
 */
export async function rebaseTicket(id: number): Promise<RebaseResult> {
  return post<RebaseResult>(`/api/tickets/${id}/git/rebase`);
}

export async function forcePushTicket(id: number): Promise<ForcePushResult> {
  return post<ForcePushResult>(`/api/tickets/${id}/git/force-push`);
}

async function post<T>(url: string): Promise<T> {
  const res = await fetch(url, { method: 'POST' });
  if (res.status === 404) throw new Error('Ticket not found.');
  const body = await res.json().catch(() => null);
  if (!body) throw new Error(`${res.status} ${res.statusText}`);
  return body as T;
}

// ---- TFVC check-in ----
// A tfvc project has no remote and no pull request: the ticket's branch is merged into the hub
// without committing, a human checks it in from Team Explorer, and the merge is committed to git
// once TFVC confirms nothing is left pending. See TicketGitService.Checkin.cs.

/**
 * What TFVC says about a file. "detected" is the one that matters: a change the workspace noticed
 * on disk that a check-in will NOT carry until someone promotes it in Team Explorer.
 */
export type TfvcState = 'pending' | 'detected' | 'clean' | 'unknown' | 'not-in-tfvc';

export interface CheckinFile {
  path: string;
  status: FileChangeStatus;
  renamedFrom?: string | null;
  /** Under one of the folders TFVC maps. False is a git-only file, never a check-in item. */
  inTfvc: boolean;
  renamedFromInTfvc: boolean;
  tfvc: TfvcState;
  /** tf's own word for it — "edit", "add", "delete", "rename, edit". */
  tfvcChange?: string | null;
  /** A rename's old half, which TFVC tracks as a separate change. */
  renamedFromTfvc?: TfvcState | null;
  renamedFromTfvcChange?: string | null;
  /**
   * Once staged, where the disk disagrees with the merge: "missing" (the merge has it, the disk does
   * not) or "present" (the merge deletes it, the disk still has it). TFVC checks in the disk and says
   * nothing about a file it never saw, so this is what catches a new file that silently went away.
   */
  disk?: DiskMismatch | null;
  renamedFromDisk?: DiskMismatch | null;
}

export type DiskMismatch = 'missing' | 'present';

export interface CheckinStatus {
  ok: boolean;
  error?: string | null;
  branch: string;
  baseBranch: string;
  repoPath: string;
  hubBranch: string;
  onBase: boolean;
  /** "this" — this ticket's merge is staged in the hub; "other" — some other merge is. */
  merge: 'none' | 'this' | 'other';
  mergeHead?: string | null;
  /** Commits the branch gained after its merge was staged. They are not in this check-in. */
  stagedBehind: number;
  /** Tracked changes outside the staged merge — before staging these block it. */
  dirty: GitChange[];
  unmerged: string[];
  files: CheckinFile[];
  /** tf was asked. It only is once the merge is staged. */
  tfvcChecked: boolean;
  tfvcError?: string | null;
  tfvcProblems: string[];
  tfPath?: string | null;
  tfvcFolders: string[];
  /** The project key, for the project-level Sync with TFVC. */
  project: string;
}

export interface CheckinStepResult {
  ok: boolean;
  error?: string | null;
  conflicted?: boolean;
  conflictedFiles?: string[];
  /** The tracked files whose changes made staging refuse. */
  blocking?: GitChange[];
  output?: string[];
}

export interface CheckinRecordResult {
  ok: boolean;
  error?: string | null;
  /** One sentence per file TFVC still holds a change for — why it was refused. */
  notCheckedIn?: string[];
  commitSha?: string | null;
  commitSubject?: string | null;
  /** Tracked files still differing from main after the commit — a Get Latest's landings. */
  leftUnstaged?: GitChange[];
  /** The ticket's own files among those: on the server, but not in this commit. */
  ticketFilesDiffer?: string[];
  output?: string[];
  movedToDone?: boolean;
  phaseNote?: string | null;
}

export async function getCheckin(id: number): Promise<CheckinStatus> {
  const res = await fetch(`/api/tickets/${id}/git/checkin`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json();
}

/** Both answer 409 with a body worth reading: the blocking files, or the conflicts that were rolled back. */
export const stageCheckin = (id: number) => post<CheckinStepResult>(`/api/tickets/${id}/git/checkin/stage`);
export const abortCheckin = (id: number) => post<CheckinStepResult>(`/api/tickets/${id}/git/checkin/abort`);

/** A refusal names every file TFVC still holds something for; that list is the whole message. */
export async function recordCheckin(id: number, changeset: number): Promise<CheckinRecordResult> {
  const res = await fetch(`/api/tickets/${id}/git/checkin/record`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ changeset }),
  });
  if (res.status === 404) throw new Error('Ticket not found.');
  const body = await res.json().catch(() => null);
  if (!body) throw new Error(`${res.status} ${res.statusText}`);
  return body as CheckinRecordResult;
}

// ---- Sync with TFVC ----
// After a Get Latest, other people's check-ins sit in the hub as uncommitted changes. A sync commits
// the ones TFVC reports as the server's and leaves the human's own work alone.

/** "tfvc" is the only source a sync commits; everything else is left where it is. */
export type SyncSource = 'tfvc' | 'pending' | 'detected' | 'git-only' | 'unknown';

export interface SyncFile {
  path: string;
  git: 'modified' | 'deleted' | 'added';
  source: SyncSource;
  tfvcChange?: string | null;
}

export interface TfvcSyncPreview {
  ok: boolean;
  error?: string | null;
  project: string;
  repoPath: string;
  baseBranch: string;
  files: SyncFile[];
  tfvcChecked: boolean;
  tfvcError?: string | null;
  tfvcProblems: string[];
  suggestedMessage: string;
}

export interface TfvcSyncResult {
  ok: boolean;
  error?: string | null;
  /** The hub moved since the list was read. Nothing was committed; read it again. */
  changed?: boolean;
  commitSha?: string | null;
  commitSubject?: string | null;
  committed?: number;
  output?: string[];
}

export async function getTfvcSync(project: string): Promise<TfvcSyncPreview> {
  const res = await fetch(`/api/projects/${encodeURIComponent(project)}/tfvc/sync`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json();
}

/** Names the files it was shown; the server refuses unless they are still exactly what it would commit. */
export async function runTfvcSync(project: string, paths: string[], message: string): Promise<TfvcSyncResult> {
  const res = await fetch(`/api/projects/${encodeURIComponent(project)}/tfvc/sync`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ paths, message }),
  });
  const body = await res.json().catch(() => null);
  if (!body) throw new Error(`${res.status} ${res.statusText}`);
  return body as TfvcSyncResult;
}

export async function createPr(id: number, title: string, description: string): Promise<PrCreateResult> {
  const res = await fetch(`/api/tickets/${id}/git/pr`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title, description }),
  });
  const body = await res.json().catch(() => null);
  if (!body) throw new Error(`${res.status} ${res.statusText}`);
  return body as PrCreateResult;
}

/** Answers 409 on refusal with a body worth reading - a conflicted merge names the files. */
export async function mergeTicket(id: number, title: string, description: string): Promise<MergeResult> {
  const res = await fetch(`/api/tickets/${id}/git/merge`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title, description }),
  });
  if (res.status === 404) throw new Error('Ticket not found.');
  const body = await res.json().catch(() => null);
  if (!body) throw new Error(`${res.status} ${res.statusText}`);
  return body as MergeResult;
}
