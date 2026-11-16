/** Intake (docs/intake-plan.md): a project's production reports filed as tickets. The owner's routes only. */

export interface IntakeSourceView {
  name: string; kind: 'sqs' | 'standin'; queueUrl: string; region: string; pollSeconds: number;
  lastPollAt: string | null; lastOkAt: string | null; lastError: string | null;
  waiting: number | null; inFlight: number | null; backingUp: boolean; lastReceivedAt: string | null;
}

export interface IntakeIssueView {
  id: number; channel: string; fingerprint: string; title: string; occurrences: number; reportedCount: number | null;
  firstSeen: string; lastSeen: string; lastState: string; muted: boolean; mutedAt: string | null; heldIn: string | null;
  ticketId: number | null; ticketHistory: string | null;
  ticket: { id: number; title: string | null; phase: string } | null;
}

export interface IntakeMessageView {
  id: number; source: string; messageId: string; handledAt: string; outcome: string; adapter: string | null;
  note: string | null; ticketId: number | null; issueId: number | null;
}

export interface IntakeUnrecognisedView {
  id: number; source: string; messageId: string; handledAt: string; note: string | null; rawBody: string | null; rawAttributes: string | null;
}

export interface IntakePage {
  enabled: boolean; on: boolean; problems: string[];
  limits: { newPerHour: number; newPerDay: number }; runbook: string | null;
  sources: IntakeSourceView[];
  counts: {
    days: number; received: number; filed: number; noted: number; held: number; muted: number; ignored: number; tests: number;
    unrecognised: number; repeatRatio: number | null; mutedIssues: number; storms: number; ticketsAllTime: number;
  };
  issues: IntakeIssueView[];
  storms: { window: string; ticketId: number; createdAt: string; name: string }[];
  recent: IntakeMessageView[];
  unrecognised: IntakeUnrecognisedView[];
}

export interface IntakeSourceConfig {
  name: string; kind: string; queueUrl: string; region: string; accessKeyId: string; pollSeconds: number; serviceUrl: string;
  secretSet?: boolean; secretMasked?: string; secretAccessKey?: string;
}

export interface IntakeConfigView {
  enabled: boolean; problems: string[]; newPerHour: number; newPerDay: number; runbook: string | null;
  sources: IntakeSourceConfig[];
}

async function json<T>(r: Response): Promise<T> {
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error((body as { error?: string }).error ?? `HTTP ${r.status}`);
  return body as T;
}

export const getIntakeProjects = () =>
  fetch('/api/intake/projects').then(r => json<{ projects: { key: string; displayName: string; enabled: boolean }[] }>(r));

export const getIntake = (project: string) =>
  fetch(`/api/projects/${encodeURIComponent(project)}/intake`).then(r => json<IntakePage>(r));

export const pollIntake = (project: string) =>
  fetch(`/api/projects/${encodeURIComponent(project)}/intake/poll`, { method: 'POST' }).then(r => json<unknown>(r));

export const testIntakeSource = (project: string, source: string) =>
  fetch(`/api/projects/${encodeURIComponent(project)}/intake/sources/${encodeURIComponent(source)}/test`, { method: 'POST' })
    .then(r => r.json() as Promise<{ ok: boolean; error?: string; waiting?: number | null; inFlight?: number | null }>);

export const setIntakeMuted = (issueId: number, muted: boolean) =>
  fetch(`/api/intake/issues/${issueId}/muted`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ muted }),
  }).then(r => json<unknown>(r));

export const muteIntakeTicket = (ticketId: number) =>
  fetch(`/api/tickets/${ticketId}/intake/mute`, { method: 'POST' })
    .then(r => json<{ issueId: number; muted: boolean; ticketClosed: boolean; message: string }>(r));

export const getIntakeConfig = (project: string) =>
  fetch(`/api/projects/${encodeURIComponent(project)}/intake/config`).then(r => json<IntakeConfigView>(r));

export const saveIntakeConfig = (project: string, cfg: {
  enabled: boolean; sources: IntakeSourceConfig[]; newPerHour: number; newPerDay: number; runbook: string | null;
}) =>
  fetch(`/api/projects/${encodeURIComponent(project)}/intake/config`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(cfg),
  }).then(r => json<{ saved: boolean }>(r));

/** How the plan names a ticket's origin, for a person. */
export const ORIGIN_LABEL: Record<string, string> = {
  owner: 'Filed by a person',
  steward: 'Filed by the steward, from a slice',
  'found-in-run': 'Found in passing by a run',
  regression: 'A Done ticket’s problem came back',
  intake: 'Filed by intake, from production',
};

export const CHANNEL_LABEL: Record<string, string> = {
  sentry: 'error report', 'cloudwatch-alarm': 'cloud alarm', storm: 'storm of reports', unrecognised: 'unrecognised report',
};
