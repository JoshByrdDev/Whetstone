import type {
  Phase, PhaseInfo, ProjectInfo, TicketSummary, TicketDetail,
  WorkItem, QaItem, Comment, QaStatusName, JourneyLeg, Priority,
  Track, ReviewSeverity, ReviewCommentStatus, ReviewVerdict,
} from '../types/tickets';

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.error) msg = body.error;
    } catch { /* non-JSON error body */ }
    throw new Error(msg);
  }
  return res.json() as Promise<T>;
}

const jsonHeaders = { 'Content-Type': 'application/json' };

// ---- Board metadata ----
export const getPhases = () => fetch('/api/phases').then(json<PhaseInfo[]>);
export const getProjects = () =>
  fetch('/api/projects').then(json<{ projects: ProjectInfo[] }>);

// ---- Tickets ----
export function getTickets(project?: string): Promise<TicketSummary[]> {
  const q = project ? `?project=${encodeURIComponent(project)}` : '';
  return fetch(`/api/tickets${q}`).then(json<TicketSummary[]>);
}
export const getTicket = (id: number) => fetch(`/api/tickets/${id}`).then(json<TicketDetail>);

// The ticket's trip through the phases. Separate from getTicket on purpose: that shape is also what
// every worker receives over MCP, and transition history is context none of them has a use for.
export const getTicketJourney = (id: number) =>
  fetch(`/api/tickets/${id}/journey`).then(json<JourneyLeg[]>);

export const createTicket = (body: {
  // title is required only for standalone tickets; a linked ticket pulls it from the tracker.
  projectKey: string; title?: string; description?: string; issueKey?: string; issueUrl?: string; phase?: Phase;
  requiresSpeculationReview?: boolean; requiresImplementationReview?: boolean; masterNotes?: string;
  skipQaTesting?: boolean; priority?: Priority;
  // Absent = the project's default (ProjectInfo.skipClassroomAnalysisByDefault).
  skipClassroomAnalysis?: boolean;
  // Review track only, and settable only here: nothing moves a ticket between tracks afterwards.
  // sourceBranch is required when track is 'Review' — a review ticket with no branch to read has
  // nothing to work on, and the server refuses it rather than failing later in the worker.
  track?: Track; sourceBranch?: string; targetBranch?: string;
  prUrl?: string; prId?: string; prAuthor?: string;
}) => fetch('/api/tickets', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) }).then(json<TicketDetail>);

export const updateTicket = (id: number, body: Partial<{
  title: string; description: string; issueKey: string; issueUrl: string; branch: string; projectKey: string;
  requiresSpeculationReview: boolean; requiresImplementationReview: boolean; masterNotes: string;
  skipQaTesting: boolean; priority: Priority; shortLoopReview: boolean; skipClassroomAnalysis: boolean;
}>) => fetch(`/api/tickets/${id}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(body) }).then(json<TicketDetail>);

export const deleteTicket = (id: number) =>
  fetch(`/api/tickets/${id}`, { method: 'DELETE' }).then(json<{ deleted: boolean }>);

export const transitionTicket = (id: number, toPhase: Phase, reason?: string) =>
  fetch(`/api/tickets/${id}/transition`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ toPhase, reason }) })
    .then(json<TicketDetail>);

export const setOrder = (id: number, boardOrder: number) =>
  fetch(`/api/tickets/${id}/order`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify({ boardOrder }) })
    .then(json<{ updated: boolean }>);

// ---- Work items ----
export const addWorkItem = (ticketId: number, text: string) =>
  fetch(`/api/tickets/${ticketId}/workitems`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ text }) }).then(json<WorkItem>);
export const updateWorkItem = (id: number, body: Partial<{ text: string; done: boolean; order: number }>) =>
  fetch(`/api/workitems/${id}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(body) }).then(json<WorkItem>);
export const deleteWorkItem = (id: number) =>
  fetch(`/api/workitems/${id}`, { method: 'DELETE' }).then(json<{ deleted: boolean }>);

// ---- QA items ----
export const addQaItem = (ticketId: number, text: string) =>
  fetch(`/api/tickets/${ticketId}/qaitems`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ text }) }).then(json<QaItem>);
export const updateQaItem = (id: number, body: Partial<{ text: string; status: QaStatusName; note: string; order: number }>) =>
  fetch(`/api/qaitems/${id}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(body) }).then(json<QaItem>);
export const deleteQaItem = (id: number) =>
  fetch(`/api/qaitems/${id}`, { method: 'DELETE' }).then(json<{ deleted: boolean }>);

// ---- Comments ----
export const addComment = (ticketId: number, body: string, author = 'Human') =>
  fetch(`/api/tickets/${ticketId}/comments`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ body, author }) }).then(json<Comment>);

// ---- Owner notes: the human → worker channel ----
// A note posted here is a DIRECTIVE, not a bulletin-board post: it is injected into the prompt of
// the next run that claims this ticket and holds that run's hand-off until a worker answers it.
// Multipart rather than JSON because the whole point is that it can carry the screenshot or the log
// dump that the words on their own would be describing.
export const addNote = (ticketId: number, body: string, files: File[] = []) => {
  const form = new FormData();
  form.append('body', body);
  files.forEach(f => form.append('files', f, f.name));
  return fetch(`/api/tickets/${ticketId}/notes`, { method: 'POST', body: form }).then(json<Comment>);
};

// The review gate's change request, as one call: the note, its files, the checklist of what to
// change (which becomes work items) and whether the fix comes straight back here. See
// TicketService.RequestChangesAsync for why these are not four calls.
// priority is optional and omitted when unchanged: the server reads its absence as "leave the band
// alone", so a review that had nothing to say about urgency never quietly resets it.
// replan sends it back through speculation and its approval gate instead of straight to
// implementation - for new scope rather than a correction.
export const requestChanges = (ticketId: number, body: {
  body: string; changeItems?: string[]; shortLoop?: boolean; replan?: boolean; files?: File[]; priority?: Priority;
}) => {
  const form = new FormData();
  form.append('body', body.body);
  (body.changeItems ?? []).forEach(i => form.append('changeItems', i));
  form.append('shortLoop', body.shortLoop ? 'true' : 'false');
  if (body.replan) form.append('replan', 'true');
  if (body.priority) form.append('priority', body.priority);
  (body.files ?? []).forEach(f => form.append('files', f, f.name));
  return fetch(`/api/tickets/${ticketId}/request-changes`, { method: 'POST', body: form }).then(json<TicketDetail>);
};

// The New Ticket form's key picker: issues assigned to me that no local ticket has claimed
// yet. configured=false means this project has no tracker credentials — show no picker, not an error.
export type UnlinkedIssue = { key: string; summary: string | null; status: string | null };
// `tracker` is the prose name of whichever system answered ("Jira", "Azure Boards"), so the form
// can label its own field without guessing which one this project is on.
export const getUnlinkedIssues = (project: string) =>
  fetch(`/api/issues/unlinked?project=${encodeURIComponent(project)}`)
    .then(json<{ configured: boolean; tracker: string; issues: UnlinkedIssue[] }>);

// ---- Dependencies (what a ticket is waiting on) ----
// A blocker may be added here even when the tracker has no such link: a dependency found while doing the
// work is real, and the local verdict outranks the tracker in both directions.
export const addDependency = (ticketId: number, body: { blockerIssueKey?: string; blockerTicketId?: number; note?: string }) =>
  fetch(`/api/tickets/${ticketId}/dependencies`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
    .then(json<{ id: number }>);
// override: false = dismissed, true = counts regardless of the tracker, null = follow it again.
export const setDependencyOverride = (depId: number, override: boolean | null, note?: string) =>
  fetch(`/api/dependencies/${depId}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify({ override, note }) })
    .then(json<{ updated: boolean }>);
export const deleteDependency = (depId: number) =>
  fetch(`/api/dependencies/${depId}`, { method: 'DELETE' }).then(json<{ deleted: boolean }>);

// ---- Peer review: staging, curating, and sending the feedback ----
//
// Note what is here and what is not. Everything below writes to the LOCAL ticket, with one
// exception - markReviewSubmitted - which records that the owner sent the feedback themselves.
// There is no endpoint anywhere that posts to a pull request, and no MCP tool that reaches even
// these: a run stages drafts and stops. That asymmetry is the whole safety story of this track.
export const addReviewComment = (ticketId: number, body: {
  body: string; filePath?: string; line?: number; side?: 'Head' | 'Base'; severity?: ReviewSeverity;
}) =>
  fetch(`/api/tickets/${ticketId}/review/comments`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
    .then(json<{ id: number }>);

// status here is the curation decision - 'Approved' to keep, 'Dismissed' to drop. 'Submitted' is
// refused: it is recorded by sending, never chosen, or the board would claim feedback reached the
// pull request when nothing had left this machine.
export const updateReviewComment = (ticketId: number, commentId: number, body: Partial<{
  body: string; severity: ReviewSeverity; status: Exclude<ReviewCommentStatus, 'Submitted'>;
}>) =>
  fetch(`/api/tickets/${ticketId}/review/comments/${commentId}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(body) })
    .then(json<{ id: number; status: ReviewCommentStatus }>);

export const deleteReviewComment = (ticketId: number, commentId: number) =>
  fetch(`/api/tickets/${ticketId}/review/comments/${commentId}`, { method: 'DELETE' })
    .then(r => { if (!r.ok) throw new Error(`${r.status} ${r.statusText}`); });

export const setReviewVerdict = (ticketId: number, verdict: ReviewVerdict, note?: string) =>
  fetch(`/api/tickets/${ticketId}/review/verdict`, { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ verdict, note }) })
    .then(json<{ verdict: ReviewVerdict; verdictAt: string }>);

// The whole review as markdown, ready to paste into the pull request. This is how v1 sends
// feedback: no credentials, nothing written to anyone's repository, and no way to post the wrong
// thing to the wrong place.
export const getReviewMarkdown = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/review/markdown`).then(json<{ markdown: string }>);

// Records that the owner has sent it. Marks every pending comment Submitted and stamps the target,
// which is what flips the gate from "waiting on you" to "waiting on them" - the reason this track
// needs no phase of its own for that.
export const markReviewSubmitted = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/review/submit`, { method: 'POST' })
    .then(json<{ submittedAt: string }>);
