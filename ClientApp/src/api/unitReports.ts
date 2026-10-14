import type { Bounce, Doc, TicketCost, Walkthrough } from './classroom';
import type { Rect } from './classroom';

/**
 * A unit report: one Classroom document about a whole unit of a steward plan, written over its
 * tickets' own analyses and ruled against the unit's outcomes. Read on the Steward page.
 */

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.message) message = body.message;
    } catch { /* not json */ }
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

export interface UnitReportMeta {
  id: number;
  headline: string;
  generatedAt: string;
  baseRef: string;
  fromSha: string;
  toSha: string;
  /** Commits on the base's first-parent line in the range, and how many are this unit's own merges. */
  rangeCommits: number;
  ownCommits: number;
  model?: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  durationMs: number;
}

export interface UnitReportShot { order: number; caption: string; imageUrl: string; focus?: Rect | null }

export interface UnitReportGap { ticketId: number; kind: 'Open' | 'NoAnalysis' | 'StaleAnalysis'; why: string }

export interface UnitReport {
  meta: UnitReportMeta;
  document: Doc;
  /** The report's own copies; `order` is the number the document cites. */
  shots: UnitReportShot[];
  gaps: UnitReportGap[];
  /** Why it no longer describes the unit, in sentences. Empty while it still does. */
  stale: string[];
}

export interface UnitReportTicket {
  id: number;
  title: string;
  phase: string;
  outcomes: string[];
  landedAt?: string | null;
  bounces: Bounce[];
  cost: TicketCost;
}

export interface UnitReportView {
  latest: UnitReport | null;
  history: UnitReportMeta[];
  tickets: UnitReportTicket[];
  repoPath: string;
  ideUrl: string;
  /** Where a report on this unit is in the Classroom's queue, if anywhere. */
  queue: 'queued' | 'running' | null;
  queueNote?: string | null;
  lastError?: string | null;
}

const where = (project: string, plan: string | null | undefined) =>
  `project=${encodeURIComponent(project)}${plan ? `&plan=${encodeURIComponent(plan)}` : ''}`;

export const getUnitReportView = (project: string, plan: string | null | undefined, unit: number) =>
  fetch(`/api/steward/plan/units/${unit}/report?${where(project, plan)}`).then(json<UnitReportView>);

export const getUnitReport = (id: number) => fetch(`/api/unit-reports/${id}`).then(json<UnitReport>);

/** Ask for a report. It runs over an unfinished unit too, and says what is unfinished. */
export const queueUnitReport = (project: string, plan: string | null | undefined, unit: number) =>
  fetch(`/api/steward/plan/units/${unit}/report?${where(project, plan)}`, { method: 'POST' }).then(json<{ message: string }>);

export const cancelUnitReport = (project: string, plan: string | null | undefined, unit: number) =>
  fetch(`/api/steward/plan/units/${unit}/report?${where(project, plan)}`, { method: 'DELETE' }).then(json<{ cancelled: boolean }>);

export const unitReportDiffUrl = (id: number) => `/api/unit-reports/${id}/diff`;

export const getUnitWalkthrough = (id: number) => fetch(`/api/unit-reports/${id}/walkthrough`).then(json<Walkthrough>);

export const writeUnitWalkthrough = (id: number) =>
  fetch(`/api/unit-reports/${id}/walkthrough`, { method: 'POST' }).then(json<{ message: string }>);
