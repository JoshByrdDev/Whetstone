import { useState } from 'react';
import { Chip } from '../ui';
import { MONO } from '../orbit';
import type { DailyRow, FirstPassRow } from './types';
import { Empty, Meter, Section, Sheet, compact, mins, pct } from './parts';

/**
 * First-pass QA: of the tickets QA looked at, the share it never sent back. Over each ticket's whole
 * history, as on the board - the window picks which tickets by when QA first saw them. By week for
 * the trend, and by what built them (the implement run that first handed them to QA).
 */
const BY: { key: string; label: string }[] = [
  { key: 'week', label: 'Week' },
  { key: 'build_model', label: 'Built by model' },
  { key: 'build_effort', label: 'Built at effort' },
  { key: 'build_skill_version', label: 'Implement skill' },
];

export function FirstPassQa({ rows }: { rows: FirstPassRow[] }) {
  const [by, setBy] = useState('week');
  const shown = rows.filter(r => r.dimension === by)
    .sort((a, b) => (by === 'week' ? a.dimValue.localeCompare(b.dimValue) : b.qaAttempted - a.qaAttempted));
  const all = rows.find(r => r.dimension === 'all');
  return (
    <Section title="First-pass QA" meta={all ? `${pct(all.firstPassRate)} of ${all.qaAttempted} tickets` : undefined}>
      <div className="mb-2 flex flex-wrap gap-1.5">
        {BY.map(b => <Chip key={b.key} on={by === b.key} onClick={() => setBy(b.key)}>{b.label}</Chip>)}
      </div>
      {shown.length === 0 ? <Empty>No ticket reached QA in this window.</Empty> : (
        <Sheet className="orbit-swap" key={by}>
          {by === 'week' ? (
            <div>
              <div className="flex h-28 items-end gap-[3px]">
                {shown.map(r => (
                  <div key={r.dimValue} className="flex h-full min-w-[8px] flex-1 flex-col justify-end"
                    title={`Week of ${r.dimValue}: ${r.firstPass} of ${r.qaAttempted} passed first time (${pct(r.firstPassRate)})`}>
                    <div className="rounded-t-[4px] bg-emerald-500" style={{ height: `${Math.max(2, 100 * r.firstPassRate)}%` }} />
                  </div>
                ))}
              </div>
              <div className="mt-1.5 flex justify-between text-[10px] text-gray-600" style={{ fontFamily: MONO }}>
                <span>{shown[0]?.dimValue}</span><span>share passed first time, by week QA first saw it</span><span>{shown[shown.length - 1]?.dimValue}</span>
              </div>
            </div>
          ) : (
            <ul className="space-y-1.5 text-xs">
              {shown.map(r => (
                <li key={r.dimValue} className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)_3rem_3.5rem] items-center gap-x-3">
                  <span className="min-w-0 truncate text-gray-300" style={by.endsWith('version') ? { fontFamily: MONO } : undefined}>{r.dimValue}</span>
                  <Meter value={r.firstPassRate} max={1} swatch="bg-emerald-500" />
                  <span className="text-right text-gray-100" style={{ fontFamily: MONO }}>{pct(r.firstPassRate)}</span>
                  <span className="text-right text-gray-500" style={{ fontFamily: MONO }} title="tickets QA looked at">of {r.qaAttempted}</span>
                </li>
              ))}
            </ul>
          )}
        </Sheet>
      )}
    </Section>
  );
}

/** Each day's worker minutes, the part spent redoing work drawn over it. One series and its subset. */
export function Daily({ rows, phase }: { rows: DailyRow[]; phase: string | null }) {
  const [only, setOnly] = useState(false);
  const days = Object.values(rows.filter(r => !only || !phase || r.phase === phase)
    .reduce<Record<string, { day: string; minutes: number; rework: number; runs: number; tokens: number }>>((acc, r) => {
      const d = acc[r.day] ??= { day: r.day, minutes: 0, rework: 0, runs: 0, tokens: 0 };
      d.minutes += r.minutes; d.rework += r.reworkMinutes; d.runs += r.runs; d.tokens += r.tokens;
      return acc;
    }, {})).sort((a, b) => a.day.localeCompare(b.day));
  const max = Math.max(1, ...days.map(d => d.minutes));
  return (
    <Section title="Worker time by day" meta="rework drawn over the whole"
      right={phase ? <Chip on={only} onClick={() => setOnly(!only)}>Only this phase</Chip> : undefined}>
      {days.length === 0 ? <Empty>No runs in this window.</Empty> : (
        <Sheet>
          <div className="flex h-28 items-end gap-[2px]">
            {days.map(d => (
              <div key={d.day} className="relative flex h-full min-w-[4px] flex-1 flex-col justify-end"
                title={`${d.day}: ${mins(d.minutes)} over ${d.runs} runs, ${mins(d.rework)} of it rework · ${compact(d.tokens)} tokens`}>
                <div className="relative w-full rounded-t-[4px] bg-sky-500" style={{ height: `${Math.max(2, (100 * d.minutes) / max)}%` }}>
                  <div className="absolute bottom-0 w-full bg-red-400" style={{ height: `${d.minutes ? (100 * d.rework) / d.minutes : 0}%` }} />
                </div>
              </div>
            ))}
          </div>
          <div className="mt-1.5 flex flex-wrap justify-between gap-x-4 text-[10px] text-gray-600" style={{ fontFamily: MONO }}>
            <span>{days[0]?.day}</span>
            <span className="inline-flex items-center gap-1.5"><span className="h-2 w-2 rounded-[2px] bg-sky-500" />all · <span className="h-2 w-2 rounded-[2px] bg-red-400" />rework</span>
            <span>{days[days.length - 1]?.day}</span>
          </div>
        </Sheet>
      )}
    </Section>
  );
}
