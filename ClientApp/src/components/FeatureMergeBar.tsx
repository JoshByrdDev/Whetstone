import { useState } from 'react';
import { mergeIntoFeature } from '../api/git';
import type { FeatureMergeResult } from '../api/git';
import { Action } from './PrModal';

const OUTCOME: Record<string, { text: string; tone: string }> = {
  merged: { text: 'merged and pushed', tone: 'text-emerald-300' },
  already: { text: 'nothing to merge', tone: 'text-gray-500' },
  conflict: { text: 'conflicts', tone: 'text-red-300' },
  ready: { text: 'would merge - held back with the rest', tone: 'text-gray-400' },
  'not-pushed': { text: 'not pushed', tone: 'text-amber-300' },
};

/**
 * The other way out of the PR dialog, for a ticket filed on a feature branch of its own on a project
 * that allows it (ProjectConfig.MergeIntoFeatureBranches): merge the ticket straight into that branch
 * in every repo it touched and push it, with no pull request. The pull request stays the dialog's main
 * button; this sits above it as the alternative. One press covers every repo, and a conflict in any
 * of them leaves all of them as they were.
 */
export function FeatureMergeBar({ ticketId, feature, title, description, disabled, onMerged }: {
  ticketId: number;
  feature: string;
  title: string;
  description: string;
  /** The PR side is busy, or already done. */
  disabled?: boolean;
  onMerged?: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<FeatureMergeResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const done = result?.ok === true;

  async function merge() {
    setBusy(true); setError(null);
    try {
      const r = await mergeIntoFeature(ticketId, title.trim(), description);
      setResult(r);
      if (!r.ok) setError(r.error ?? 'Could not merge.');
      else onMerged?.();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not merge.');
    } finally {
      setBusy(false);
    }
  }

  const several = (result?.repos.length ?? 0) > 1 || (result?.repos.some(r => r.dir) ?? false);

  return (
    <div className="px-5 max-sm:px-3 py-2.5 border-t border-hair bg-panel/60 space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-[11px] text-gray-500 min-w-0 flex-1">
          {done
            ? <span className="text-emerald-400">Merged into <span className="font-mono">{feature}</span> and pushed.{result?.movedToDone && ' The ticket is Done.'}</span>
            : <>Or skip the pull request: merge it into <span className="font-mono text-gray-300">{feature}</span> and push.</>}
        </span>
        {!done && (
          <Action tone="good" onClick={merge} disabled={busy || disabled}>
            {busy ? 'Merging…' : `Merge into ${feature}`}
          </Action>
        )}
      </div>
      <div key={result ? (done ? 'done' : 'refused') : 'idle'} className="orbit-swap space-y-1.5">
        {error && <div className="text-xs text-red-300">{error}</div>}
        {result?.phaseNote && <div className="text-xs text-amber-300">{result.phaseNote}</div>}
        {result && several && result.repos.map(r => (
          <div key={r.dir} className="text-xs min-w-0">
            <span className="font-mono text-gray-400">{r.dir || 'repo'}</span>{' '}
            <span className={OUTCOME[r.outcome]?.tone ?? 'text-gray-400'}>{OUTCOME[r.outcome]?.text ?? r.outcome}</span>
            {r.mergeSha && <span className="font-mono text-gray-500"> at {r.mergeSha.slice(0, 7)}</span>}
            {r.conflictedFiles.length > 0 && (
              <div className="mt-0.5 font-mono text-[10px] text-red-300/80 break-all">{r.conflictedFiles.join(', ')}</div>
            )}
            {r.localNote && <div className="text-[11px] text-amber-300/80">{r.localNote}</div>}
          </div>
        ))}
        {result && !several && result.repos[0]?.localNote && (
          <div className="text-[11px] text-amber-300/80">{result.repos[0].localNote}</div>
        )}
        {result && !several && (result.repos[0]?.conflictedFiles.length ?? 0) > 0 && (
          <div className="font-mono text-[10px] text-red-300/80 break-all">{result.repos[0].conflictedFiles.join(', ')}</div>
        )}
      </div>
    </div>
  );
}
