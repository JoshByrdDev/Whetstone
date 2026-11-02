import { MONO } from '../orbit';
import type { PhaseRow, ReworkRow, TimeSplitRow } from './types';
import {
  Empty, NUM, REASONS, SegBar, Section, Sheet, Swatch, TD, TH, TIME_PARTS, byPipeline, compact, mins, pct,
  phaseName, reasonOf, secs,
} from './parts';

/**
 * The page's spine: every phase on one row, and the two questions asked of all of them at once -
 * where does its wall clock go, and how much of it is doing work again. Picking a phase here is
 * what the detail sections below it are about.
 */

export function PhaseTable({ phases, focus, onFocus }: {
  phases: PhaseRow[]; focus: string | null; onFocus: (p: string) => void;
}) {
  const rows = [...phases].sort((a, b) => byPipeline(a.phase, b.phase));
  const maxTotal = Math.max(1, ...rows.map(r => r.totalMinutes));
  return (
    <Section title="Phases" meta="median per run · pick one to open it below">
      {rows.length === 0 ? <Empty>No runs in this window.</Empty> : (
        <Sheet pad={false}>
          <table className="w-full table-fixed text-xs">
            <thead>
              <tr className="border-b border-hair text-gray-500">
                <th className={`${TH} w-[26%] text-left sm:w-[16%]`}>Phase</th>
                <th className={`${TH} ${NUM}`}>Runs</th>
                <th className={`${TH} ${NUM}`}>Median</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>p90</th>
                <th className={`${TH} w-[22%] text-left max-sm:hidden`}>Worker time</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>Tokens</th>
                <th className={`${TH} ${NUM}`}>Rework</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>Sent back</th>
                <th className={`${TH} ${NUM} max-sm:hidden`}>Unfinished</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-hairsoft">
              {rows.map(r => {
                const unfinished = r.timeouts + r.errors;
                return (
                  <tr key={r.phase} className="orbit-pick cursor-pointer" aria-current={focus === r.phase ? 'true' : undefined}
                    onClick={() => onFocus(r.phase)} tabIndex={0}
                    onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onFocus(r.phase); } }}>
                    <td className={`${TD} truncate font-medium ${focus === r.phase ? 'text-amber-300' : 'text-gray-200'}`}>{phaseName(r.phase)}</td>
                    <td className={`${TD} ${NUM} text-gray-400`} style={{ fontFamily: MONO }}>{r.runs}</td>
                    <td className={`${TD} ${NUM} text-gray-100`} style={{ fontFamily: MONO }}>{mins(r.medianMinutes)}</td>
                    <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden`} style={{ fontFamily: MONO }}>{mins(r.p90Minutes)}</td>
                    <td className={`${TD} max-sm:hidden`}>
                      <div className="flex items-center gap-2">
                        <div className="flex h-3 min-w-0 flex-1 items-center">
                          <div className="h-2 rounded-r-[4px] bg-sky-500" style={{ width: `${Math.max(2, (100 * r.totalMinutes) / maxTotal)}%` }} />
                        </div>
                        <span className="w-10 shrink-0 text-right text-gray-400" style={{ fontFamily: MONO }}>{mins(r.totalMinutes)}</span>
                      </div>
                    </td>
                    <td className={`${TD} ${NUM} text-gray-300 max-sm:hidden`} style={{ fontFamily: MONO }}>{compact(r.medianTokens)}</td>
                    <td className={`${TD} ${NUM} ${(r.reworkShare ?? 0) > 0.15 ? 'text-red-300' : 'text-gray-300'}`} style={{ fontFamily: MONO }}
                      title={`${mins(r.reworkMinutes)} of ${mins(r.totalMinutes)} was redoing work`}>{pct(r.reworkShare)}</td>
                    <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden`} style={{ fontFamily: MONO }}
                      title="runs that left the ticket in an earlier phase than they found it">{pct(r.wentBackward / r.runs)}</td>
                    <td className={`${TD} ${NUM} max-sm:hidden ${unfinished ? 'text-red-300' : 'text-gray-600'}`} style={{ fontFamily: MONO }}
                      title={`${r.timeouts} timed out, ${r.errors} failed`}>{unfinished || '—'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </Sheet>
      )}
    </Section>
  );
}

const partMinutes = (r: TimeSplitRow, key: string): number =>
  (r as unknown as Record<string, number>)[`${key}Minutes`] ?? 0;
const partMedian = (r: TimeSplitRow, key: string): number | null =>
  (r as unknown as Record<string, number | null>)[`median${key[0].toUpperCase()}${key.slice(1)}S`] ?? null;

/**
 * Where the wall clock goes, one bar per phase on one scale, so a long bar is a phase that costs
 * more in total and its colours say on what. The focused phase's legend is the table.
 */
export function TimeSplit({ rows, focus, onFocus }: { rows: TimeSplitRow[]; focus: string | null; onFocus: (p: string) => void }) {
  const sorted = [...rows].sort((a, b) => byPipeline(a.phase, b.phase));
  const max = Math.max(1, ...sorted.map(r => r.totalMinutes));
  const f = sorted.find(r => r.phase === focus) ?? sorted[0];
  const measured = sorted.reduce((s, r) => s + r.runsRecorded + r.runsTranscript, 0);
  const all = sorted.reduce((s, r) => s + r.runs, 0);
  return (
    <Section title="Where the time goes" meta={all ? `${pct(measured / all)} of runs measured` : undefined}>
      {sorted.length === 0 ? <Empty>No runs in this window.</Empty> : (
        <Sheet>
          <div className="space-y-2.5">
            {sorted.map(r => (
              <button key={r.phase} onClick={() => onFocus(r.phase)} aria-current={focus === r.phase ? 'true' : undefined}
                className="orbit-pick flex w-full min-w-0 items-center gap-3 text-left">
                <span className={`w-20 shrink-0 truncate text-xs ${focus === r.phase ? 'text-amber-300' : 'text-gray-300'}`}>{phaseName(r.phase)}</span>
                <div className="min-w-0 flex-1">
                  <SegBar width={r.totalMinutes / max}
                    parts={TIME_PARTS.map(p => ({ ...p, value: partMinutes(r, p.key), title: mins(partMinutes(r, p.key)) }))} />
                </div>
                <span className="w-12 shrink-0 text-right text-[11px] text-gray-400" style={{ fontFamily: MONO }}>{mins(r.totalMinutes)}</span>
              </button>
            ))}
          </div>

          {f && (
            <div className="mt-4 border-t border-hairsoft pt-3">
              <div className="mb-2 text-[11px] text-gray-500">
                {phaseName(f.phase)}: {f.runsRecorded} run{f.runsRecorded === 1 ? '' : 's'} with every stage timed,{' '}
                {f.runsTranscript} split from the transcript alone, {f.runsUnmeasured} unmeasured
              </div>
              <ul className="grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-2">
                {TIME_PARTS.filter(p => partMinutes(f, p.key) > 0).map(p => (
                  <li key={p.key} className="flex min-w-0 items-center gap-2 text-xs" title={p.note}>
                    <Swatch swatch={p.swatch} />
                    <span className="min-w-0 flex-1 truncate text-gray-300">{p.label}</span>
                    <span className="text-gray-500" style={{ fontFamily: MONO }} title="median per run">{secs(partMedian(f, p.key))}</span>
                    <span className="w-12 text-right text-gray-300" style={{ fontFamily: MONO }}>{mins(partMinutes(f, p.key))}</span>
                    <span className="w-10 text-right text-gray-500" style={{ fontFamily: MONO }}>{pct(partMinutes(f, p.key) / f.totalMinutes)}</span>
                  </li>
                ))}
              </ul>
              <p className="mt-2 text-[10.5px] text-gray-600">Columns: median per run · total · share of the phase's time.</p>
            </div>
          )}
        </Sheet>
      )}
    </Section>
  );
}

/**
 * Why each phase's runs happened, as a share of its minutes - the first-time work beside every kind
 * of redo. A cause in the legend narrows the run list to it.
 */
export function Rework({ rows, focus, onPick }: {
  rows: ReworkRow[]; focus: string | null; onPick: (phase: string, reason: string) => void;
}) {
  const phases = [...new Set(rows.map(r => r.phase))].sort(byPipeline);
  const of = (phase: string) => rows.filter(r => r.phase === phase);
  const f = focus && phases.includes(focus) ? focus : phases[0];
  const focusRows = f ? of(f).sort((a, b) => b.minutes - a.minutes) : [];
  const total = focusRows.reduce((s, r) => s + r.minutes, 0);
  return (
    <Section title="Rework by cause" meta="share of each phase's minutes">
      {phases.length === 0 ? <Empty>No runs in this window.</Empty> : (
        <Sheet>
          <div className="space-y-2.5">
            {phases.map(p => (
              <div key={p} className="flex min-w-0 items-center gap-3">
                <span className={`w-20 shrink-0 truncate text-xs ${p === f ? 'text-amber-300' : 'text-gray-300'}`}>{phaseName(p)}</span>
                <div className="min-w-0 flex-1">
                  <SegBar parts={REASONS.map(r => {
                    const row = of(p).find(x => x.entryReason === r.key);
                    return { ...r, value: row?.minutes ?? 0, title: row ? `${mins(row.minutes)}, ${row.runs} runs` : '' };
                  })} />
                </div>
                <span className="w-12 shrink-0 text-right text-[11px] text-gray-400" style={{ fontFamily: MONO }}
                  title="share of minutes that was redoing work">
                  {pct(of(p).filter(r => r.reworkClass === 'rework').reduce((s, r) => s + r.minutes, 0) /
                    Math.max(1e-9, of(p).reduce((s, r) => s + r.minutes, 0)))}
                </span>
              </div>
            ))}
          </div>
          {f && (
            <div className="mt-4 border-t border-hairsoft pt-3">
              <div className="mb-2 text-[11px] text-gray-500">{phaseName(f)}, by why the run happened · pick one to list its runs</div>
              <ul className="grid grid-cols-1 gap-x-6 gap-y-0.5 sm:grid-cols-2">
                {focusRows.map(r => {
                  const meta = reasonOf(r.entryReason);
                  return (
                    <li key={r.entryReason}>
                      <button onClick={() => onPick(f, r.entryReason)} title={meta.note}
                        className="orbit-pick flex w-full min-w-0 items-center gap-2 py-0.5 text-left text-xs">
                        <Swatch swatch={meta.swatch} />
                        <span className="min-w-0 flex-1 truncate text-gray-300">{meta.label}</span>
                        <span className="text-gray-500" style={{ fontFamily: MONO }}>{r.runs}</span>
                        <span className="w-12 text-right text-gray-300" style={{ fontFamily: MONO }}>{mins(r.minutes)}</span>
                        <span className="w-10 text-right text-gray-500" style={{ fontFamily: MONO }}>{pct(total ? r.minutes / total : 0)}</span>
                      </button>
                    </li>
                  );
                })}
              </ul>
              <p className="mt-2 text-[10.5px] text-gray-600">Columns: runs · minutes · share of the phase's minutes.</p>
            </div>
          )}
        </Sheet>
      )}
    </Section>
  );
}
