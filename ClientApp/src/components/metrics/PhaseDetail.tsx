import { useState } from 'react';
import { Chip } from '../ui';
import { MONO } from '../orbit';
import type { DiffSizeRow, PhaseRow, Scope, StartRebaseRow, ToolRow, VersionRow } from './types';
import { scopeQuery } from './types';
import {
  DIMENSIONS, Empty, Meter, machineName, NUM, Section, Sheet, TD, TH, compact, day, mins, pct, phaseName, reasonOf, secs, useJson,
} from './parts';

/**
 * One phase, opened up: what explains its cost (split by any dimension), whether the last change to
 * its skill or the prompt helped, what its agents wait on, and what the start rebase and the size of
 * the change cost. Every row that names a set of runs narrows the run list to them.
 */

export type RunFilter = { name: string; value: string; label: string };

/** A value as the page writes it: a version hash short, a reason by its name. */
function valueLabel(dimension: string, value: string): string {
  if (dimension === 'entry_reason') return reasonOf(value).label;
  if (dimension === 'machine') return machineName(value);
  if (dimension === 'machine_load') {
    const [m, load] = value.split(' / ');
    return load ? `${machineName(m)}, ${load}` : machineName(m);
  }
  if (dimension === 'pass') return value === '3+' ? 'Third or later' : value === '1' ? 'First' : value === '2' ? 'Second' : value;
  return value;
}

export function SplitBy({ scope, phase, bump, onPick }: {
  scope: Scope; phase: string; bump: number; onPick: (f: RunFilter) => void;
}) {
  const [dim, setDim] = useState('model');
  const { data } = useJson<PhaseRow[]>(`/api/analytics/breakdown?${scopeQuery(scope, { phase, dimension: dim })}`, bump);
  const rows = data ?? [];
  const max = Math.max(1, ...rows.map(r => r.medianMinutes));
  const d = DIMENSIONS.find(x => x.key === dim)!;
  return (
    <Section title={`${phaseName(phase)} split by`} meta="what explains the cost">
      <div className="mb-2 flex flex-wrap gap-1.5">
        {DIMENSIONS.map(x => <Chip key={x.key} on={dim === x.key} onClick={() => setDim(x.key)}>{x.label}</Chip>)}
      </div>
      {rows.length === 0 ? <Empty>No runs in this window.</Empty> : (
        <Sheet pad={false} className="orbit-swap" key={dim}>
          <table className="w-full table-fixed text-xs">
            <thead>
              <tr className="border-b border-hair text-gray-500">
                <th className={`${TH} w-[34%] text-left sm:w-[28%]`}>{d.label}</th>
                <th className={`${TH} ${NUM}`}>Runs</th>
                <th className={`${TH} w-[22%] text-left max-sm:hidden`}>Median</th>
                <th className={`${TH} ${NUM} sm:hidden`}>Median</th>
                <th className={`${TH} ${NUM} max-sm:hidden lg:max-xl:hidden`}>p90</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>Tokens</th>
                <th className={`${TH} ${NUM}`}>Rework</th>
                <th className={`${TH} ${NUM} whitespace-nowrap max-sm:hidden lg:max-xl:hidden`}>Sent back</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-hairsoft">
              {rows.map(r => (
                <tr key={r.dimValue} className="orbit-pick cursor-pointer" tabIndex={0}
                  onClick={() => onPick({ name: d.filter, value: r.dimValue, label: `${d.label}: ${valueLabel(dim, r.dimValue)}` })}
                  onKeyDown={e => { if (e.key === 'Enter') onPick({ name: d.filter, value: r.dimValue, label: `${d.label}: ${valueLabel(dim, r.dimValue)}` }); }}>
                  <td className={TD}>
                    <div className="truncate text-gray-200" style={dim.endsWith('version') ? { fontFamily: MONO } : undefined}>{valueLabel(dim, r.dimValue)}</div>
                    {r.dimLabel && <div className="truncate text-[10.5px] text-gray-500" title={r.dimLabel}>{r.dimLabel}</div>}
                    {dim.endsWith('version') && <div className="text-[10.5px] text-gray-600">{day(r.firstAt)} → {day(r.lastAt)}</div>}
                  </td>
                  <td className={`${TD} ${NUM} text-gray-400`} style={{ fontFamily: MONO }}>{r.runs}</td>
                  <td className={`${TD} max-sm:hidden`}>
                    <div className="flex items-center gap-2">
                      <Meter value={r.medianMinutes} max={max} swatch="bg-amber-500" />
                      <span className="w-10 shrink-0 text-right text-gray-100" style={{ fontFamily: MONO }}>{mins(r.medianMinutes)}</span>
                    </div>
                  </td>
                  <td className={`${TD} ${NUM} text-gray-100 sm:hidden`} style={{ fontFamily: MONO }}>{mins(r.medianMinutes)}</td>
                  <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden lg:max-xl:hidden`} style={{ fontFamily: MONO }}>{mins(r.p90Minutes)}</td>
                  <td className={`${TD} ${NUM} text-gray-300 max-sm:hidden`} style={{ fontFamily: MONO }}>{compact(r.medianTokens)}</td>
                  <td className={`${TD} ${NUM} text-gray-300`} style={{ fontFamily: MONO }}>{pct(r.reworkShare)}</td>
                  <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden lg:max-xl:hidden`} style={{ fontFamily: MONO }}>{pct(r.wentBackward / r.runs)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Sheet>
      )}
    </Section>
  );
}

/**
 * Each version of the phase's skill (or of the worker prompt) in the order it was in force, with
 * the change against the version before it. A dot line of medians above the table shows the trend
 * at a glance; the table carries the numbers.
 */
export function Versions({ scope, phase, bump, onPick }: {
  scope: Scope; phase: string; bump: number; onPick: (f: RunFilter) => void;
}) {
  const [kind, setKind] = useState<'skill' | 'prompt'>('skill');
  const { data } = useJson<VersionRow[]>(`/api/analytics/versions?${scopeQuery(scope, { phase, kind })}`, bump);
  const rows = (data ?? []).slice().sort((a, b) => a.versionSeq - b.versionSeq);
  const max = Math.max(1, ...rows.map(r => r.medianMinutes));
  const recorded = rows.some(r => r.source === 'recorded');
  return (
    <Section title="Versions" meta={kind === 'skill' ? 'of this phase\'s skill, oldest first' : 'of the worker prompt, oldest first'}
      right={<div className="flex gap-1.5"><Chip on={kind === 'skill'} onClick={() => setKind('skill')}>Skill</Chip><Chip on={kind === 'prompt'} onClick={() => setKind('prompt')}>Prompt</Chip></div>}>
      {rows.length === 0 ? <Empty>No version history for this phase in this window.</Empty> : (
        <Sheet pad={false} className="orbit-swap" key={kind}>
          {rows.length > 1 && (
            <div className="border-b border-hairsoft px-4 pb-2 pt-3">
              <svg viewBox={`0 0 ${Math.max(1, rows.length - 1) * 40 + 20} 60`} className="h-16 w-full" preserveAspectRatio="none" role="img"
                aria-label={`median minutes per version: ${rows.map(r => mins(r.medianMinutes)).join(', ')}`}>
                <polyline fill="none" stroke="var(--w-accent)" strokeWidth={1.5} vectorEffect="non-scaling-stroke"
                  points={rows.map((r, i) => `${10 + i * 40},${55 - (50 * r.medianMinutes) / max}`).join(' ')} />
                {rows.map((r, i) => (
                  <circle key={r.version} cx={10 + i * 40} cy={55 - (50 * r.medianMinutes) / max} r={Math.min(6, 2 + Math.sqrt(r.runs) / 3)}
                    fill="var(--w-accent)" vectorEffect="non-scaling-stroke"><title>{`${r.version}: ${mins(r.medianMinutes)} median, ${r.runs} runs`}</title></circle>
                ))}
              </svg>
              <div className="flex justify-between text-[10px] text-gray-600"><span>oldest</span><span>median minutes · dot size is runs</span><span>newest</span></div>
            </div>
          )}
          <table className="w-full table-fixed text-xs">
            <thead>
              <tr className="border-b border-hair text-gray-500">
                <th className={`${TH} w-[40%] text-left`}>Version</th>
                <th className={`${TH} ${NUM}`}>Runs</th>
                <th className={`${TH} ${NUM}`}>Median</th>
                <th className={`${TH} ${NUM} max-sm:hidden lg:max-xl:hidden`}>Tokens</th>
                <th className={`${TH} ${NUM} whitespace-nowrap`}>Sent back</th>
                <th className={`${TH} ${NUM} whitespace-nowrap max-sm:hidden`} title="runs that were themselves redoing work">Rework</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-hairsoft">
              {rows.map((r, i) => {
                const prev = rows[i - 1];
                const delta = prev ? r.medianMinutes - prev.medianMinutes : null;
                return (
                  <tr key={r.version} className="orbit-pick cursor-pointer" tabIndex={0}
                    onClick={() => onPick({ name: `${kind}_version`, value: r.version, label: `${kind === 'skill' ? 'Skill' : 'Prompt'} ${r.version}` })}>
                    <td className={TD}>
                      <div className="flex min-w-0 items-baseline gap-2">
                        <span className="shrink-0 text-gray-200" style={{ fontFamily: MONO }}>{r.version}</span>
                        <span className="shrink-0 text-[10.5px] text-gray-600">{day(r.versionAt ?? r.firstAt)}</span>
                      </div>
                      {r.label && <div className="truncate text-[10.5px] text-gray-500" title={r.label}>{r.label}</div>}
                    </td>
                    <td className={`${TD} ${NUM} text-gray-400`} style={{ fontFamily: MONO }}>{r.runs}</td>
                    <td className={`${TD} ${NUM}`} style={{ fontFamily: MONO }}>
                      <span className="text-gray-100">{mins(r.medianMinutes)}</span>
                      {delta !== null && Math.abs(delta) >= 0.1 && (
                        <span className={`block text-[10.5px] ${delta > 0 ? 'text-red-300' : 'text-emerald-300'}`}
                          title="against the version before">{delta > 0 ? '+' : '−'}{mins(Math.abs(delta))}</span>
                      )}
                    </td>
                    <td className={`${TD} ${NUM} text-gray-300 max-sm:hidden lg:max-xl:hidden`} style={{ fontFamily: MONO }}>{compact(r.medianTokens)}</td>
                    <td className={`${TD} ${NUM} text-gray-300`} style={{ fontFamily: MONO }}>{pct(r.sentBackShare)}</td>
                    <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden`} style={{ fontFamily: MONO }}>{pct(r.reworkRunShare)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          <p className="px-4 py-2 text-[10.5px] text-gray-600">
            {recorded ? 'Hashes recorded by the runs themselves, and from git for runs before that.' : 'From git history: the version committed when each run started.'}
            {' '}Sent back: runs that left the ticket in an earlier phase. Rework: runs that were themselves redoing work.
            {' '}A version with few runs is a weak signal.
          </p>
        </Sheet>
      )}
    </Section>
  );
}

/**
 * What the phase's agents wait on: tool by tool, one scale, and a shell row opens into the commands
 * it ran - "Bash" is never the answer, `dotnet build` is.
 */
export function Tools({ scope, phase, bump }: { scope: Scope; phase: string; bump: number }) {
  const { data } = useJson<ToolRow[]>(`/api/analytics/tools?${scopeQuery(scope, { phase })}`, bump);
  const [open, setOpen] = useState<string | null>('Bash');
  const [all, setAll] = useState(false);
  const rows = data ?? [];
  // One line per tool; the per-command rows are folded under their tool.
  const tools = Object.values(rows.reduce<Record<string, ToolRow & { details: ToolRow[]; blank?: ToolRow }>>((acc, r) => {
    const t = acc[r.toolName] ??= { ...r, toolDetail: '', calls: 0, runs: 0, totalSeconds: 0, errorCalls: 0, unansweredCalls: 0, details: [] };
    t.calls += r.calls; t.totalSeconds += r.totalSeconds; t.errorCalls += r.errorCalls; t.unansweredCalls += r.unansweredCalls;
    t.runs = Math.max(t.runs, r.runs);
    if (r.toolDetail) t.details.push(r);
    else { t.medianSeconds = r.medianSeconds; t.p90Seconds = r.p90Seconds; t.blank = r; }
    return acc;
  }, {})).map(t => {
    // A tool with per-command rows has no one median; its calls whose command could not be read
    // are a line of their own beside the others.
    if (t.details.length === 0) return t;
    if (t.blank) t.details.push({ ...t.blank, toolDetail: '(other)' });
    return { ...t, medianSeconds: null, p90Seconds: null };
  }).sort((a, b) => b.totalSeconds - a.totalSeconds);
  const total = tools.reduce((s, t) => s + t.totalSeconds, 0);
  const max = Math.max(1, ...tools.map(t => t.totalSeconds));
  const shown = all ? tools : tools.slice(0, 12);

  const line = (t: ToolRow, sub = false) => (
    <div className={`grid min-w-0 grid-cols-[minmax(0,1fr)_3.2rem_3.2rem] items-center gap-x-3 sm:grid-cols-[minmax(0,14rem)_minmax(0,1fr)_3.5rem_3.5rem_3.5rem_3rem] ${sub ? 'pl-4' : ''}`}>
      <span className={`min-w-0 truncate ${sub ? 'text-gray-400' : 'text-gray-200'}`} style={sub ? { fontFamily: MONO } : undefined}
        title={t.toolDetail || t.toolName}>{sub ? t.toolDetail : t.toolShort}{!sub && t.toolFamily.startsWith('mcp:') && <span className="ml-1.5 text-[10px] text-gray-600">{t.toolFamily.slice(4)}</span>}</span>
      <span className="max-sm:hidden"><Meter value={t.totalSeconds} max={max} swatch={sub ? 'bg-emerald-300' : 'bg-emerald-500'} /></span>
      <span className="text-right text-gray-100" style={{ fontFamily: MONO }} title="total, each call counted in full">{mins(t.totalSeconds / 60)}</span>
      <span className="text-right text-gray-400" style={{ fontFamily: MONO }} title="calls">{compact(t.calls)}</span>
      <span className="text-right text-gray-400 max-sm:hidden" style={{ fontFamily: MONO }} title="median per call">{secs(t.medianSeconds)}</span>
      <span className={`text-right max-sm:hidden ${t.errorCalls ? 'text-red-300' : 'text-gray-600'}`} style={{ fontFamily: MONO }} title="calls that returned an error">{t.errorCalls || '—'}</span>
    </div>
  );

  return (
    <Section title="Tool time" meta={total ? `${mins(total / 60)} across ${compact(tools.reduce((s, t) => s + t.calls, 0))} calls` : undefined}>
      {tools.length === 0 ? <Empty>No transcripts matched to this phase's runs in this window, so there are no tool timings.</Empty> : (
        <Sheet>
          <div className="mb-1.5 grid grid-cols-[minmax(0,1fr)_3.2rem_3.2rem] gap-x-3 text-[10.5px] text-gray-500 sm:grid-cols-[minmax(0,14rem)_minmax(0,1fr)_3.5rem_3.5rem_3.5rem_3rem]">
            <span>Tool</span><span className="max-sm:hidden" /><span className="text-right">Total</span><span className="text-right">Calls</span>
            <span className="text-right max-sm:hidden">Median</span><span className="text-right max-sm:hidden">Errors</span>
          </div>
          <ul className="space-y-1 text-xs">
            {shown.map(t => (
              <li key={t.toolName}>
                {t.details.length > 0 ? (
                  <button className="orbit-pick block w-full text-left" aria-expanded={open === t.toolName}
                    onClick={() => setOpen(open === t.toolName ? null : t.toolName)}>{line(t)}</button>
                ) : line(t)}
                {open === t.toolName && t.details.length > 0 && (
                  <ul className="orbit-swap mt-1 space-y-0.5">
                    {t.details.sort((a, b) => b.totalSeconds - a.totalSeconds).slice(0, 15).map(d => <li key={d.toolDetail}>{line(d, true)}</li>)}
                  </ul>
                )}
              </li>
            ))}
          </ul>
          {tools.length > 12 && (
            <button className="orbit-tap mt-2 text-[11px] text-gray-500 hover:text-gray-300" onClick={() => setAll(!all)}>
              {all ? 'Fewer' : `All ${tools.length} tools`}
            </button>
          )}
          <p className="mt-2 text-[10.5px] text-gray-600">
            Each call counted in full, so calls made in parallel add up past the phase's tool time. A loop is almost always a worker polling for something to finish.
          </p>
        </Sheet>
      )}
    </Section>
  );
}

/** Two questions that only runs recorded with run tracking can answer, side by side. */
export function DiffAndRebase({ phase, diff, rebase, onPick }: {
  phase: string; diff: DiffSizeRow[]; rebase: StartRebaseRow[]; onPick: (f: RunFilter) => void;
}) {
  const d = diff.filter(r => r.phase === phase).sort((a, b) => a.sizeOrder - b.sizeOrder);
  const rb = rebase.filter(r => r.phase === phase).sort((a, b) => b.runs - a.runs);
  const maxD = Math.max(1, ...d.map(r => r.medianMinutes));
  const maxR = Math.max(1, ...rb.map(r => r.medianPrepS ?? 0));
  const note = 'Fills in from runs recorded since the worker started measuring this.';
  return (
    <div className="grid min-w-0 grid-cols-1 gap-6 lg:grid-cols-2">
      <Section title="Duration by size of change" meta="lines changed on the branch">
        {d.length === 0 ? <Empty>{note}</Empty> : (
          <Sheet>
            <ul className="space-y-1.5 text-xs">
              {d.map(r => (
                <li key={r.sizeBucket}>
                  <button className="orbit-pick grid w-full grid-cols-[4.5rem_minmax(0,1fr)_3rem_3rem] items-center gap-x-3 text-left"
                    onClick={() => onPick({ name: 'size_bucket', value: r.sizeBucket, label: `${r.sizeBucket} lines` })}>
                    <span className="text-gray-300" style={{ fontFamily: MONO }}>{r.sizeBucket}</span>
                    <Meter value={r.medianMinutes} max={maxD} swatch="bg-amber-500" title={`p90 ${mins(r.p90Minutes)}`} />
                    <span className="text-right text-gray-100" style={{ fontFamily: MONO }}>{mins(r.medianMinutes)}</span>
                    <span className="text-right text-gray-500" style={{ fontFamily: MONO }} title="runs">{r.runs}</span>
                  </button>
                </li>
              ))}
            </ul>
          </Sheet>
        )}
      </Section>
      <Section title="Start rebase" meta="prep time, by what the rebase found">
        {rb.length === 0 ? <Empty>{note}</Empty> : (
          <Sheet>
            <ul className="space-y-1.5 text-xs">
              {rb.map(r => (
                <li key={r.startRebase}>
                  <button className="orbit-pick grid w-full grid-cols-[4.5rem_minmax(0,1fr)_3rem_3rem] items-center gap-x-3 text-left"
                    onClick={() => onPick({ name: 'start_rebase', value: r.startRebase, label: `Start rebase: ${r.startRebase}` })}
                    title={`median ${r.medianBaseBehind ?? '—'} commits behind, at most ${r.maxBaseBehind ?? '—'}`}>
                    <span className="text-gray-300">{r.startRebase}</span>
                    <Meter value={r.medianPrepS ?? 0} max={maxR} swatch="bg-sky-500" />
                    <span className="text-right text-gray-100" style={{ fontFamily: MONO }}>{secs(r.medianPrepS)}</span>
                    <span className="text-right text-gray-500" style={{ fontFamily: MONO }} title="runs">{r.runs}</span>
                  </button>
                </li>
              ))}
            </ul>
            <p className="mt-2 text-[10.5px] text-gray-600">
              {mins(rb.reduce((s, r) => s + r.totalPrepMinutes, 0))} of prep in all · median prep per run beside each outcome.
            </p>
          </Sheet>
        )}
      </Section>
    </div>
  );
}
