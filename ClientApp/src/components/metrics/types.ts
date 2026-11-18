/**
 * The shapes the analytics routes return (Api/AnalyticsEndpoints.cs). Each is one row of a dbt mart
 * (analytics/models/marts/), with its columns camel-cased and its three scope columns left off.
 */

export interface AnalyticsStatus {
  available: boolean; unavailable: string | null; running: boolean; hasMarts: boolean;
  asOf: string | null; stale: boolean; lastOk: boolean | null; lastError: string | null;
  lastSeconds: number | null; lastStartedAt: string | null; transcripts: boolean; isolated: boolean;
}

export interface PhaseRow {
  phase: string; dimension: string; dimValue: string; dimLabel: string | null;
  runs: number; tickets: number; medianMinutes: number; p90Minutes: number; totalMinutes: number;
  medianTokens: number; totalTokens: number; totalCacheReadTokens: number | null; medianTurns: number | null;
  medianToolCalls: number | null; completed: number; timeouts: number; errors: number;
  advanced: number; wentBackward: number; reworkRuns: number; reworkMinutes: number; reworkShare: number | null;
  firstAt: string; lastAt: string;
}

export interface TimeSplitRow {
  phase: string; runs: number; runsRecorded: number; runsTranscript: number; runsUnmeasured: number;
  totalMinutes: number; prepMinutes: number; startupMinutes: number; modelMinutes: number; toolMinutes: number;
  agentUnsplitMinutes: number; handoffMinutes: number; overheadMinutes: number; unmeasuredMinutes: number;
  medianPrepS: number | null; medianStartupS: number | null; medianModelS: number | null;
  medianToolS: number | null; medianHandoffS: number | null; medianOverheadS: number | null;
}

export interface ReworkRow {
  phase: string; entryReason: string; reworkClass: string; runs: number; tickets: number;
  minutes: number; tokens: number; medianMinutes: number; meanPass: number;
  shareOfRuns: number; shareOfMinutes: number | null;
}

export interface FirstPassRow {
  dimension: string; dimValue: string; qaAttempted: number; firstPass: number; firstPassRate: number;
  qaFailures: number; meanQaFailures: number; reworkMinutes: number;
  done: number; escapedBugTickets: number; escapedRate: number | null;
}

export interface DailyRow {
  phase: string; day: string; runs: number; minutes: number; medianMinutes: number;
  tokens: number; reworkMinutes: number; unfinished: number;
}

export interface StartRebaseRow {
  phase: string; startRebase: string; runs: number; medianPrepS: number | null; p90PrepS: number | null;
  totalPrepMinutes: number; medianBaseBehind: number | null; maxBaseBehind: number | null;
  medianMinutes: number; conflictEntries: number;
}

export interface DiffSizeRow {
  phase: string; sizeBucket: string; sizeOrder: number; runs: number; medianLines: number;
  medianFiles: number | null; medianMinutes: number; p90Minutes: number; medianTokens: number;
  minutesPer100Lines: number | null; wentBackward: number;
}

export interface ToolRow {
  phase: string; toolFamily: string; toolName: string; toolDetail: string; toolShort: string;
  calls: number; runs: number; totalSeconds: number; medianSeconds: number | null; p90Seconds: number | null;
  maxSeconds: number | null; errorCalls: number; unansweredCalls: number; resultChars: number | null;
  shareOfToolSeconds: number | null;
}

export interface VersionRow {
  phase: string; kind: 'skill' | 'prompt'; version: string; label: string | null; versionAt: string | null;
  source: string; firstAt: string; lastAt: string; runs: number; tickets: number;
  medianMinutes: number; p90Minutes: number; medianTokens: number; medianTurns: number | null;
  reworkRunShare: number; sentBackShare: number; unfinishedShare: number; versionSeq: number;
}

export interface RunListRow {
  runId: number; ticketId: number; ticketTitle: string | null; projectKey: string; phase: string;
  model: string | null; provider: string; effort: string; startedAt: string; outcome: string;
  phaseAfter: string | null; entryReason: string; reworkClass: string; pass: number | null;
  minutes: number; tokens: number; numTurns: number | null; toolCalls: number | null; toolErrors: number | null;
  splitSource: string; linesChanged: number; skillVersion: string | null; promptVersion: string | null;
  /** `@board`, a worker host's name, or `unknown`; and how many runs it had going when this one started. */
  machine: string; machineRuns: number | null;
}

/** Every column of fct_runs. Only what the drawer reads is named; the rest arrive too. */
export interface RunDetail extends RunListRow {
  endedAt: string; durationS: number; inputTokens: number | null; outputTokens: number | null;
  cacheReadTokens: number | null; cacheCreationTokens: number | null; peakContextTokens: number | null;
  /** Null when the provider reported no window, or for a run recorded before it was kept. */
  contextWindowTokens: number | null; compactions: number | null;
  mutations: number; listCostUsd: number | null; cliVersion: string | null; entryReasonSource: string;
  prepS: number | null; startupS: number | null; modelS: number | null; toolS: number | null;
  agentUnsplitS: number | null; handoffS: number | null; overheadS: number | null; unmeasuredS: number | null;
  sessionId: string | null; matchMethod: string | null; sessionSpanS: number | null;
  skillVersionLabel: string | null; skillVersionAt: string | null; skillVersionSource: string | null;
  promptVersionLabel: string | null; promptVersionAt: string | null; promptVersionSource: string | null;
  startRebase: string | null; baseBehind: number | null; filesChanged: number | null;
  linesAdded: number | null; linesRemoved: number | null; runCommits: number | null; experimentArm: string;
  machineSource: string; slot: number | null;
}

export interface ToolCall {
  seq: number; toolName: string; toolShort: string; toolFamily: string; toolDetail: string | null;
  inputSummary: string | null; offsetS: number; seconds: number | null; isError: boolean;
  resultChars: number | null; isSidechain: boolean;
}

/** QA's weaker calls - carry-overs and passes by inspection - and how often refinement overturned them (mart_qa_rulings). */
export interface QaRulingRow {
  dimension: string; dimValue: string; qaRuns: number; reworkRuns: number; reworkRunsOverturned: number;
  reworkOverturnShare: number | null; passes: number; passesDriven: number; byInspection: number;
  carryOvers: number; carryOverRate: number | null; inspectionRate: number | null;
  carryOverturned: number; carryOverturnRate: number | null;
  inspectionOverturned: number; inspectionOverturnRate: number | null; drivenOverturned: number;
  overturned: number; overturnRate: number | null; overturnedRedriven: number;
  overturnedFailed: number; overturnedFailedRate: number | null;
}

/** Finished work a person sent back after it reached review, by kind of miss (mart_escapes). */
export interface EscapeRow {
  dimension: string; dimValue: string; handoffs: number; ticketsReviewed: number; escapes: number;
  misses: number; requirementChanges: number; unclassified: number; overridden: number;
  outsideContract: number; inheritedBehaviour: number; uiPolish: number; logic: number; other: number;
  foundByOwner: number; foundInPrReview: number;
  missesPerHandoff: number | null; missesPerTicket: number | null;
}

export interface Overview {
  scope: { windowDays: number; project: string; track: string };
  status: AnalyticsStatus;
  phases: PhaseRow[]; timeSplit: TimeSplitRow[]; rework: ReworkRow[]; firstPass: FirstPassRow[];
  qaRulings?: QaRulingRow[];
  escapes?: EscapeRow[];
  daily: DailyRow[]; startRebase: StartRebaseRow[]; diffSize: DiffSizeRow[];
}

/** What the page is scoped to, as every analytics route takes it. */
export interface Scope { days: number; project: string | null; track: string }

export function scopeQuery(s: Scope, extra: Record<string, string | number | null | undefined> = {}): string {
  const q = new URLSearchParams({ days: String(s.days), track: s.track });
  if (s.project) q.set('project', s.project);
  for (const [k, v] of Object.entries(extra)) if (v !== null && v !== undefined && v !== '') q.set(k, String(v));
  return q.toString();
}

/** mart_bugs: confirmed bugs by what let them through. */
export interface BugRow {
  dimension: string; dimValue: string; bugs: number; links: number; escapedBugs: number; sourceTickets: number;
  unmappedLinks: number; linksWithRun: number; medianDaysToFind: number | null; fixMinutes: number;
  pendingLinks: number; rejectedLinks: number;
}

/** fct_bug_links: one cause suggested for one bug. */
export interface BugLinkRow {
  linkId: number; bugTicketId: number; bugTitle: string | null; bugCreatedAt: string; projectKey: string;
  sourceTicketId: number | null; sourceTitle: string | null; status: 'Suggested' | 'Confirmed' | 'Rejected';
  origin: string; share: number | null; escaped: boolean; foundBy: string; missedBy: string;
  daysToFind: number | null; writtenByRunId: number | null; runTier: string; writtenPhase: string | null;
  writtenModel: string | null; writtenEffort: string | null; writtenSkillVersion: string | null;
  attributedFixMinutes: number;
}

/** mart_bugs_by_origin: bug tickets by where they came from, per week filed ('all' is the window). */
export interface BugOriginRow {
  week: string; origin: 'owner' | 'steward' | 'found-in-run' | 'regression' | 'intake'; channel: string;
  filed: number; done: number; medianHoursToDone: number | null; regressions: number;
  // Done is fixed + closedWithoutWork: a bug closed as not a bug, already fixed, a duplicate or not
  // needed (Ticket.Resolution) stays counted as filed. Optional: marts built before it was recorded.
  fixed?: number; closedWithoutWork?: number; notABug?: number; alreadyFixed?: number;
  duplicates?: number; notNeeded?: number; closedBySteward?: number;
}

export interface BugsView { summary: BugRow | null; split: BugRow[]; links: BugLinkRow[]; origins?: BugOriginRow[] }

/** mart_experiment_arms */
export interface ArmRow {
  experimentId: number; armKey: 'control' | 'variant'; armName: string; skillVariant: string | null;
  model: string | null; effort: string | null; provider: string | null; tickets: number; done: number;
  medianMinutes: number | null; medianFreshTokens: number | null; medianCacheReadTokens: number | null;
  qaFailureN: number; qaFailureBad: number; reworkN: number; reworkBad: number; escapedN: number; escapedBad: number;
  reworkShare: number; ticketsLeftArm: number; appliedDone: number; appliedMedianMinutes: number | null;
  appliedMedianFreshTokens: number | null;
}

/** mart_experiments, with its arms */
export interface ExperimentRow {
  experimentId: number; projectKey: string; skill: string; name: string; hypothesis: string | null;
  status: string; stage: string; minTicketsPerArm: number; waitDays: number; startedAt: string;
  stoppedAt: string | null; stopKind: string | null; stopReason: string | null; winner: string | null;
  controlTickets: number; variantTickets: number; lastDoneAt: string | null; verdictFrom: string | null;
  qaFailureP: number; qaFailureBreaks: boolean; reworkP: number; reworkBreaks: boolean;
  escapedP: number; escapedBreaks: boolean; guardrailBroken: boolean;
  suggestion: string | null; suggestionWhy: string; arms: ArmRow[];
}

/**
 * mart_discovery_funnel: one cut of the Discovery track's funnel. Counts are null where a cut has
 * no row of that grain (an area has proposals but no discovery-wide cost). Rates are null below
 * `minVerdicts` closed verdicts, with `enoughVerdicts` false - the page says so rather than show 0%.
 */
export interface DiscoveryFunnelRow {
  dimension: string; dimValue: string; dimLabel: string | null;
  discoveries: number | null; discoveriesDone: number | null;
  proposalsWritten: number | null; shown: number | null; dropped: number | null; restored: number | null;
  restoredApproved: number | null; decided: number | null; closed: number | null; approved: number | null;
  rejected: number | null; rejectedGeneric: number | null; rejectedAlreadyDecided: number | null;
  rejectedNotNow: number | null; rejectedWrong: number | null; snoozed: number | null; changedMind: number | null;
  newToMe: number | null; newToMeAsked: number | null; edited: number | null; medianEditShare: number | null;
  filed: number | null; filedTickets: number | null; filedTicketsDone: number | null;
  goals: number | null; goalsChecked: number | null; landedFirstTime: number | null;
  recheckShown: number | null; recheckClosed: number | null; recheckApproved: number | null;
  fixupsClosed: number | null; fixupsApproved: number | null; recheckNewClosed: number | null; recheckNewApproved: number | null;
  repeats: number | null; repeatsAcross: number | null;
  bugsFiled: number | null; bugsConfirmed: number | null; bugsFixed: number | null;
  runs: number | null; runsPriced: number | null; runMinutes: number | null; runTokens: number | null;
  discoveriesPriced: number | null; costUsd: number | null; approvedPriced: number | null;
  ownerMinutes: number | null; approvedTimed: number | null; ownerMinutesTimed: number | null;
  observations: number | null; scoutObservations: number | null; siftOut: number | null; siftDropped: number | null;
  visits: number | null; discoveriesWithVisits: number | null; medianPages: number | null; medianAreas: number | null;
  medianRoles: number | null; medianWidths: number | null; visitMinutes: number | null;
  offRouteVisits: number | null; routeJudgedVisits: number | null;
  enoughVerdicts: boolean; minVerdicts: number;
  approvalRate: number | null; rejectionRate: number | null; newToMeShare: number | null; editedShare: number | null;
  repeatShare: number | null; approvedPerDollar: number | null; approvedPerOwnerMinute: number | null;
  siftYield: number | null; siftCutShare: number | null; offRouteShare: number | null;
  landedRate: number | null; recheckYield: number | null;
  verdictRounds: number | null; verdictRoundsCompleted: number | null; medianHoursToVerdict: number | null;
  p90HoursToVerdict: number | null; medianOwnerMinutesPerRound: number | null;
}

/** mart_discovery_rejections: one reason in one cut. */
export interface DiscoveryRejectionRow {
  dimension: string; dimValue: string; dimLabel: string | null; reason: string; rejections: number;
  rejected: number; closed: number; enoughVerdicts: boolean; minVerdicts: number;
  shareOfRejections: number; shareOfClosed: number; teaches: string;
}

/** mart_discovery_versions: one version of a discovery skill or of the taste file. */
export interface DiscoveryVersionRow {
  kind: 'sift_skill' | 'recheck_skill' | 'scout_skill' | 'chart_skill' | 'taste'; version: string;
  label: string | null; versionAt: string | null; versionSeq: number; firstAt: string; lastAt: string;
  discoveries: number; shown: number; closed: number; approved: number; rejected: number; rejectedTaste: number;
  newToMe: number; edited: number; enoughVerdicts: boolean; minVerdicts: number;
  approvalRate: number | null; tasteRejectionRate: number | null; newToMeShare: number | null;
}

/** GET /api/analytics/discovery */
export interface DiscoveryView {
  scope: { windowDays: number; project: string; track: string }; by: string;
  summary: DiscoveryFunnelRow | null; split: DiscoveryFunnelRow[];
  rejections: DiscoveryRejectionRow[]; rejectionsSplit: DiscoveryRejectionRow[];
  versions: DiscoveryVersionRow[];
}
