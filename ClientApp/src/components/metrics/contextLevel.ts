import { compact } from './parts';

/**
 * How full a run's context got, against the window its model had. The one place that says which share
 * is worth a warning, so the Runs page and the ticket drawer cannot disagree about the same run.
 *
 * - `unknown`: the provider reported no window, so a share would be a guess. The peak is still shown.
 * - `warn`: past half the window. Worth knowing, nothing wrong yet.
 * - `high`: past four fifths, or the conversation was compacted at all. A compacted run's peak can
 *   look ordinary afterwards, which is why a compaction counts on its own.
 */
export type ContextLevel = 'unknown' | 'ok' | 'warn' | 'high';

export const CONTEXT_WARN_SHARE = 0.5;
export const CONTEXT_HIGH_SHARE = 0.8;

export function contextLevel(peak: number | null | undefined, window: number | null | undefined,
  compactions: number | null | undefined): ContextLevel {
  if ((compactions ?? 0) > 0) return 'high';
  if (!peak || !window) return 'unknown';
  const share = peak / window;
  if (share >= CONTEXT_HIGH_SHARE) return 'high';
  if (share >= CONTEXT_WARN_SHARE) return 'warn';
  return 'ok';
}

/** Ordered so the worst of several runs can be picked with a comparison. */
export const CONTEXT_RANK: Record<ContextLevel, number> = { unknown: 0, ok: 1, warn: 2, high: 3 };

/** The text colour each level reads in: amber for caution, red for trouble, as elsewhere on the board. */
export const CONTEXT_TONE: Record<ContextLevel, string> = {
  unknown: 'text-gray-200', ok: 'text-gray-200', warn: 'text-amber-300', high: 'text-red-300',
};

/** "486k of 1.0M · 49%", plus the compactions when there were any. Just the peak when no window is known. */
export function describeContext(peak: number | null | undefined, window: number | null | undefined,
  compactions: number | null | undefined): string {
  if (!peak) return '—';
  const base = window ? `${compact(peak)} of ${compact(window)} · ${Math.round((peak / window) * 100)}%` : compact(peak);
  const n = compactions ?? 0;
  return n > 0 ? `${base}, compacted ${n}×` : base;
}
