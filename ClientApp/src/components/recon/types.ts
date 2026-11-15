import type { AnalyticsStatus } from '../metrics/types';

/**
 * What the Recon routes return (Api/ReconEndpoints.cs). The rows are the Recon marts' own, in
 * camelCase, with their list columns parsed; docs/recon-tab-plan.md, "R1 and R2 as built", names
 * every column.
 */

export interface ReconArea { name: string; pages: string[]; code: string[] }

export interface ReconRepo { repo: string; base_ref: string | null; tip: string | null; commits: number | null; error: string | null }

export interface ReconProject {
  key: string; displayName: string; areas: ReconArea[]; roles: string[];
  /** False: the marts were built before this project had a discovery block - "not built yet". */
  built: boolean; builtAreas: number | null; builtRoles: number | null;
  builtAt: string | null; inputsAt: string | null; historyOk: boolean | null; repos: ReconRepo[] | null;
  /** Why Scout this cannot file a discovery here (Discovery not one of its tracks, no siteUrl), or null. */
  cannotFile: string | null;
}

export interface ReconProjects { projects: ReconProject[]; status: AnalyticsStatus; log: string | null }

export interface ReconCell { role: string; width: string }

export type SuggestionKind = 'rerun' | 'gap' | 'changed';

export interface Suggestion {
  suggestionKey: string; kind: SuggestionKind; rule: number; rank: number;
  area: string; areaOrdinal: number; cells: ReconCell[]; direction: string;
  sourceDiscoveryId: number | null; sourceLineageId: number | null;
  reasons: string[]; cooling: boolean; areaHeadCommit: string | null;
  filesChanged: number; commitsChanged: number; tickets: number[];
  lastScoutedAt: string | null; lastCommit: string | null; approved: number | null; newToMe: number | null;
}

export interface SetAside { key: string; kind: string | null; area: string | null; direction: string | null; until: string | null; at: string }

export interface InFlight { key: string; ticketId: number; title: string | null; phase: string }

export interface SuggestionsRead { suggestions: Suggestion[]; setAside: SetAside[]; inFlight: InFlight[] }

export interface CoverageCell {
  area: string; areaOrdinal: number; role: string; roleOrdinal: number; width: string; widthOrdinal: number;
  inConfig: boolean; hasCode: boolean; visits: number; seconds: number; discoveries: number;
  lastVisitedAt: string | null; lastDiscoveryId: number | null; lastCommit: string | null; lastPlacement: string | null;
  state: 'gap' | 'fresh' | 'changed' | 'unknown'; commitsSince: number; filesSince: number; ticketsSince: number[];
  shown: number; approved: number; areaHeadCommit: string | null; cooling: boolean;
}

export interface PatternRow { routePattern: string; role: string; width: string; visits: number; seconds: number; discoveries: number; lastVisitedAt: string | null }

export interface AreaDiscovery {
  discoveryId: number; title: string | null; direction: string | null; phase: string; isDone: boolean; createdAt: string;
  visits: number; seconds: number; lastVisitedAt: string | null; shown: number; approved: number; rejected: number; newToMe: number; filed: number;
}

export interface AreaCommit {
  repo: string; sha: string; fpIndex: number; committedAt: string | null; subject: string | null; files: number;
  tickets: number[]; since: 'after' | 'recent' | 'by_date';
}

export interface AreaRead {
  area: string; config: ReconArea | null; cells: CoverageCell[]; patterns: PatternRow[];
  discoveries: AreaDiscovery[]; commits: AreaCommit[];
}

export interface YieldRow {
  cut: 'area' | 'direction'; cutKey: string; label: string | null; discoveries: number; shown: number; approved: number;
  rejected: number; rejectedGeneric: number; rejectedAlreadyDecided: number; rejectedNotNow: number; rejectedWrong: number;
  snoozed: number; newToMe: number; filed: number; costUsd: number | null; costComplete: boolean | null;
  latestDiscoveryId: number | null; latestDirection: string | null; latestAt: string | null;
}

export interface YieldRead { days: number; areas: YieldRow[]; directions: YieldRow[] }

/** What Scout this hands the new-ticket form: the key it files under, and the words it starts from. */
export interface ScoutAsk { key: string; title: string; direction: string; note: string }
