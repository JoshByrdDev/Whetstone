import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { Chip } from '../ui';
import { chamfer, MONO } from '../orbit';
import { useVeil } from '../../hooks/useVeil';
import type { RunDetail, RunListRow, Scope, ToolCall } from './types';
import { scopeQuery } from './types';
import type { RunFilter } from './PhaseDetail';
import { CONTEXT_TONE, contextLevel, describeContext } from './contextLevel';
import {
  Empty, FilterChip, NUM, SegBar, Section, Sheet, Swatch, TD, TH, TIME_PARTS, compact, day, mins, phaseName, reasonOf,
  secs, useJson,
} from './parts';

/**
 * The runs behind whichever number was picked, and one run opened: its time split, what it was told,
 * and every tool call it made, laid out on its own clock.
 */

const SORTS = [{ key: 'recent', label: 'Recent' }, { key: 'slowest', label: 'Slowest' }, { key: 'tokens', label: 'Most tokens' }];

export function RunList({ scope, phase, filter, onClearFilter, bump }: {
  scope: Scope; phase: string | null; filter: RunFilter | null; onClearFilter: () => void; bump: number;
}) {
  const [sort, setSort] = useState('recent');
  const [open, setOpen] = useState<number | null>(null);
  const extra: Record<string, string> = { sort, limit: '60' };
  if (phase) extra.phase = phase;
  if (filter) extra[filter.name] = filter.value;
  const { data } = useJson<RunListRow[]>(`/api/analytics/runs?${scopeQuery(scope, extra)}`, bump);
  const rows = data ?? [];
  return (
    <Section title="Runs" meta={phase ? `${phaseName(phase)} · click one for its tool calls` : 'click one for its tool calls'}
      right={<div className="flex gap-1.5 max-sm:hidden">{SORTS.map(s => <Chip key={s.key} on={sort === s.key} onClick={() => setSort(s.key)}>{s.label}</Chip>)}</div>}>
      <div className="mb-2 flex flex-wrap items-center gap-1.5">
        {filter && <FilterChip label={filter.label} onClear={onClearFilter} />}
        <div className="flex gap-1.5 sm:hidden">{SORTS.map(s => <Chip key={s.key} on={sort === s.key} onClick={() => setSort(s.key)}>{s.label}</Chip>)}</div>
      </div>
      {rows.length === 0 ? <Empty>No runs match.</Empty> : (
        <Sheet pad={false}>
          <table className="w-full table-fixed text-xs">
            <thead>
              <tr className="border-b border-hair text-gray-500">
                <th className={`${TH} w-[58%] text-left sm:w-[34%]`}>Ticket</th>
                <th className={`${TH} text-left max-sm:hidden`}>Why</th>
                <th className={`${TH} text-left max-sm:hidden`}>Model</th>
                <th className={`${TH} ${NUM}`}>Time</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>Tokens</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>Calls</th>
                <th className={`${TH} text-left`}>Ended</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-hairsoft">
              {rows.map(r => (
                <tr key={r.runId} className="orbit-pick cursor-pointer" tabIndex={0} onClick={() => setOpen(r.runId)}
                  onKeyDown={e => { if (e.key === 'Enter') setOpen(r.runId); }}>
                  <td className={TD}>
                    <div className="flex min-w-0 items-baseline gap-1.5">
                      <span className="shrink-0 text-gray-500" style={{ fontFamily: MONO }}>#{r.ticketId}</span>
                      <span className="min-w-0 truncate text-gray-200" title={r.ticketTitle ?? ''}>{r.ticketTitle}</span>
                    </div>
                    <div className="text-[10.5px] text-gray-600">{phaseName(r.phase)} · {day(r.startedAt)}{r.pass && r.pass > 1 ? ` · pass ${r.pass}` : ''}</div>
                  </td>
                  <td className={`${TD} max-sm:hidden`}>
                    <span className="inline-flex min-w-0 items-center gap-1.5"><Swatch swatch={reasonOf(r.entryReason).swatch} /><span className="truncate text-gray-300">{reasonOf(r.entryReason).label}</span></span>
                  </td>
                  <td className={`${TD} truncate text-gray-400 max-sm:hidden`} title={`${r.model ?? ''} · ${r.effort}`}>{r.model?.replace(/^claude-/, '') ?? '—'}</td>
                  <td className={`${TD} ${NUM} text-gray-100`} style={{ fontFamily: MONO }}>{mins(r.minutes)}</td>
                  <td className={`${TD} ${NUM} text-gray-300 max-sm:hidden`} style={{ fontFamily: MONO }}>{compact(r.tokens)}</td>
                  <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden`} style={{ fontFamily: MONO }}>{r.splitSource === 'none' ? '—' : r.toolCalls}</td>
                  <td className={`${TD} truncate`}>
                    {r.outcome === 'Completed'
                      ? <span className="text-gray-500">{r.phaseAfter && r.phaseAfter !== r.phase ? `→ ${phaseName(r.phaseAfter)}` : 'stayed'}</span>
                      : <span className="text-red-300">{r.outcome === 'Timeout' ? 'timed out' : 'failed'}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Sheet>
      )}
      {open !== null && <RunDrawer runId={open} onClose={() => setOpen(null)} />}
    </Section>
  );
}

const MATCH: Record<string, string> = {
  session_id: 'by the session id the run recorded',
  prompt: 'by the ticket and phase its prompt names',
  cwd: 'by its worktree and start time',
};

/** One run, over the page. Rendered only while open, so its exit is the veil's own. */
export function RunDrawer({ runId, onClose }: { runId: number; onClose: () => void }) {
  const { fx, dismiss } = useVeil(onClose);
  const { data, error } = useJson<{ run: RunDetail; calls: ToolCall[] }>(`/api/analytics/runs/${runId}`);
  const [filter, setFilter] = useState('');

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss]);

  const run = data?.run;
  const ctxLevel = contextLevel(run?.peakContextTokens, run?.contextWindowTokens, run?.compactions);
  const calls =(data?.calls ?? []).filter(c => !filter || `${c.toolShort} ${c.toolDetail ?? ''} ${c.inputSummary ?? ''}`.toLowerCase().includes(filter.toLowerCase()));
  const span = Math.max(1, run?.durationS ?? 1, ...(data?.calls ?? []).map(c => c.offsetS + (c.seconds ?? 0)));
  const parts = run ? TIME_PARTS.map(p => {
    const s = (run as unknown as Record<string, number | null>)[`${p.key}S`] ?? 0;
    return { ...p, value: s, title: secs(s) };
  }) : [];

  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex justify-end" data-fx={fx} role="dialog" aria-modal="true" aria-label={`Run ${runId}`}>
      <div className="orbit-dim absolute inset-0 bg-black/60" onClick={dismiss} />
      <div className="relative flex h-full w-full max-w-[760px] flex-col border-l border-hair bg-void" style={{ clipPath: chamfer(0) }}>
        <div className="flex items-start gap-3 border-b border-hair px-4 py-3 sm:px-5">
          <div className="min-w-0 flex-1">
            <div className="text-[11px] text-gray-500" style={{ fontFamily: MONO }}>
              run {runId}{run ? ` · #${run.ticketId} · ${run.projectKey}` : ''}
            </div>
            <div className="truncate text-sm font-medium text-gray-100" title={run?.ticketTitle ?? ''}>{run?.ticketTitle ?? (error ? 'Could not load this run' : 'Loading…')}</div>
            {run && (
              <div className="mt-0.5 text-[11px] text-gray-400">
                {phaseName(run.phase)} → {run.outcome === 'Completed' ? phaseName(run.phaseAfter) : run.outcome === 'Timeout' ? 'timed out' : 'failed'}
                {' · '}{reasonOf(run.entryReason).label}{run.pass ? ` · pass ${run.pass}` : ''}{' · '}{new Date(run.startedAt).toLocaleString()}
              </div>
            )}
          </div>
          <button onClick={dismiss} className="orbit-tap shrink-0 px-2 text-lg leading-none text-gray-400 hover:text-gray-100" aria-label="Close">×</button>
        </div>

        {run && (
          <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4 sm:px-5">
            <div className="mb-1 flex items-baseline justify-between text-xs">
              <span className="text-gray-300">{mins(run.minutes)} wall clock</span>
              <span className="text-[11px] text-gray-500">{run.splitSource === 'recorded' ? 'every stage timed' : run.splitSource === 'transcript' ? 'split from the transcript' : 'no transcript found'}</span>
            </div>
            <SegBar parts={parts} height="h-3.5" />
            <ul className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px]">
              {parts.filter(p => p.value > 0).map(p => (
                <li key={p.key} className="inline-flex items-center gap-1.5" title={p.note}>
                  <Swatch swatch={p.swatch} /><span className="text-gray-400">{p.label}</span><span className="text-gray-200" style={{ fontFamily: MONO }}>{secs(p.value)}</span>
                </li>
              ))}
            </ul>

            <dl className="mt-4 grid grid-cols-2 gap-x-4 gap-y-2 text-xs sm:grid-cols-3">
              <Fact k="Model" v={`${run.model ?? '—'}${run.effort !== 'not recorded' ? ` · ${run.effort}` : ''}`} />
              <Fact k="Tokens" v={`${compact(run.tokens)} · ${compact(run.cacheReadTokens)} cached`} />
              <Fact k="Turns" v={run.numTurns ?? '—'} />
              <Fact k="Tool calls" v={run.splitSource === 'none' ? '—' : `${run.toolCalls}${run.toolErrors ? `, ${run.toolErrors} errors` : ''}`} />
              <Fact k="Peak context" v={<span className={CONTEXT_TONE[ctxLevel]}>{describeContext(run.peakContextTokens, run.contextWindowTokens, run.compactions)}</span>}
                sub={ctxLevel === 'high' ? (run.compactions ? 'ran out of room and was summarised' : 'near the window')
                  : ctxLevel === 'warn' ? 'over half the window'
                  : ctxLevel === 'unknown' && run.peakContextTokens ? 'no window reported' : null} />
              <Fact k="Change" v={run.linesAdded === null ? 'not recorded' : `+${run.linesAdded} −${run.linesRemoved} in ${run.filesChanged} files`} />
              <Fact k="Skill" v={run.skillVersion ?? '—'} sub={run.skillVersionLabel} mono />
              <Fact k="Prompt" v={run.promptVersion ?? '—'} sub={run.promptVersionLabel} mono />
              <Fact k="Start rebase" v={run.startRebase ? `${run.startRebase}${run.baseBehind !== null ? `, ${run.baseBehind} behind` : ''}` : 'not recorded'} />
              <Fact k="Transcript" v={run.matchMethod ? 'found' : 'none here'} sub={run.matchMethod ? MATCH[run.matchMethod] : 'a worker host keeps its own; older ones age out'} />
              {run.listCostUsd !== null && <Fact k="List price" v={`$${run.listCostUsd.toFixed(2)}`} sub="a relative weight, not a bill" />}
              {run.experimentArm !== 'none' && <Fact k="A/B arm" v={run.experimentArm} />}
            </dl>

            <div className="mt-5 flex items-center gap-3">
              <span className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">Tool calls</span>
              <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="filter"
                className="min-w-0 flex-1 border border-hair bg-sunk px-2 py-1 text-xs text-gray-200 placeholder:text-gray-600" style={{ clipPath: chamfer(6) }} />
            </div>
            {(data?.calls.length ?? 0) === 0 ? <div className="mt-2"><Empty>No tool calls on record for this run.</Empty></div> : (
              <ol className="mt-2 space-y-px text-[11px]">
                {calls.map(c => (
                  <li key={c.seq} className="grid min-w-0 grid-cols-[minmax(0,1fr)_3rem] items-center gap-x-2 sm:grid-cols-[minmax(0,15rem)_minmax(0,1fr)_3rem]">
                    <div className="min-w-0 truncate" title={c.inputSummary ?? ''}>
                      <span className={c.isError ? 'text-red-300' : 'text-gray-200'}>{c.toolDetail || c.toolShort}</span>
                      {c.inputSummary && c.toolDetail !== c.inputSummary && <span className="ml-1.5 text-gray-600">{c.inputSummary}</span>}
                    </div>
                    <div className="relative h-2.5 max-sm:hidden" title={`at ${secs(c.offsetS)}, took ${secs(c.seconds)}`}>
                      <div className={`absolute top-0 h-full rounded-[2px] ${c.isError ? 'bg-red-400' : c.isSidechain ? 'bg-violet-400' : 'bg-emerald-500'}`}
                        style={{ left: `${Math.max(0, Math.min(100, (100 * c.offsetS) / span))}%`, width: `max(2px, ${(100 * (c.seconds ?? 0)) / span}%)` }} />
                    </div>
                    <span className="text-right text-gray-400" style={{ fontFamily: MONO }}>{c.seconds === null ? 'no reply' : secs(c.seconds)}</span>
                  </li>
                ))}
              </ol>
            )}
          </div>
        )}
      </div>
    </div>,
    document.body,
  );
}

function Fact({ k, v, sub, mono }: { k: string; v: React.ReactNode; sub?: string | null; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="text-[10.5px] text-gray-500">{k}</dt>
      <dd className="truncate text-gray-200" style={mono ? { fontFamily: MONO } : undefined}>{v}</dd>
      {sub && <dd className="truncate text-[10.5px] text-gray-500" title={sub}>{sub}</dd>}
    </div>
  );
}
