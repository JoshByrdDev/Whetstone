/**
 * A/B experiments on a phase's skill or agent settings (docs/bugs-and-experiments-plan.md,
 * section 2). Start, stop and declare are routes no MCP tool reaches - only a person runs one.
 */

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

const post = (url: string, body?: unknown) =>
  fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

export interface Rate { n: number; bad: number; value: number | null }

export interface ArmResult {
  key: 'control' | 'variant'; name: string;
  skillVariant: string | null; model: string | null; effort: string | null; provider: string | null;
  tickets: number; done: number;
  medianMinutes: number | null; medianFreshTokens: number | null; medianCacheRead: number | null;
  qaFailure: Rate; rework: Rate; escapedBug: Rate; reworkShare: number;
}

export interface GuardrailCheck { name: string; p: number; breaks: boolean; passes: boolean }

export interface TicketMeasure {
  ticketId: number; title: string | null; arm: string; ordinal: number; assignedAt: string;
  phase: string; doneAt: string | null; runs: number; workerMinutes: number; freshTokens: number;
  hadQaFailure: boolean; hadRework: boolean; escapedBug: boolean; runsApplied: number;
}

export type Stage = 'enrolling' | 'landing' | 'waiting' | 'ready' | 'stopped' | 'concluded';

export interface ExperimentView {
  id: number; projectKey: string; skill: string; name: string; hypothesis: string | null;
  status: string; stage: Stage; minTicketsPerArm: number; waitDays: number;
  startedAt: string; stoppedAt: string | null; stopKind: string | null; stopReason: string | null;
  concludedAt: string | null; winner: string | null; conclusionNote: string | null;
  lastDoneAt: string | null; verdictFrom: string | null;
  arms: ArmResult[]; checks: GuardrailCheck[]; suggestion: string | null; suggestionWhy: string;
  tickets: TicketMeasure[];
}

export interface ExperimentOptions {
  skills: string[];
  variants: Record<string, string[]>;
  providers: string[];
  efforts: string[];
  roles: Record<string, { provider: string; model: string; effort: string }>;
}

export interface ArmSpec { name?: string; skillVariant?: string; model?: string; effort?: string; provider?: string }

export interface CreateSpec {
  projectKey: string; skill: string; name: string; hypothesis?: string;
  minTicketsPerArm?: number; waitDays?: number; control: ArmSpec; variant: ArmSpec;
}

export const listExperiments = (project: string | null) =>
  fetch(`/api/experiments${project ? `?project=${encodeURIComponent(project)}` : ''}`).then(json<ExperimentView[]>);

export const getExperimentOptions = (project: string) =>
  fetch(`/api/experiments/options?project=${encodeURIComponent(project)}`).then(json<ExperimentOptions>);

export const createExperiment = (spec: CreateSpec) => post('/api/experiments', spec).then(json<ExperimentView>);

export const stopExperiment = (id: number, reason?: string) =>
  post(`/api/experiments/${id}/stop`, { reason }).then(json<ExperimentView>);

export const declareExperiment = (id: number, winner: string, note?: string) =>
  post(`/api/experiments/${id}/declare`, { winner, note }).then(json<ExperimentView>);
