import { useState } from 'react';
import { Chip } from '../ui';
import { MONO } from '../orbit';
import type { BugLinkRow, BugsView, Scope } from './types';
import { scopeQuery } from './types';
import { Empty, Meter, NUM, Section, Sheet, Stat, TD, TH, day, mins, pct, phaseName, useJson } from './parts';
import { RunDrawer } from './Runs';
import type { RunFilter } from './PhaseDetail';

/**
 * Bugs traced back to what wrote them (docs/bugs-and-experiments-plan.md): how many confirmed bugs
 * there were in the window, how many escaped - filed after their cause was already merged or Done -
 * and what let them through. The causes are listed, each with the run that most likely wrote the
 * blamed lines, which opens like any other run.
 */

const BY: { key: string; label: string; filter?: string }[] = [
  { key: 'missed_by', label: 'Missed by' },
  { key: 'found_by', label: 'Found by' },
  { key: 'written_model', label: 'Written by model', filter: 'model' },
  { key: 'written_effort', label: 'At effort', filter: 'effort' },
  { key: 'written_skill_version', label: 'Skill version', filter: 'skill_version' },
  { key: 'written_experiment_arm', label: 'A/B arm', filter: 'experiment_arm' },
];

const FOUND: Record<string, string> = {
  Owner: 'the owner', LaterTicketQa: "a later ticket's QA", Production: 'production', Steward: 'the steward',
};
const STAGE: Record<string, string> = {
  speculate: 'Speculate', implement: 'Implement', 'qa-test': 'QA', refine: 'Refine', review: 'Review',
};
const label = (dim: string, v: string) =>
  dim === 'found_by' ? FOUND[v] ?? v : dim === 'missed_by' ? STAGE[v] ?? v : v.replace(/^claude-/, '');

export function Bugs({ scope, bump, onPick }: { scope: Scope; bump: number; onPick: (f: RunFilter) => void }) {
  const [by, setBy] = useState('missed_by');
  const [open, setOpen] = useState<number | null>(null);
  const { data } = useJson<BugsView>(`/api/analytics/bugs?${scopeQuery(scope, { dimension: by })}`, bump);
  const s = data?.summary;
  const links = data?.links ?? [];
  const split = data?.split ?? [];
  const max = Math.max(1, ...split.map(r => r.bugs));
  const d = BY.find(b => b.key === by)!;

  if (data && !s?.bugs && links.length === 0) {
    return (
      <Section title="Bugs traced">
        <Empty>No bug has been traced in this window. Mark a ticket as a bug on the board and it is traced back to the tickets and runs that wrote it.</Empty>
      </Section>
    );
  }

  return (
    <Section title="Bugs traced" meta="confirmed causes only · window is when the bug was filed">
      <div className="mb-3 flex flex-wrap gap-3">
        <Stat label="Bugs" value={s ? String(s.bugs) : '—'} muted={!s?.bugs}
          context={s ? `${s.links} confirmed cause${s.links === 1 ? '' : 's'} in ${s.sourceTickets} ticket${s.sourceTickets === 1 ? '' : 's'}` : undefined} />
        <Stat label="Escaped" value={s?.bugs ? pct(s.escapedBugs / s.bugs) : '—'} muted={!s?.bugs}
          context={s ? `${s.escapedBugs} filed after the cause was merged or Done` : undefined} />
        <Stat label="Found after" value={s?.medianDaysToFind != null ? s.medianDaysToFind.toFixed(1) : '—'} unit={s?.medianDaysToFind != null ? 'days' : undefined}
          muted={s?.medianDaysToFind == null} context="median, delivery to bug filed" />
        <Stat label="Fixing them" value={s ? mins(s.fixMinutes) : '—'} muted={!s?.fixMinutes}
          context={s?.pendingLinks ? `${s.pendingLinks} suggested cause${s.pendingLinks === 1 ? '' : 's'} waiting on the owner` : 'worker time on the bug tickets'} />
      </div>

      <div className="mb-2 flex flex-wrap gap-1.5">
        {BY.map(b => <Chip key={b.key} on={by === b.key} onClick={() => setBy(b.key)}>{b.label}</Chip>)}
      </div>
      <div className="space-y-4">
        <Sheet className="orbit-swap" key={by}>
          {split.length === 0 ? <Empty>No confirmed bug in this window.</Empty> : (
            <ul className="space-y-1.5 text-xs">
              {split.map(r => {
                const pickable = d.filter && r.dimValue !== 'unknown';
                const body = (
                  <>
                    <span className="min-w-0 truncate text-gray-300" style={by.endsWith('version') ? { fontFamily: MONO } : undefined}>{label(by, r.dimValue)}</span>
                    <Meter value={r.bugs} max={max} swatch="bg-red-400" />
                    <span className="text-right text-gray-100" style={{ fontFamily: MONO }}>{r.bugs}</span>
                    <span className="text-right text-gray-500" style={{ fontFamily: MONO }} title="escaped">{r.escapedBugs}</span>
                    <span className="text-right text-gray-500" style={{ fontFamily: MONO }} title="worker time fixing them">{mins(r.fixMinutes)}</span>
                  </>
                );
                const grid = 'grid w-full min-w-0 grid-cols-[minmax(0,12rem)_minmax(0,1fr)_2rem_2rem_3rem] items-center gap-x-3 text-left';
                return (
                  <li key={r.dimValue}>
                    {pickable
                      ? <button className={`orbit-pick ${grid}`} title="List the runs"
                          onClick={() => onPick({ name: d.filter!, value: r.dimValue, label: `${d.label}: ${label(by, r.dimValue)}` })}>{body}</button>
                      : <div className={grid}>{body}</div>}
                  </li>
                );
              })}
              <li className="pt-1 text-[10.5px] text-gray-600">Columns: bugs · escaped · time fixing them. A bug with two causes counts under each.</li>
            </ul>
          )}
        </Sheet>
        <LinkList links={links} onOpen={setOpen} />
      </div>
      {open !== null && <RunDrawer runId={open} onClose={() => setOpen(null)} />}
    </Section>
  );
}

function LinkList({ links, onOpen }: { links: BugLinkRow[]; onOpen: (runId: number) => void }) {
  if (links.length === 0) return <Sheet><Empty>No causes traced in this window.</Empty></Sheet>;
  return (
    <Sheet pad={false}>
      <table className="w-full table-fixed text-xs">
        <thead>
          <tr className="border-b border-hair text-gray-500">
            <th className={`${TH} w-[52%] text-left sm:w-[56%]`}>Bug ← cause</th>
            <th className={`${TH} text-left`}>Status</th>
            <th className={`${TH} ${NUM} max-sm:hidden`}>Share</th>
            <th className={`${TH} text-left`}>Written by</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-hairsoft">
          {links.map(l => (
            <tr key={l.linkId}>
              <td className={TD}>
                <div className="flex min-w-0 items-baseline gap-1.5">
                  <span className="shrink-0 text-gray-500" style={{ fontFamily: MONO }}>#{l.bugTicketId}</span>
                  <span className="min-w-0 truncate text-gray-200" title={l.bugTitle ?? ''}>{l.bugTitle}</span>
                </div>
                <div className="flex min-w-0 items-baseline gap-1.5 text-[10.5px] text-gray-500">
                  <span className="shrink-0">←</span>
                  {l.sourceTicketId === null
                    ? <span className="truncate">commits from outside the board</span>
                    : <><span className="shrink-0" style={{ fontFamily: MONO }}>#{l.sourceTicketId}</span><span className="min-w-0 truncate" title={l.sourceTitle ?? ''}>{l.sourceTitle}</span></>}
                </div>
              </td>
              <td className={TD}>
                <div className={l.status === 'Confirmed' ? 'text-gray-200' : l.status === 'Rejected' ? 'text-gray-600 line-through' : 'text-amber-300'}>
                  {l.status === 'Suggested' ? 'suggested' : l.status === 'Confirmed' ? (l.escaped ? 'escaped' : 'confirmed') : 'rejected'}
                </div>
                <div className="truncate text-[10.5px] text-gray-500">
                  {l.status === 'Confirmed' ? `missed by ${STAGE[l.missedBy] ?? l.missedBy}` : day(l.bugCreatedAt)}
                </div>
              </td>
              <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden`} style={{ fontFamily: MONO }}>{pct(l.share)}</td>
              <td className={TD}>
                {l.writtenByRunId === null
                  ? <span className="text-gray-600">{l.sourceTicketId === null ? '—' : 'no run found'}</span>
                  : <button className="orbit-tap min-w-0 max-w-full truncate text-left text-gray-300 underline decoration-gray-600 underline-offset-2"
                      onClick={() => onOpen(l.writtenByRunId!)} title="the latest run whose own commits changed a blamed file">
                      {phaseName(l.writtenPhase)} · {l.writtenModel?.replace(/^claude-/, '') ?? 'run'}
                    </button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Sheet>
  );
}
