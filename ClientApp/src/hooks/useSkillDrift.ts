import { useEffect, useState } from 'react';

/** One project's copies that have fallen behind their template, or have no baseline to compare. */
export interface SkillDriftSummary {
  project: string;
  behind: number;
  untracked: number;
}

/**
 * The template-drift counts, shared by the tab badge and the Skills page.
 *
 * One store at module level rather than a fetch per caller: the page resolving a card has to move
 * the badge in the header at the same moment, and two hooks polling on their own clocks would let
 * them disagree for up to a poll. Templates change when Whetstone itself is pulled, which is rare,
 * so a slow poll plus a refresh whenever the window comes back is enough to notice it.
 */
const POLL_MS = 5 * 60_000;

let current: SkillDriftSummary[] = [];
const listeners = new Set<(s: SkillDriftSummary[]) => void>();
let timer: ReturnType<typeof setInterval> | null = null;

export function refreshSkillDrift(): void {
  fetch('/api/skill-drift')
    .then(r => (r.ok ? r.json() : null))
    .then((data: SkillDriftSummary[] | null) => {
      if (!Array.isArray(data)) return;
      current = data;
      listeners.forEach(l => l(current));
    })
    .catch(() => {});
}

const onFocus = () => refreshSkillDrift();

export function useSkillDrift(): SkillDriftSummary[] {
  const [summary, setSummary] = useState(current);
  useEffect(() => {
    listeners.add(setSummary);
    if (listeners.size === 1) {
      refreshSkillDrift();
      timer = setInterval(refreshSkillDrift, POLL_MS);
      window.addEventListener('focus', onFocus);
    }
    return () => {
      listeners.delete(setSummary);
      if (listeners.size === 0) {
        if (timer) clearInterval(timer);
        timer = null;
        window.removeEventListener('focus', onFocus);
      }
    };
  }, []);
  return summary;
}

/** Everything that wants attention, across every project: what the tab's badge shows. */
export const driftTotal = (s: SkillDriftSummary[]) => s.reduce((n, p) => n + p.behind + p.untracked, 0);
